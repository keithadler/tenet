namespace Tenet.Kernel;

/// <summary>
/// The set of checked constants. Adding a declaration checks it; adding a constant directly (<see cref="AddCore"/>)
/// does not, and is reserved for constants the kernel itself derived. A child environment sees its parent's constants
/// and is used to stage the auxiliary types created while eliminating nested inductives.
/// </summary>
public sealed class Environment
{
    private readonly Dictionary<Name, ConstantInfo> _constants = new();
    private readonly List<ConstantInfo> _order = new();
    private readonly Environment? _parent;
    private bool _quotInitialized;

    public Environment() { }

    private Environment(Environment parent)
    {
        _parent = parent;
        _quotInitialized = parent._quotInitialized;
    }

    /// <summary>A child environment that layers new constants over this one.</summary>
    public Environment CreateChild() => new(this);

    public bool QuotInitialized => _quotInitialized;
    /// <summary>Record that the quotient constants are present (used when they are added unchecked).</summary>
    public void MarkQuotInitialized() => _quotInitialized = true;

    /// <summary>Constants added to this environment (not its parents), in order of addition.</summary>
    public IReadOnlyList<ConstantInfo> OwnConstants => _order;

    public int Count => _constants.Count + (_parent?.Count ?? 0);

    public ConstantInfo? Find(Name n)
    {
        for (Environment? env = this; env is not null; env = env._parent)
        {
            if (env._constants.TryGetValue(n, out ConstantInfo? c))
            {
                return c;
            }
        }
        return null;
    }

    public ConstantInfo Get(Name n) => Find(n) ?? throw new KernelException($"unknown constant '{n}'");

    public bool Contains(Name n) => Find(n) is not null;

    /// <summary>Add a constant without checking it.</summary>
    public void AddCore(ConstantInfo info)
    {
        _constants[info.Name] = info;
        _order.Add(info);
    }

    public void CheckName(Name n)
    {
        if (Contains(n))
        {
            throw new KernelException($"'{n}' has already been declared");
        }
    }

    public void CheckDuplicatedUnivParams(IReadOnlyList<Name> ls)
    {
        for (int i = 0; i < ls.Count; i++)
        {
            for (int j = i + 1; j < ls.Count; j++)
            {
                if (ls[i].Equals(ls[j]))
                {
                    throw new KernelException($"failed to add declaration to environment, duplicate universe level parameter: '{ls[i]}'");
                }
            }
        }
    }

    private static void CheckNoFVar(Name n, Expr e)
    {
        if (e.HasFVar)
        {
            throw new KernelException($"declaration '{n}' contains free variables");
        }
    }

    private void CheckConstantVal(Name name, Name[] lparams, Expr type, TypeChecker checker)
    {
        CheckName(name);
        CheckDuplicatedUnivParams(lparams);
        CheckNoFVar(name, type);
        Expr sort = checker.Check(type, lparams);
        checker.EnsureSort(sort, type);
    }

    /// <summary>Check a declaration and add the resulting constants.</summary>
    public void Add(Declaration d, bool check = true)
    {
        switch (d)
        {
            case AxiomDecl a:
                AddAxiom(a, check);
                break;
            case DefinitionDecl def:
                AddDefinition(def, check);
                break;
            case TheoremDecl t:
                AddTheorem(t, check);
                break;
            case OpaqueDecl o:
                AddOpaque(o, check);
                break;
            case MutualDefinitionDecl m:
                AddMutual(m, check);
                break;
            case QuotDecl:
                Quot.AddQuot(this);
                break;
            case InductiveDecl i:
                Inductive.AddInductive(this, i);
                break;
            default:
                throw new KernelException("unknown declaration kind");
        }
    }

    private void AddAxiom(AxiomDecl d, bool check)
    {
        if (check)
        {
            var checker = new TypeChecker(this, safety: d.IsUnsafe ? DefinitionSafety.Unsafe : DefinitionSafety.Safe);
            CheckConstantVal(d.Name, d.LevelParams, d.Type, checker);
        }
        AddCore(d.ToInfo());
    }

    private void AddDefinition(DefinitionDecl d, bool check)
    {
        if (d.Safety == DefinitionSafety.Unsafe)
        {
            // Unsafe definitions may be recursive: check the header, add, then check the body.
            if (check)
            {
                var checker = new TypeChecker(this, safety: DefinitionSafety.Unsafe);
                CheckConstantVal(d.Name, d.LevelParams, d.Type, checker);
            }
            AddCore(d.ToInfo());
            if (check)
            {
                var checker = new TypeChecker(this, safety: DefinitionSafety.Unsafe);
                CheckNoFVar(d.Name, d.Value);
                Expr valType = checker.Check(d.Value, d.LevelParams);
                if (!checker.IsDefEq(valType, d.Type))
                {
                    throw TypeMismatch(d.Name, d.Type, valType);
                }
            }
            return;
        }
        if (check)
        {
            // The reference checks safe and partial definitions with a safe checker: a partial
            // definition may not depend on another partial one.
            var checker = new TypeChecker(this);
            CheckConstantVal(d.Name, d.LevelParams, d.Type, checker);
            CheckNoFVar(d.Name, d.Value);
            Expr valType = checker.Check(d.Value, d.LevelParams);
            if (!checker.IsDefEq(valType, d.Type))
            {
                throw TypeMismatch(d.Name, d.Type, valType);
            }
        }
        AddCore(d.ToInfo());
    }

    private void AddTheorem(TheoremDecl d, bool check)
    {
        if (check)
        {
            var checker = new TypeChecker(this);
            CheckConstantVal(d.Name, d.LevelParams, d.Type, checker);
            if (!checker.IsProp(d.Type))
            {
                throw new KernelException($"type of theorem '{d.Name}' is not a proposition: {d.Type}");
            }
            CheckNoFVar(d.Name, d.Value);
            Expr valType = checker.Check(d.Value, d.LevelParams);
            if (!checker.IsDefEq(valType, d.Type))
            {
                throw TypeMismatch(d.Name, d.Type, valType);
            }
        }
        AddCore(d.ToInfo());
    }

    private void AddOpaque(OpaqueDecl d, bool check)
    {
        if (check)
        {
            // The reference checks opaque values with a safe checker regardless of the unsafe flag.
            var checker = new TypeChecker(this);
            CheckConstantVal(d.Name, d.LevelParams, d.Type, checker);
            CheckNoFVar(d.Name, d.Value);
            Expr valType = checker.Check(d.Value, d.LevelParams);
            if (!checker.IsDefEq(valType, d.Type))
            {
                throw TypeMismatch(d.Name, d.Type, valType);
            }
        }
        AddCore(d.ToInfo());
    }

    private void AddMutual(MutualDefinitionDecl m, bool check)
    {
        if (m.Definitions.Length == 0)
        {
            throw new KernelException("invalid empty mutual definition");
        }
        DefinitionSafety safety = m.Definitions[0].Safety;
        if (safety == DefinitionSafety.Safe)
        {
            throw new KernelException("invalid mutual definition, declaration is not tagged as unsafe/partial");
        }
        Name[] lparams = m.Definitions[0].LevelParams;
        if (check)
        {
            var checker = new TypeChecker(this, safety: safety);
            var found = new HashSet<Name>();
            foreach (DefinitionDecl v in m.Definitions)
            {
                if (v.Safety != safety)
                {
                    throw new KernelException("invalid mutual definition, declarations must have the same safety annotation");
                }
                if (!Name.ListEquals(v.LevelParams, lparams))
                {
                    throw new KernelException("invalid mutual definition, declarations must have the same universe level parameters");
                }
                if (!found.Add(v.Name))
                {
                    throw new KernelException($"invalid mutual definition, duplicate declaration name '{v.Name}'");
                }
                CheckConstantVal(v.Name, v.LevelParams, v.Type, checker);
            }
        }
        foreach (DefinitionDecl v in m.Definitions)
        {
            AddCore(v.ToInfo());
        }
        if (check)
        {
            var checker = new TypeChecker(this, safety: safety);
            foreach (DefinitionDecl v in m.Definitions)
            {
                CheckNoFVar(v.Name, v.Value);
                Expr valType = checker.Check(v.Value, v.LevelParams);
                if (!checker.IsDefEq(valType, v.Type))
                {
                    throw TypeMismatch(v.Name, v.Type, valType);
                }
            }
        }
    }

    private static KernelException TypeMismatch(Name name, Expr expected, Expr actual) =>
        new($"declaration type mismatch for '{name}'\n  expected: {expected}\n  inferred: {actual}");
}
