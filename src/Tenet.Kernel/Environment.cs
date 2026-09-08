namespace Tenet.Kernel;

/// <summary>
/// The set of checked constants. Adding a declaration checks it; adding a constant directly (<see cref="AddCore"/>)
/// does not, and is reserved for constants the kernel itself derived. A child environment sees its parent's constants
/// and is used to stage the auxiliary types created while eliminating nested inductives.
/// </summary>
public sealed class Environment
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Name, ConstantInfo> _constants = new();
    private readonly List<ConstantInfo> _order = new();
    private readonly Environment? _parent;
    private readonly HashSet<Name>? _hidden;
    private bool _quotInitialized;
    private Func<Name, ConstantInfo?>? _resolver;

    public Environment() { }

    private Environment(Environment parent, HashSet<Name>? hidden)
    {
        _parent = parent;
        _hidden = hidden;
        _quotInitialized = parent._quotInitialized && (hidden is null || !hidden.Contains(Quot.QuotName));
    }

    /// <summary>A child environment that layers new constants over this one.</summary>
    public Environment CreateChild() => new(this, null);

    /// <summary>
    /// A child environment in which the parent's <paramref name="hidden"/> constants are invisible, so a declaration
    /// already present in the parent can be re-derived and checked as if it were new.
    /// </summary>
    public Environment CreateChild(IEnumerable<Name> hidden) => new(this, new HashSet<Name>(hidden));

    public bool QuotInitialized => _quotInitialized;
    /// <summary>Record that the quotient constants are present (used when they are added unchecked).</summary>
    public void MarkQuotInitialized() => _quotInitialized = true;

    /// <summary>
    /// Install a fallback that materializes constants on demand (for example by decoding them from a memory-mapped
    /// <c>.olean</c> file). A resolved constant is cached as if added unchecked; <see cref="EvictResolved"/> drops the cache.
    /// The resolver may be called concurrently.
    /// </summary>
    public void SetResolver(Func<Name, ConstantInfo?>? resolver) => _resolver = resolver;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Name, byte> _resolved = new();

    /// <summary>Forget every constant that came from the resolver, so it is decoded again when next needed.</summary>
    public void EvictResolved()
    {
        foreach (Name n in _resolved.Keys)
        {
            _constants.TryRemove(n, out _);
        }
        _resolved.Clear();
    }

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
            if (env._hidden is not null && env._hidden.Contains(n))
            {
                return null;
            }
            if (env._resolver is Func<Name, ConstantInfo?> resolve)
            {
                ConstantInfo? r = resolve(n);
                if (r is not null)
                {
                    r = env._constants.GetOrAdd(n, r);
                    env._resolved[n] = 0;
                    return r;
                }
            }
        }
        return null;
    }

    public ConstantInfo Get(Name n) => Find(n) ?? throw new KernelException($"unknown constant '{n}'");

    public bool Contains(Name n) => Find(n) is not null;

    private static readonly Name StringOfList = Name.Of("String", "ofList");
    private static readonly Name StringMk = Name.Of("String", "mk");

    /// <summary>
    /// The constant a string literal expands to. The reference kernel hardcodes one name, and that name changed with
    /// <c>String</c>'s representation: <c>String.mk</c> while <c>String</c> was <c>structure String where mk :: data : List Char</c>
    /// (up to about Lean 4.20), <c>String.ofList</c> since. Reading it from the environment instead lets one checker
    /// handle modules built by either toolchain, and can only affect what a literal reduces to, never what is accepted
    /// without a reduction.
    /// </summary>
    public Name StringLiteralConstructor => Contains(StringOfList) ? StringOfList : StringMk;

    /// <summary>Add a constant without checking it. Safe to call from one thread while others read.</summary>
    public void AddCore(ConstantInfo info)
    {
        _constants[info.Name] = info;
        lock (_order)
        {
            _order.Add(info);
        }
        t_added?.Add(info);
    }

    /// <summary>Constants added by the current thread's in-progress <see cref="RunFastThenFaithful"/> action, for rollback.</summary>
    [ThreadStatic] private static List<ConstantInfo>? t_added;

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

    private void CheckConstantVal(Name name, Name[] lparams, Expr type, TypeChecker checker, bool checkName = true)
    {
        if (checkName)
        {
            CheckName(name);
        }
        CheckDuplicatedUnivParams(lparams);
        CheckNoFVar(name, type);
        Expr sort = checker.Check(type, lparams);
        checker.EnsureSort(sort, type);
    }

    /// <summary>Check a declaration and add the resulting constants.</summary>
    public void Add(Declaration d, bool check = true) => RunFastThenFaithful(() => AddCore(d, check, add: true));

    /// <summary>
    /// Run a checking action so that a rejection leaves this environment unchanged. In the fast mode (failure caching
    /// on), a rejected action is run again in the faithful mode, so the reported verdict is exactly the reference
    /// algorithm's; the fast mode can only reject more, never accept more.
    /// </summary>
    private void RunFastThenFaithful(Action action)
    {
        bool fast = TypeChecker.CacheFailures && !TypeChecker.InFaithfulScope;
        try
        {
            RunWithRollback(action);
        }
        catch (KernelException e) when (fast && e is not DeterministicTimeoutException)
        {
            TypeChecker.Stats.CountFaithfulRetry();
            using var _ = new TypeChecker.FaithfulScope();
            RunWithRollback(action);
        }
    }

    private void RunWithRollback(Action action)
    {
        List<ConstantInfo>? saved = t_added;
        var added = new List<ConstantInfo>();
        t_added = added;
        try
        {
            action();
        }
        catch (KernelException)
        {
            Rollback(added);
            throw;
        }
        finally
        {
            t_added = saved;
        }
    }

    /// <summary>Remove constants that a rejected action had already added.</summary>
    private void Rollback(List<ConstantInfo> added)
    {
        if (added.Count == 0)
        {
            return;
        }
        lock (_order)
        {
            foreach (ConstantInfo c in added)
            {
                _constants.TryRemove(c.Name, out _);
                _order.Remove(c);
            }
        }
    }

    /// <summary>
    /// Run every check <see cref="Add"/> would run on a constant-like declaration whose constants are already present
    /// (added unchecked), without adding anything. Used for checking declarations out of order or in parallel.
    /// Inductive and quotient declarations are re-derived in a child environment instead; see <see cref="CreateChild(IEnumerable{Name})"/>.
    /// </summary>
    public void Validate(Declaration d)
    {
        if (d is InductiveDecl or QuotDecl)
        {
            throw new KernelException("Validate does not apply to inductive or quotient declarations");
        }
        RunFastThenFaithful(() => AddCore(d, check: true, add: false));
    }

    private void AddCore(Declaration d, bool check, bool add)
    {
        switch (d)
        {
            case AxiomDecl a:
                AddAxiom(a, check, add);
                break;
            case DefinitionDecl def:
                AddDefinition(def, check, add);
                break;
            case TheoremDecl t:
                AddTheorem(t, check, add);
                break;
            case OpaqueDecl o:
                AddOpaque(o, check, add);
                break;
            case MutualDefinitionDecl m:
                AddMutual(m, check, add);
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

    private void AddAxiom(AxiomDecl d, bool check, bool add)
    {
        if (check)
        {
            var checker = new TypeChecker(this, safety: d.IsUnsafe ? DefinitionSafety.Unsafe : DefinitionSafety.Safe);
            CheckConstantVal(d.Name, d.LevelParams, d.Type, checker, checkName: add);
        }
        if (add)
        {
            AddCore(d.ToInfo());
        }
    }

    private void AddDefinition(DefinitionDecl d, bool check, bool add)
    {
        if (d.Safety == DefinitionSafety.Unsafe)
        {
            // Unsafe definitions may be recursive: check the header, add, then check the body.
            if (check)
            {
                var checker = new TypeChecker(this, safety: DefinitionSafety.Unsafe);
                CheckConstantVal(d.Name, d.LevelParams, d.Type, checker, checkName: add);
            }
            if (add)
            {
                AddCore(d.ToInfo());
            }
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
            CheckConstantVal(d.Name, d.LevelParams, d.Type, checker, checkName: add);
            CheckNoFVar(d.Name, d.Value);
            Expr valType = checker.Check(d.Value, d.LevelParams);
            if (!checker.IsDefEq(valType, d.Type))
            {
                throw TypeMismatch(d.Name, d.Type, valType);
            }
        }
        if (add)
        {
            AddCore(d.ToInfo());
        }
    }

    private void AddTheorem(TheoremDecl d, bool check, bool add)
    {
        if (check)
        {
            var checker = new TypeChecker(this);
            CheckConstantVal(d.Name, d.LevelParams, d.Type, checker, checkName: add);
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
        if (add)
        {
            AddCore(d.ToInfo());
        }
    }

    private void AddOpaque(OpaqueDecl d, bool check, bool add)
    {
        if (check)
        {
            // The reference checks opaque values with a safe checker regardless of the unsafe flag.
            var checker = new TypeChecker(this);
            CheckConstantVal(d.Name, d.LevelParams, d.Type, checker, checkName: add);
            CheckNoFVar(d.Name, d.Value);
            Expr valType = checker.Check(d.Value, d.LevelParams);
            if (!checker.IsDefEq(valType, d.Type))
            {
                throw TypeMismatch(d.Name, d.Type, valType);
            }
        }
        if (add)
        {
            AddCore(d.ToInfo());
        }
    }

    private void AddMutual(MutualDefinitionDecl m, bool check, bool add)
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
                CheckConstantVal(v.Name, v.LevelParams, v.Type, checker, checkName: add);
            }
        }
        if (add)
        {
            foreach (DefinitionDecl v in m.Definitions)
            {
                AddCore(v.ToInfo());
            }
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
