using Tenet.Export;
using Tenet.Kernel;
using Tenet.Olean;
using Xunit;
using Environment = Tenet.Kernel.Environment;

namespace Tenet.Tests;

public class OleanTests
{
    /// <summary>The toolchain's compiled core library, if a matching elan toolchain is installed.</summary>
    internal static string? ToolchainLib()
    {
        string? env = System.Environment.GetEnvironmentVariable("TENET_LEAN_LIB");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
        {
            return env;
        }
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        string toolchains = Path.Combine(home, ".elan", "toolchains");
        if (!Directory.Exists(toolchains))
        {
            return null;
        }
        // prefer the toolchain that produced the fixture exports
        foreach (string dir in Directory.GetDirectories(toolchains).OrderByDescending(d => d.Contains("4.34.0-rc2", StringComparison.Ordinal)))
        {
            string lib = Path.Combine(dir, "lib", "lean");
            if (File.Exists(Path.Combine(lib, "Init", "Prelude.olean")))
            {
                return lib;
            }
        }
        return null;
    }

    private static string? Export(string name)
    {
        string? dir = System.Environment.GetEnvironmentVariable("TENET_EXPORTS");
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }
        string p = Path.Combine(dir, name);
        return File.Exists(p) ? p : null;
    }

    private static string Trunc(string s) => s.Length > 300 ? s[..300] + "…" : s;

    /// <summary>Describe the first structurally different subterm of two expressions.</summary>
    internal static string FirstDifference(Expr a, Expr b, string path)
    {
        if (a.Equals(b))
        {
            return "(equal)";
        }
        if (a is LetExpr && b is LetExpr)
        {
            // fall through to the field-wise comparison, which ignores nonDep
        }
        else if (a.Kind != b.Kind)
        {
            return $"{path}: kinds {a.Kind} vs {b.Kind}\n  export: {Trunc(a.ToString())}\n  olean:  {Trunc(b.ToString())}";
        }
        switch (a)
        {
            case AppExpr x when b is AppExpr y:
                {
                    string f = FirstDifference(x.Fn, y.Fn, path + "/fn");
                    return f != "(equal)" ? f : FirstDifference(x.Arg, y.Arg, path + "/arg");
                }
            case BindingExpr x when b is BindingExpr y:
                {
                    string d = FirstDifference(x.Domain, y.Domain, path + "/domain");
                    return d != "(equal)" ? d : FirstDifference(x.Body, y.Body, path + "/body");
                }
            case LetExpr x when b is LetExpr y:
                {
                    // nonDep is ignored here (see the caller)
                    string t = FirstDifference(x.Type, y.Type, path + "/type");
                    if (t != "(equal)") return t;
                    string v = FirstDifference(x.Value, y.Value, path + "/value");
                    if (v != "(equal)") return v;
                    return FirstDifference(x.Body, y.Body, path + "/body");
                }
            case ProjExpr x when b is ProjExpr y:
                return x.Idx != y.Idx || !x.StructName.Equals(y.StructName) ? $"{path}: proj {x.StructName}.{x.Idx} vs {y.StructName}.{y.Idx}" : FirstDifference(x.Struct, y.Struct, path + "/struct");
            case ConstExpr or SortExpr or BVarExpr or LitExpr or FVarExpr:
                return a.Equals(b) ? "(equal)" : $"{path}: {a.Kind}\n  export: {Trunc(a.ToString())}\n  olean:  {Trunc(b.ToString())}";
            default:
                return $"{path}: {a.Kind}\n  export: {Trunc(a.ToString())}\n  olean:  {Trunc(b.ToString())}";
        }
    }

    [Fact]
    public void PreludeHeaderAndImports()
    {
        string? lib = ToolchainLib();
        if (lib is null)
        {
            return;
        }
        using var m = new OleanModule(Path.Combine(lib, "Init", "Prelude.olean"));
        Assert.Equal(2, m.FormatVersion);
        Assert.StartsWith("4.", m.LeanVersion, StringComparison.Ordinal);
        Assert.Equal(40, m.GitHash.Length);
        Assert.Empty(m.Imports);
        Assert.True(m.ConstantNames.Count > 1000);
        Assert.Contains(Name.Of("Nat", "rec"), m.ConstantNames);
        using var core = new OleanModule(Path.Combine(lib, "Init", "Core.olean"));
        Assert.Contains(core.Imports, i => i.Module.Equals(Name.Of("Init", "SizeOf")));
    }

    [Fact]
    public void PreludeDecodesToTheSameConstantsAsTheExport()
    {
        string? lib = ToolchainLib();
        string? export = Export("Init.Prelude.ndjson");
        if (lib is null || export is null)
        {
            return;
        }
        ExportFile file = NdjsonReader.ReadFile(export);
        var exported = new Environment();
        foreach (ExportDecl d in file.Decls)
        {
            ExportChecker.AddUnchecked(exported, d);
        }
        using var m = new OleanModule(Path.Combine(lib, "Init", "Prelude.olean"));
        int compared = 0;
        foreach (ConstantInfo c in m.DecodeAll())
        {
            ConstantInfo? e = exported.Find(c.Name);
            if (e is null)
            {
                continue; // the export omits unsafe declarations and some auxiliary ones
            }
            compared++;
            Assert.Equal(e.KindName, c.KindName);
            Assert.True(Name.ListEquals(e.LevelParams, c.LevelParams), c.Name.ToString());
            Assert.True(FirstDifference(e.Type, c.Type, "") == "(equal)", $"type of {c.Name}: {FirstDifference(e.Type, c.Type, "")}");
            if (e.Value is not null)
            {
                // lean4export writes `nondep: false` for every let; Lean's stored flag is authoritative, so compare modulo it
                string diff = FirstDifference(e.Value, c.Value!, "");
                Assert.True(diff == "(equal)", $"value of {c.Name}\n first difference: {diff}");
            }
            if (e is RecursorInfo er)
            {
                var cr = (RecursorInfo)c;
                Assert.Equal(er.Rules.Length, cr.Rules.Length);
                for (int i = 0; i < er.Rules.Length; i++)
                {
                    Assert.True(FirstDifference(er.Rules[i].Rhs, cr.Rules[i].Rhs, "") == "(equal)", "rule of " + c.Name);
                }
                Assert.Equal(er.K, cr.K);
            }
            if (e is InductiveInfo ei)
            {
                var ci = (InductiveInfo)c;
                Assert.Equal((ei.NumParams, ei.NumIndices, ei.NumNested, ei.IsRec, ei.IsReflexive), (ci.NumParams, ci.NumIndices, ci.NumNested, ci.IsRec, ci.IsReflexive));
            }
            if (e is DefinitionInfo ed)
            {
                var cd = (DefinitionInfo)c;
                Assert.Equal(ed.Hints, cd.Hints);
                Assert.Equal(ed.Safety, cd.Safety);
            }
        }
        Assert.True(compared > 1500, $"only {compared} constants compared");
    }
}

public class OleanCheckerTests
{
    [Fact]
    public void PreludeChecksInPlace()
    {
        string? lib = OleanTests.ToolchainLib();
        if (lib is null)
        {
            return;
        }
        var search = new LeanSearchPath();
        search.Add(lib);
        using var checker = new OleanChecker(search);
        Name prelude = Name.Of("Init", "Prelude");
        checker.Load([(prelude, Path.Combine(lib, "Init", "Prelude.olean"))]);
        OleanCheckResult r = checker.Check([prelude], new OleanCheckOptions { Jobs = 4 });
        Assert.True(r.Success, string.Join("\n", r.Failures.Take(5).Select(f => f.Name + ": " + f.Message)));
        Assert.True(r.Checked > 1900, $"checked {r.Checked}");
        Assert.Equal(1, r.ModulesChecked);
    }

    [Fact]
    public void CoreChecksWithImportsResolvedLazily()
    {
        string? lib = OleanTests.ToolchainLib();
        if (lib is null)
        {
            return;
        }
        var search = new LeanSearchPath();
        search.Add(lib);
        using var checker = new OleanChecker(search);
        Name core = Name.Of("Init", "Core");
        checker.Load([(core, Path.Combine(lib, "Init", "Core.olean"))]);
        Assert.True(checker.Modules.Count > 1, "imports should be mapped");
        OleanCheckResult r = checker.Check([core], new OleanCheckOptions { Jobs = 4 });
        Assert.True(r.Success, string.Join("\n", r.Failures.Take(5).Select(f => f.Name + ": " + f.Message)));
        Assert.Equal(1, r.ModulesChecked);
        Assert.True(r.ModulesLoaded > 1);
    }

    [Fact]
    public void ATamperedConstantIsRejectedInPlace()
    {
        string? lib = OleanTests.ToolchainLib();
        if (lib is null)
        {
            return;
        }
        // Decode Prelude, swap two theorem proofs, and replay the units against a resolver-backed environment.
        using var m = new OleanModule(Path.Combine(lib, "Init", "Prelude.olean"));
        var constants = m.DecodeAll().ToList();
        var thms = constants.OfType<TheoremInfo>().Take(2).ToList();
        Assert.Equal(2, thms.Count);
        var tampered = new TheoremInfo(thms[0].Name, thms[0].LevelParams, thms[0].Type, thms[1].Value, thms[0].All);
        var env = new Tenet.Kernel.Environment();
        env.SetResolver(n => n.Equals(tampered.Name) ? tampered : m.FindConstant(n));
        env.MarkQuotInitialized();
        Assert.Throws<KernelException>(() => Replay.CheckUnit(env, new Replay.Unit(tampered.Name, "theorem", [tampered]), installed: true));
    }
}
