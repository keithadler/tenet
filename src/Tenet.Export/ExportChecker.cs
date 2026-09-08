using System.Diagnostics;
using Tenet.Kernel;
using Environment = Tenet.Kernel.Environment;

namespace Tenet.Export;

/// <summary>A declaration that failed to check, with the kernel's message.</summary>
public sealed record CheckFailure(Name Name, string Kind, string Message, TimeSpan Elapsed);

/// <summary>Progress callback data.</summary>
public sealed record CheckProgress(int Index, int Total, Name Current, int Failed, TimeSpan Elapsed);

public sealed class CheckOptions
{
    /// <summary>Only check these declarations (dependencies are added unchecked). Null checks everything.</summary>
    public HashSet<Name>? Only { get; init; }

    /// <summary>Keep going after a failure, adding the failed declaration unchecked so later ones can be checked.</summary>
    public bool ContinueOnError { get; init; } = true;

    /// <summary>Compare the kernel-derived constructors and recursors against what the exporter wrote.</summary>
    public bool CompareInductive { get; init; } = true;

    /// <summary>Declarations slower than this are listed in the result.</summary>
    public TimeSpan SlowThreshold { get; init; } = TimeSpan.FromSeconds(1);

    public Action<CheckProgress>? Progress { get; init; }
}

public sealed class CheckResult
{
    public int Checked { get; internal set; }
    public int Skipped { get; internal set; }
    public List<CheckFailure> Failures { get; } = new();
    public List<(Name Name, TimeSpan Elapsed)> Slow { get; } = new();
    public TimeSpan Elapsed { get; internal set; }
    public Environment Environment { get; internal set; } = new();
    public bool Success => Failures.Count == 0;
}

/// <summary>Replays an export through the kernel, checking every declaration.</summary>
public static class ExportChecker
{
    public static CheckResult Check(ExportFile file, CheckOptions? options = null)
    {
        options ??= new CheckOptions();
        var result = new CheckResult();
        var env = new Environment();
        result.Environment = env;
        var total = Stopwatch.StartNew();
        int index = 0;
        foreach (ExportDecl decl in file.Decls)
        {
            index++;
            bool check = options.Only is null || ShouldCheck(decl, options.Only);
            var sw = Stopwatch.StartNew();
            if (!check)
            {
                AddUnchecked(env, decl);
                result.Skipped++;
            }
            else
            {
                try
                {
                    CheckDecl(env, decl, options.CompareInductive);
                    result.Checked++;
                }
                catch (KernelException e)
                {
                    result.Failures.Add(new CheckFailure(decl.DisplayName, decl.Kind, e.Message, sw.Elapsed));
                    if (!options.ContinueOnError)
                    {
                        break;
                    }
                    RecoverAfterFailure(env, decl);
                }
                sw.Stop();
                if (sw.Elapsed >= options.SlowThreshold)
                {
                    result.Slow.Add((decl.DisplayName, sw.Elapsed));
                }
            }
            options.Progress?.Invoke(new CheckProgress(index, file.Decls.Count, decl.DisplayName, result.Failures.Count, total.Elapsed));
        }
        result.Elapsed = total.Elapsed;
        return result;
    }

    private static bool ShouldCheck(ExportDecl decl, HashSet<Name> only)
    {
        if (decl is ExportInductive ind)
        {
            return ind.Types.Any(t => only.Contains(t.Name)) || ind.Ctors.Any(c => only.Contains(c.Name)) || ind.Recs.Any(r => only.Contains(r.Name));
        }
        if (decl is ExportMutualDefinition m)
        {
            return m.Definitions.Any(d => only.Contains(d.Name));
        }
        return only.Contains(decl.DisplayName);
    }

    /// <summary>After a failure, install the exporter's own view of the declaration so later declarations still resolve.</summary>
    private static void RecoverAfterFailure(Environment env, ExportDecl decl)
    {
        switch (decl)
        {
            case ExportMutualDefinition m:
                if (!m.Definitions.Any(d => env.Contains(d.Name)))
                {
                    AddUnchecked(env, decl);
                }
                break;
            case ExportInductive ind:
                foreach (Name n in ind.Types.Select(t => t.Name).Concat(ind.Ctors.Select(c => c.Name)).Concat(ind.Recs.Select(r => r.Name)))
                {
                    if (env.Contains(n))
                    {
                        return; // partially added; leave the kernel's view in place
                    }
                }
                AddUnchecked(env, decl);
                break;
            default:
                if (!env.Contains(decl.DisplayName))
                {
                    AddUnchecked(env, decl);
                }
                break;
        }
    }

    /// <summary>Add the declaration without checking, using the exporter's metadata verbatim.</summary>
    public static void AddUnchecked(Environment env, ExportDecl decl)
    {
        switch (decl)
        {
            case ExportAxiom a:
                env.AddCore(new AxiomInfo(a.Name, a.LevelParams, a.Type, a.IsUnsafe));
                break;
            case ExportDefinition d:
                env.AddCore(new DefinitionInfo(d.Name, d.LevelParams, d.Type, d.Value, d.Hints, d.Safety, d.All));
                break;
            case ExportTheorem t:
                env.AddCore(new TheoremInfo(t.Name, t.LevelParams, t.Type, t.Value, t.All));
                break;
            case ExportMutualDefinition m:
                foreach (ExportDefinition d in m.Definitions)
                {
                    env.AddCore(new DefinitionInfo(d.Name, d.LevelParams, d.Type, d.Value, d.Hints, d.Safety, d.All));
                }
                break;
            case ExportOpaque o:
                env.AddCore(new OpaqueInfo(o.Name, o.LevelParams, o.Type, o.Value, o.IsUnsafe, o.All));
                break;
            case ExportQuot q:
                env.AddCore(new QuotInfo(q.Name, q.LevelParams, q.Type, q.QuotKind));
                if (q.QuotKind == QuotKind.Ind)
                {
                    env.MarkQuotInitialized();
                }
                break;
            case ExportInductive ind:
                foreach (ExportInductiveVal t in ind.Types)
                {
                    env.AddCore(new InductiveInfo(t.Name, t.LevelParams, t.Type, t.NumParams, t.NumIndices, t.All, t.Ctors, t.NumNested, t.IsRec, t.IsUnsafe, t.IsReflexive));
                }
                foreach (ExportConstructorVal c in ind.Ctors)
                {
                    env.AddCore(new ConstructorInfo(c.Name, c.LevelParams, c.Type, c.Induct, c.Cidx, c.NumParams, c.NumFields, c.IsUnsafe));
                }
                foreach (ExportRecursorVal r in ind.Recs)
                {
                    env.AddCore(new RecursorInfo(r.Name, r.LevelParams, r.Type, r.All, r.NumParams, r.NumIndices, r.NumMotives, r.NumMinors,
                        r.Rules.Select(x => new RecursorRule(x.Ctor, x.NumFields, x.Rhs)).ToArray(), r.K, r.IsUnsafe));
                }
                break;
        }
    }

    /// <summary>Check one exported declaration against <paramref name="env"/> and add it.</summary>
    public static void CheckDecl(Environment env, ExportDecl decl, bool compareInductive = true)
    {
        switch (decl)
        {
            case ExportAxiom a:
                env.Add(new AxiomDecl(a.Name, a.LevelParams, a.Type, a.IsUnsafe));
                break;
            case ExportDefinition d:
                env.Add(new DefinitionDecl(d.Name, d.LevelParams, d.Type, d.Value, d.Hints, d.Safety, d.All));
                break;
            case ExportTheorem t:
                env.Add(new TheoremDecl(t.Name, t.LevelParams, t.Type, t.Value, t.All));
                break;
            case ExportMutualDefinition m:
                env.Add(new MutualDefinitionDecl(m.Definitions.Select(d => new DefinitionDecl(d.Name, d.LevelParams, d.Type, d.Value, d.Hints, d.Safety, d.All)).ToArray()));
                break;
            case ExportOpaque o:
                env.Add(new OpaqueDecl(o.Name, o.LevelParams, o.Type, o.Value, o.IsUnsafe, o.All));
                break;
            case ExportQuot q:
                env.Add(new QuotDecl());
                CompareQuot(env, q);
                break;
            case ExportInductive ind:
                env.Add(ToInductiveDecl(ind));
                if (compareInductive)
                {
                    CompareInductive(env, ind);
                }
                break;
            default:
                throw new KernelException("unknown exported declaration kind " + decl.Kind);
        }
    }

    /// <summary>Rebuild the declaration Lean's elaborator handed to the kernel: types and constructors only.</summary>
    public static InductiveDecl ToInductiveDecl(ExportInductive ind)
    {
        if (ind.Types.Length == 0)
        {
            throw new KernelException("exported inductive block has no types");
        }
        ExportInductiveVal first = ind.Types[0];
        var types = new InductiveType[ind.Types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            ExportInductiveVal t = ind.Types[i];
            var ctors = new Constructor[t.Ctors.Length];
            for (int j = 0; j < ctors.Length; j++)
            {
                Name cname = t.Ctors[j];
                ExportConstructorVal c = ind.Ctors.FirstOrDefault(x => x.Name.Equals(cname))
                    ?? throw new KernelException($"exported inductive '{t.Name}' lists constructor '{cname}' but the block does not define it");
                ctors[j] = new Constructor(c.Name, c.Type);
            }
            types[i] = new InductiveType(t.Name, t.Type, ctors);
        }
        return new InductiveDecl(first.LevelParams, first.NumParams, types, first.IsUnsafe);
    }

    private static void CompareQuot(Environment env, ExportQuot q)
    {
        if (env.Get(q.Name) is not QuotInfo info)
        {
            throw new KernelException($"'{q.Name}' is not a quotient constant in the kernel's environment");
        }
        Require(info.Kind == q.QuotKind, q.Name, "quotient kind", q.QuotKind, info.Kind);
        Require(Name.ListEquals(info.LevelParams, q.LevelParams), q.Name, "universe parameters", Fmt(q.LevelParams), Fmt(info.LevelParams));
        Require(info.Type.Equals(q.Type), q.Name, "type", q.Type, info.Type);
    }

    /// <summary>The exporter's metadata must agree with what the kernel derived, field by field.</summary>
    private static void CompareInductive(Environment env, ExportInductive ind)
    {
        foreach (ExportInductiveVal t in ind.Types)
        {
            if (env.Get(t.Name) is not InductiveInfo k)
            {
                throw new KernelException($"'{t.Name}' is not an inductive type in the kernel's environment");
            }
            Require(Name.ListEquals(k.LevelParams, t.LevelParams), t.Name, "universe parameters", Fmt(t.LevelParams), Fmt(k.LevelParams));
            Require(k.Type.Equals(t.Type), t.Name, "type", t.Type, k.Type);
            Require(k.NumParams == t.NumParams, t.Name, "numParams", t.NumParams, k.NumParams);
            Require(k.NumIndices == t.NumIndices, t.Name, "numIndices", t.NumIndices, k.NumIndices);
            Require(Name.ListEquals(k.All, t.All), t.Name, "all", Fmt(t.All), Fmt(k.All));
            Require(Name.ListEquals(k.Ctors, t.Ctors), t.Name, "ctors", Fmt(t.Ctors), Fmt(k.Ctors));
            Require(k.NumNested == t.NumNested, t.Name, "numNested", t.NumNested, k.NumNested);
            Require(k.IsRec == t.IsRec, t.Name, "isRec", t.IsRec, k.IsRec);
            Require(k.IsUnsafe == t.IsUnsafe, t.Name, "isUnsafe", t.IsUnsafe, k.IsUnsafe);
            Require(k.IsReflexive == t.IsReflexive, t.Name, "isReflexive", t.IsReflexive, k.IsReflexive);
        }
        foreach (ExportConstructorVal c in ind.Ctors)
        {
            if (env.Get(c.Name) is not ConstructorInfo k)
            {
                throw new KernelException($"'{c.Name}' is not a constructor in the kernel's environment");
            }
            Require(Name.ListEquals(k.LevelParams, c.LevelParams), c.Name, "universe parameters", Fmt(c.LevelParams), Fmt(k.LevelParams));
            Require(k.Type.Equals(c.Type), c.Name, "type", c.Type, k.Type);
            Require(k.Induct.Equals(c.Induct), c.Name, "induct", c.Induct, k.Induct);
            Require(k.Cidx == c.Cidx, c.Name, "cidx", c.Cidx, k.Cidx);
            Require(k.NumParams == c.NumParams, c.Name, "numParams", c.NumParams, k.NumParams);
            Require(k.NumFields == c.NumFields, c.Name, "numFields", c.NumFields, k.NumFields);
            Require(k.IsUnsafe == c.IsUnsafe, c.Name, "isUnsafe", c.IsUnsafe, k.IsUnsafe);
        }
        foreach (ExportRecursorVal r in ind.Recs)
        {
            if (env.Get(r.Name) is not RecursorInfo k)
            {
                throw new KernelException($"'{r.Name}' is not a recursor in the kernel's environment");
            }
            Require(Name.ListEquals(k.LevelParams, r.LevelParams), r.Name, "universe parameters", Fmt(r.LevelParams), Fmt(k.LevelParams));
            Require(k.Type.Equals(r.Type), r.Name, "type", r.Type, k.Type);
            Require(Name.ListEquals(k.All, r.All), r.Name, "all", Fmt(r.All), Fmt(k.All));
            Require(k.NumParams == r.NumParams, r.Name, "numParams", r.NumParams, k.NumParams);
            Require(k.NumIndices == r.NumIndices, r.Name, "numIndices", r.NumIndices, k.NumIndices);
            Require(k.NumMotives == r.NumMotives, r.Name, "numMotives", r.NumMotives, k.NumMotives);
            Require(k.NumMinors == r.NumMinors, r.Name, "numMinors", r.NumMinors, k.NumMinors);
            Require(k.K == r.K, r.Name, "k", r.K, k.K);
            Require(k.IsUnsafe == r.IsUnsafe, r.Name, "isUnsafe", r.IsUnsafe, k.IsUnsafe);
            Require(k.Rules.Length == r.Rules.Length, r.Name, "number of rules", r.Rules.Length, k.Rules.Length);
            for (int i = 0; i < r.Rules.Length; i++)
            {
                ExportRecursorRule er = r.Rules[i];
                RecursorRule kr = k.Rules[i];
                Require(kr.Ctor.Equals(er.Ctor), r.Name, $"rule {i} constructor", er.Ctor, kr.Ctor);
                Require(kr.NumFields == er.NumFields, r.Name, $"rule {i} nfields", er.NumFields, kr.NumFields);
                Require(kr.Rhs.Equals(er.Rhs), r.Name, $"rule {i} rhs", er.Rhs, kr.Rhs);
            }
        }
        // The kernel must not have produced constants the exporter does not know about (or vice versa).
        var exported = new HashSet<Name>(ind.Types.Select(t => t.Name).Concat(ind.Ctors.Select(c => c.Name)).Concat(ind.Recs.Select(r => r.Name)));
        foreach (Name n in ind.Types.SelectMany(t => t.All))
        {
            if (!exported.Contains(n) && ind.Types.Length > 0 && env.Find(n) is InductiveInfo)
            {
                throw new KernelException($"'{ind.Types[0].Name}': the block's 'all' field names '{n}', which the export does not define");
            }
        }
    }

    private static void Require(bool ok, Name name, string field, object exported, object derived)
    {
        if (!ok)
        {
            throw new KernelException($"'{name}': exported {field} does not match the kernel's\n  exported: {exported}\n  kernel:   {derived}");
        }
    }

    private static string Fmt(Name[] ns) => "[" + string.Join(", ", ns.Select(n => n.ToString())) + "]";
}
