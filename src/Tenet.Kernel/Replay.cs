namespace Tenet.Kernel;

/// <summary>
/// Re-checking constants that already carry the kernel's derived metadata (as found in exports and <c>.olean</c>
/// files): the constants are grouped into units, each unit is re-derived from what Lean's elaborator would have given
/// the kernel, and the result is compared field by field with what was stored.
/// </summary>
public static class Replay
{
    /// <summary>A checkable unit: one constant, an inductive block (types, constructors, recursors), or the quotient block.</summary>
    public sealed class Unit
    {
        public Name Name { get; }
        public string Kind { get; }
        public IReadOnlyList<ConstantInfo> Constants { get; }
        public Unit(Name name, string kind, IReadOnlyList<ConstantInfo> constants)
        {
            Name = name;
            Kind = kind;
            Constants = constants;
        }
        public IEnumerable<Name> Names => Constants.Select(c => c.Name);

        /// <summary>
        /// True when this unit is a helper of Lean's old code generator (present up to about Lean 4.20, gone since).
        /// Those helpers reference constants such as <c>_neutral</c> that the generator added straight to the kernel
        /// environment without storing them, so they cannot be checked from module data by any kernel. Lean's own
        /// <c>Environment.replay</c> never meets them because it skips every unsafe constant; Tenet checks unsafe
        /// constants, so it has to recognize these by name, exactly as Lean's <c>looksLikeOldCodegenName</c> does.
        /// </summary>
        public bool IsOldCodegenHelper => Constants.Any(c => LooksLikeOldCodegenName(c.Name));
    }

    /// <summary>Lean's <c>looksLikeOldCodegenName</c>: a helper the old code generator emitted.</summary>
    public static bool LooksLikeOldCodegenName(Name n) =>
        n.LastString is string s && (s.StartsWith("_cstage", StringComparison.Ordinal)
                                     || s.StartsWith("_spec_", StringComparison.Ordinal)
                                     || s.StartsWith("_elambda", StringComparison.Ordinal));

    /// <summary>
    /// Group constants into units. Constructors and recursors join the block of their inductive type; the four quotient
    /// constants form one unit; everything else is a unit of its own. Order follows the first constant of each unit.
    /// </summary>
    public static List<Unit> GroupUnits(IEnumerable<ConstantInfo> constants)
    {
        var all = constants.ToList();
        var byName = new Dictionary<Name, ConstantInfo>();
        foreach (ConstantInfo c in all)
        {
            byName[c.Name] = c;
        }
        var units = new List<Unit>();
        var done = new HashSet<Name>();
        List<ConstantInfo>? quot = null;
        foreach (ConstantInfo c in all)
        {
            if (done.Contains(c.Name))
            {
                continue;
            }
            switch (c)
            {
                case QuotInfo:
                    quot ??= new List<ConstantInfo>();
                    quot.Add(c);
                    done.Add(c.Name);
                    if (quot.Count == 1)
                    {
                        units.Add(new Unit(Quot.QuotName, "quot", quot));
                    }
                    break;
                case InductiveInfo ind:
                    {
                        var members = new List<ConstantInfo>();
                        foreach (Name t in ind.All)
                        {
                            if (byName.TryGetValue(t, out ConstantInfo? tc) && tc is InductiveInfo ti)
                            {
                                members.Add(ti);
                                done.Add(t);
                                foreach (Name ctor in ti.Ctors)
                                {
                                    if (byName.TryGetValue(ctor, out ConstantInfo? cc) && cc is ConstructorInfo)
                                    {
                                        members.Add(cc);
                                        done.Add(ctor);
                                    }
                                }
                            }
                        }
                        foreach (ConstantInfo r in all)
                        {
                            if (r is RecursorInfo rec && !done.Contains(r.Name) && Name.ListEquals(rec.All, ind.All))
                            {
                                members.Add(rec);
                                done.Add(rec.Name);
                            }
                        }
                        units.Add(new Unit(ind.All.Length > 0 ? ind.All[0] : ind.Name, "inductive", members));
                        break;
                    }
                case DefinitionInfo { Safety: not DefinitionSafety.Safe } def:
                    {
                        // Lean adds unsafe and partial definitions as a mutual block (they may refer to themselves);
                        // replay the block the same way so the kernel sees them under the right safety level.
                        var members = new List<ConstantInfo>();
                        foreach (Name n in def.All.Length > 0 ? def.All : [def.Name])
                        {
                            if (byName.TryGetValue(n, out ConstantInfo? mc) && mc is DefinitionInfo md && md.Safety == def.Safety && !done.Contains(n))
                            {
                                members.Add(md);
                                done.Add(n);
                            }
                        }
                        if (!done.Contains(def.Name))
                        {
                            members.Add(def);
                            done.Add(def.Name);
                        }
                        units.Add(new Unit(members[0].Name, "mutual def", members));
                        break;
                    }
                case ConstructorInfo or RecursorInfo:
                    // reached before its inductive type: defer until the type shows up, or emit alone if it never does
                    if (!byName.ContainsKey(c is ConstructorInfo ctor2 ? ctor2.Induct : ((RecursorInfo)c).All.FirstOrDefault() ?? Name.Anonymous))
                    {
                        units.Add(new Unit(c.Name, c.KindName, [c]));
                        done.Add(c.Name);
                    }
                    break;
                default:
                    units.Add(new Unit(c.Name, c.KindName, [c]));
                    done.Add(c.Name);
                    break;
            }
        }
        // constructors/recursors whose type appeared later in the list were skipped above; they were added with the type
        foreach (ConstantInfo c in all)
        {
            if (!done.Contains(c.Name))
            {
                units.Add(new Unit(c.Name, c.KindName, [c]));
                done.Add(c.Name);
            }
        }
        return units;
    }

    /// <summary>The declaration Lean's elaborator handed to the kernel for this block: types and constructors only.</summary>
    public static InductiveDecl ToInductiveDecl(IReadOnlyList<ConstantInfo> block)
    {
        var types = block.OfType<InductiveInfo>().ToList();
        if (types.Count == 0)
        {
            throw new KernelException("inductive block has no types");
        }
        var ctors = block.OfType<ConstructorInfo>().ToDictionary(c => c.Name);
        var decls = new InductiveType[types.Count];
        for (int i = 0; i < types.Count; i++)
        {
            InductiveInfo t = types[i];
            var cs = new Constructor[t.Ctors.Length];
            for (int j = 0; j < cs.Length; j++)
            {
                if (!ctors.TryGetValue(t.Ctors[j], out ConstructorInfo? c))
                {
                    throw new KernelException($"inductive '{t.Name}' lists constructor '{t.Ctors[j]}' but the block does not define it");
                }
                cs[j] = new Constructor(c.Name, c.Type);
            }
            decls[i] = new InductiveType(t.Name, t.Type, cs);
        }
        return new InductiveDecl(types[0].LevelParams, types[0].NumParams, decls, types[0].IsUnsafe);
    }

    /// <summary>
    /// Check one unit against <paramref name="env"/>. When <paramref name="installed"/> is true the unit's constants are
    /// already present (unchecked) and blocks are re-derived in a child environment that hides them; otherwise the
    /// unit is added to <paramref name="env"/>.
    /// </summary>
    public static void CheckUnit(Environment env, Unit unit, bool installed, bool compare = true)
    {
        switch (unit.Kind)
        {
            case "quot":
                {
                    Environment target = installed ? env.CreateChild(unit.Names) : env;
                    target.Add(new QuotDecl());
                    foreach (ConstantInfo q in unit.Constants)
                    {
                        Compare(q, target.Get(q.Name));
                    }
                    break;
                }
            case "inductive":
                {
                    Environment target = installed ? env.CreateChild(unit.Names) : env;
                    target.Add(ToInductiveDecl(unit.Constants));
                    if (compare)
                    {
                        foreach (ConstantInfo c in unit.Constants)
                        {
                            Compare(c, target.Get(c.Name));
                        }
                    }
                    break;
                }
            case "mutual def":
                {
                    var block = new MutualDefinitionDecl(unit.Constants.Cast<DefinitionInfo>()
                        .Select(d => new DefinitionDecl(d.Name, d.LevelParams, d.Type, d.Value, d.Hints, d.Safety, d.All)).ToArray());
                    if (installed)
                    {
                        env.Validate(block);
                    }
                    else
                    {
                        env.Add(block);
                    }
                    break;
                }
            default:
                {
                    Declaration d = ToDeclaration(unit.Constants[0]);
                    if (installed)
                    {
                        env.Validate(d);
                    }
                    else
                    {
                        env.Add(d);
                    }
                    break;
                }
        }
    }

    /// <summary>The declaration a stand-alone constant came from.</summary>
    public static Declaration ToDeclaration(ConstantInfo c) => c switch
    {
        AxiomInfo a => new AxiomDecl(a.Name, a.LevelParams, a.Type, a.IsUnsafe),
        DefinitionInfo d => new DefinitionDecl(d.Name, d.LevelParams, d.Type, d.Value, d.Hints, d.Safety, d.All),
        TheoremInfo t => new TheoremDecl(t.Name, t.LevelParams, t.Type, t.Value, t.All),
        OpaqueInfo o => new OpaqueDecl(o.Name, o.LevelParams, o.Type, o.OpaqueValue, o.IsUnsafe, o.All),
        ConstructorInfo => throw new KernelException($"constructor '{c.Name}' without its inductive type"),
        RecursorInfo => throw new KernelException($"recursor '{c.Name}' without its inductive type"),
        _ => throw new KernelException($"cannot replay '{c.Name}' ({c.KindName}) on its own"),
    };

    /// <summary>Every stored field must agree with what the kernel derived.</summary>
    public static void Compare(ConstantInfo stored, ConstantInfo derived)
    {
        Name n = stored.Name;
        Require(stored.KindName == derived.KindName, n, "kind", stored.KindName, derived.KindName);
        Require(Name.ListEquals(stored.LevelParams, derived.LevelParams), n, "universe parameters", Fmt(stored.LevelParams), Fmt(derived.LevelParams));
        Require(stored.Type.Equals(derived.Type), n, "type", stored.Type, derived.Type);
        switch (stored)
        {
            case QuotInfo q:
                Require(q.Kind == ((QuotInfo)derived).Kind, n, "quotient kind", q.Kind, ((QuotInfo)derived).Kind);
                break;
            case InductiveInfo t:
                {
                    var k = (InductiveInfo)derived;
                    Require(k.NumParams == t.NumParams, n, "numParams", t.NumParams, k.NumParams);
                    Require(k.NumIndices == t.NumIndices, n, "numIndices", t.NumIndices, k.NumIndices);
                    Require(Name.ListEquals(k.All, t.All), n, "all", Fmt(t.All), Fmt(k.All));
                    Require(Name.ListEquals(k.Ctors, t.Ctors), n, "ctors", Fmt(t.Ctors), Fmt(k.Ctors));
                    Require(k.NumNested == t.NumNested, n, "numNested", t.NumNested, k.NumNested);
                    Require(k.IsRec == t.IsRec, n, "isRec", t.IsRec, k.IsRec);
                    Require(k.IsUnsafe == t.IsUnsafe, n, "isUnsafe", t.IsUnsafe, k.IsUnsafe);
                    Require(k.IsReflexive == t.IsReflexive, n, "isReflexive", t.IsReflexive, k.IsReflexive);
                    break;
                }
            case ConstructorInfo c:
                {
                    var k = (ConstructorInfo)derived;
                    Require(k.Induct.Equals(c.Induct), n, "induct", c.Induct, k.Induct);
                    Require(k.Cidx == c.Cidx, n, "cidx", c.Cidx, k.Cidx);
                    Require(k.NumParams == c.NumParams, n, "numParams", c.NumParams, k.NumParams);
                    Require(k.NumFields == c.NumFields, n, "numFields", c.NumFields, k.NumFields);
                    Require(k.IsUnsafe == c.IsUnsafe, n, "isUnsafe", c.IsUnsafe, k.IsUnsafe);
                    break;
                }
            case RecursorInfo r:
                {
                    var k = (RecursorInfo)derived;
                    Require(Name.ListEquals(k.All, r.All), n, "all", Fmt(r.All), Fmt(k.All));
                    Require(k.NumParams == r.NumParams, n, "numParams", r.NumParams, k.NumParams);
                    Require(k.NumIndices == r.NumIndices, n, "numIndices", r.NumIndices, k.NumIndices);
                    Require(k.NumMotives == r.NumMotives, n, "numMotives", r.NumMotives, k.NumMotives);
                    Require(k.NumMinors == r.NumMinors, n, "numMinors", r.NumMinors, k.NumMinors);
                    Require(k.K == r.K, n, "k", r.K, k.K);
                    Require(k.IsUnsafe == r.IsUnsafe, n, "isUnsafe", r.IsUnsafe, k.IsUnsafe);
                    Require(k.Rules.Length == r.Rules.Length, n, "number of rules", r.Rules.Length, k.Rules.Length);
                    for (int i = 0; i < r.Rules.Length; i++)
                    {
                        Require(k.Rules[i].Ctor.Equals(r.Rules[i].Ctor), n, $"rule {i} constructor", r.Rules[i].Ctor, k.Rules[i].Ctor);
                        Require(k.Rules[i].NumFields == r.Rules[i].NumFields, n, $"rule {i} nfields", r.Rules[i].NumFields, k.Rules[i].NumFields);
                        Require(k.Rules[i].Rhs.Equals(r.Rules[i].Rhs), n, $"rule {i} rhs", r.Rules[i].Rhs, k.Rules[i].Rhs);
                    }
                    break;
                }
        }
    }

    private static void Require(bool ok, Name name, string field, object stored, object derived)
    {
        if (!ok)
        {
            throw new KernelException($"'{name}': stored {field} does not match the kernel's\n  stored: {stored}\n  kernel: {derived}");
        }
    }

    private static string Fmt(Name[] ns) => "[" + string.Join(", ", ns.Select(n => n.ToString())) + "]";

    /// <summary>Every constant name referenced by the constant's type, value, and rules.</summary>
    /// <summary>
    /// Which declarations in <paramref name="scope"/> transitively depend on <paramref name="axiom"/>.
    /// Only edges inside the scope are followed. That is sound when the scope is everything a project defines and
    /// the axiom is one the project introduces, because imports form a one-way graph: a library constant cannot
    /// reference a constant of the project that imports it, so no path can leave the scope and come back.
    /// </summary>
    public static HashSet<Name> DependentsOf(Name axiom, IReadOnlyCollection<ConstantInfo> scope)
    {
        var names = new HashSet<Name>(scope.Select(c => c.Name));
        var users = new Dictionary<Name, List<Name>>();
        var work = new Queue<Name>();
        var hit = new HashSet<Name>();
        foreach (ConstantInfo c in scope)
        {
            foreach (Name u in UsedConstants(c))
            {
                if (u.Equals(axiom))
                {
                    if (hit.Add(c.Name))
                    {
                        work.Enqueue(c.Name);
                    }
                }
                else if (names.Contains(u))
                {
                    if (!users.TryGetValue(u, out List<Name>? list))
                    {
                        users[u] = list = new List<Name>();
                    }
                    list.Add(c.Name);
                }
            }
        }
        while (work.Count > 0)
        {
            Name n = work.Dequeue();
            if (!users.TryGetValue(n, out List<Name>? ups))
            {
                continue;
            }
            foreach (Name up in ups)
            {
                if (hit.Add(up))
                {
                    work.Enqueue(up);
                }
            }
        }
        return hit;
    }

    /// <summary>
    /// The axioms a declaration depends on, transitively, as Lean's <c>#print axioms</c> reports them, together with
    /// the number of constants reached. A proof resting on nothing but <c>propext</c>, <c>Classical.choice</c> and
    /// <c>Quot.sound</c> is complete in Lean's logic; <c>sorryAx</c> marks a hole. Constants the lookup cannot find
    /// are passed over, so the caller should check the declaration first.
    /// </summary>
    public static (SortedSet<Name> Axioms, long Visited) AxiomsOf(Func<Name, ConstantInfo?> find, Name start)
    {
        var axioms = new SortedSet<Name>();
        var seen = new HashSet<Name>();
        var todo = new Stack<Name>();
        long visited = 0;
        todo.Push(start);
        while (todo.Count > 0)
        {
            Name cur = todo.Pop();
            if (!seen.Add(cur))
            {
                continue;
            }
            ConstantInfo? c = find(cur);
            if (c is null)
            {
                continue;
            }
            visited++;
            if (c is AxiomInfo)
            {
                axioms.Add(cur);
                continue;
            }
            foreach (Name u in UsedConstants(c))
            {
                todo.Push(u);
            }
        }
        return (axioms, visited);
    }

    public static HashSet<Name> UsedConstants(ConstantInfo c)
    {
        var used = new HashSet<Name>();
        void Visit(Expr e) => ExprOps.ForEach(e, (t, _) =>
        {
            if (t is ConstExpr k)
            {
                used.Add(k.Name);
            }
            return true;
        });
        Visit(c.Type);
        if (c.Value is Expr v)
        {
            Visit(v);
        }
        if (c is OpaqueInfo o)
        {
            Visit(o.OpaqueValue);
        }
        if (c is RecursorInfo r)
        {
            foreach (RecursorRule rule in r.Rules)
            {
                Visit(rule.Rhs);
            }
        }
        return used;
    }
}
