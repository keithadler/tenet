using System.Numerics;
using System.Text;

namespace Tenet.Kernel;

/// <summary>
/// Inductive types: checking a (mutual, nested) declaration, deriving constructors and recursors, and iota reduction.
/// </summary>
public static class Inductive
{
    private static readonly Name NatZero = Name.Of("Nat", "zero");
    private static readonly Name NatSucc = Name.Of("Nat", "succ");
    private static readonly Expr NatZeroExpr = Expr.Const(NatZero, []);
    private static readonly Expr NatSuccExpr = Expr.Const(NatSucc, []);
    private static readonly Expr StringOfListExpr = Expr.Const(Name.Of("String", "ofList"), []);
    private static readonly Expr CharType = Expr.Const(Name.Of("Char"), []);
    private static readonly Expr ListConsChar = Expr.App(Expr.Const(Name.Of("List", "cons"), [Level.Zero]), CharType);
    private static readonly Expr ListNilChar = Expr.App(Expr.Const(Name.Of("List", "nil"), [Level.Zero]), CharType);
    private static readonly Expr CharOfNat = Expr.Const(Name.Of("Char", "ofNat"), []);
    private static readonly Name NestedPrefix = Name.Of("_nested");

    public static Name MkRecName(Name inductName) => inductName.Str("rec");

    /// <summary>An inductive type with one constructor, no indices, and no recursion: a structure.</summary>
    public static bool IsNonRecStructure(Environment env, Name declName) =>
        env.Find(declName) is InductiveInfo i && i.Ctors.Length == 1 && i.NumIndices == 0 && !i.IsRec;

    public static Name? IsConstructorApp(Environment env, Expr e) =>
        e.GetAppFn() is ConstExpr c && env.Find(c.Name) is ConstructorInfo ? c.Name : null;

    private static Name? GetFirstCtor(Environment env, Name dName) =>
        env.Find(dName) is InductiveInfo i && i.Ctors.Length > 0 ? i.Ctors[0] : null;

    /// <summary>For a type <c>I params</c>, the application <c>I.ctor params</c> of its first constructor.</summary>
    private static Expr? MkNullaryCtor(Environment env, Expr type, int numParams)
    {
        Expr d = type.GetAppArgs(out Expr[] args);
        if (d is not ConstExpr dc)
        {
            return null;
        }
        Name? ctor = GetFirstCtor(env, dc.Name);
        if (ctor is null)
        {
            return null;
        }
        return Expr.MkApp(Expr.Const(ctor, dc.Levels), args.AsSpan(0, Math.Min(numParams, args.Length)));
    }

    /// <summary>Convert <c>e : S params</c> into <c>S.mk e.1 ... e.n</c>.</summary>
    public static Expr ExpandEtaStruct(Environment env, Expr eType, Expr e)
    {
        Expr I = eType.GetAppArgs(out Expr[] args);
        if (I is not ConstExpr ic)
        {
            return e;
        }
        Name? ctorName = GetFirstCtor(env, ic.Name);
        if (ctorName is null)
        {
            return e;
        }
        var ctor = (ConstructorInfo)env.Get(ctorName);
        Expr result = Expr.MkApp(Expr.Const(ctorName, ic.Levels), args.AsSpan(0, Math.Min(ctor.NumParams, args.Length)));
        for (int i = 0; i < ctor.NumFields; i++)
        {
            result = Expr.App(result, Expr.Proj(ic.Name, i, e));
        }
        return result;
    }

    private static Expr ToCtorWhenK(Environment env, RecursorInfo rval, Expr e, Func<Expr, Expr> whnf, Func<Expr, Expr> infer, Func<Expr, Expr, bool> isDefEq)
    {
        Expr appType = whnf(infer(e));
        if (appType.GetAppFn() is not ConstExpr I || !I.Name.Equals(rval.GetMajorInduct()))
        {
            return e;
        }
        Expr? newCtorApp = MkNullaryCtor(env, appType, rval.NumParams);
        if (newCtorApp is null)
        {
            return e;
        }
        Expr newType = infer(newCtorApp);
        return isDefEq(appType, newType) ? newCtorApp : e;
    }

    private static Expr ToCtorWhenStructure(Environment env, Name inductName, Expr e, Func<Expr, Expr> whnf, Func<Expr, Expr> infer, Func<Expr, bool> isProp)
    {
        if (!IsNonRecStructure(env, inductName) || IsConstructorApp(env, e) is not null)
        {
            return e;
        }
        Expr eType = whnf(infer(e));
        if (!eType.GetAppFn().IsConstOf(inductName))
        {
            return e;
        }
        if (isProp(eType))
        {
            return e;
        }
        return ExpandEtaStruct(env, eType, e);
    }

    public static Expr NatLitToConstructor(Expr e)
    {
        var v = ((NatLiteral)((LitExpr)e).Value).Value;
        return v.IsZero ? NatZeroExpr : Expr.App(NatSuccExpr, Expr.NatLit(v - 1));
    }

    /// <summary>Expand a string literal to <c>String.ofList [Char.ofNat c₁, ...]</c>.</summary>
    public static Expr StringLitToConstructor(Expr e)
    {
        string s = ((StrLiteral)((LitExpr)e).Value).Value;
        var codes = new List<int>();
        foreach (Rune r in s.EnumerateRunes())
        {
            codes.Add(r.Value);
        }
        Expr result = ListNilChar;
        for (int i = codes.Count - 1; i >= 0; i--)
        {
            result = Expr.MkApp(ListConsChar, Expr.App(CharOfNat, Expr.NatLit(codes[i])), result);
        }
        return Expr.App(StringOfListExpr, result);
    }

    /// <summary>Iota reduction: reduce a recursor applied to a constructor application.</summary>
    public static Expr? TryReduceRec(Environment env, Expr e, Func<Expr, Expr> whnf, Func<Expr, Expr> infer, Func<Expr, Expr, bool> isDefEq, Func<Expr, bool> isProp)
    {
        if (e.GetAppFn() is not ConstExpr recFn || env.Find(recFn.Name) is not RecursorInfo recVal)
        {
            return null;
        }
        e.GetAppArgs(out Expr[] recArgs);
        int majorIdx = recVal.MajorIdx;
        if (majorIdx >= recArgs.Length)
        {
            return null;
        }
        Expr major = recArgs[majorIdx];
        if (recVal.K)
        {
            major = ToCtorWhenK(env, recVal, major, whnf, infer, isDefEq);
        }
        major = whnf(major);
        if (major.IsNatLit)
        {
            major = NatLitToConstructor(major);
        }
        else if (major.IsStrLit)
        {
            major = whnf(StringLitToConstructor(major));
        }
        else
        {
            major = ToCtorWhenStructure(env, recVal.GetMajorInduct(), major, whnf, infer, isProp);
        }
        RecursorRule? rule = recVal.GetRuleFor(major);
        if (rule is null)
        {
            return null;
        }
        major.GetAppArgs(out Expr[] majorArgs);
        if (rule.NumFields > majorArgs.Length)
        {
            return null;
        }
        if (recFn.Levels.Length != recVal.LevelParams.Length)
        {
            return null;
        }
        Expr rhs = ExprOps.InstantiateLevelParams(rule.Rhs, recVal.LevelParams, recFn.Levels);
        // parameters, motives, minor premises
        rhs = Expr.MkApp(rhs, recArgs.AsSpan(0, recVal.NumParams + recVal.NumMotives + recVal.NumMinors));
        // fields of the major premise (its parameter count may differ from the recursor's for nested types)
        int nparams = majorArgs.Length - rule.NumFields;
        rhs = Expr.MkApp(rhs, majorArgs.AsSpan(nparams, rule.NumFields));
        if (recArgs.Length > majorIdx + 1)
        {
            rhs = Expr.MkApp(rhs, recArgs.AsSpan(majorIdx + 1));
        }
        return rhs;
    }

    // =========================================================================================
    // Adding an inductive declaration
    // =========================================================================================

    /// <summary>Check <paramref name="d"/> and add its types, constructors, and recursors to <paramref name="env"/>.</summary>
    public static void AddInductive(Environment env, InductiveDecl d)
    {
        foreach (InductiveType t in d.Types)
        {
            CheckNoFVar(t.Name, t.Type);
            CheckNoNestedAux(t.Name, t.Type);
            foreach (Constructor c in t.Ctors)
            {
                CheckNoFVar(c.Name, c.Type);
                CheckNoNestedAux(c.Name, c.Type);
            }
        }
        CheckUniformIndOccs(d);
        var elim = new ElimNestedInductiveFn(env, d);
        ElimNestedResult res = elim.Run();
        int nnested = res.Aux2Nested.Count;
        Environment auxEnv = env.CreateChild();
        new AddInductiveFn(auxEnv, res.AuxDecl, nnested).Run();
        if (nnested == 0)
        {
            foreach (ConstantInfo c in auxEnv.OwnConstants)
            {
                env.AddCore(c);
            }
            return;
        }
        RestoreNested(env, auxEnv, d, res);
    }

    private static void CheckNoFVar(Name n, Expr e)
    {
        if (e.HasFVar)
        {
            throw new KernelException($"declaration '{n}' contains free variables");
        }
    }

    private static void CheckNoNestedAux(Name n, Expr e)
    {
        if (ExprOps.Find(e, (t, _) => (t is ConstExpr c && NestedPrefix.IsPrefixOf(c.Name)) || (t is ProjExpr p && NestedPrefix.IsPrefixOf(p.StructName))))
        {
            throw new KernelException($"invalid declaration '{n}', it uses the reserved prefix '{NestedPrefix}'");
        }
    }

    /// <summary>Every occurrence of a type being declared must be applied to exactly the block's universe levels and parameters.</summary>
    private static void CheckUniformIndOccs(InductiveDecl d)
    {
        int nparams = d.NumParams;
        Level[] lvls = LParamsToLevels(d.LevelParams);
        var indNames = new HashSet<Name>(d.Types.Select(t => t.Name));
        foreach (InductiveType t in d.Types)
        {
            foreach (Constructor c in t.Ctors)
            {
                ExprOps.ForEach(c.Type, (e, offset) =>
                {
                    Expr fn = e.GetAppArgs(out Expr[] args);
                    if (fn is not ConstExpr fc || !indNames.Contains(fc.Name))
                    {
                        return true;
                    }
                    if (args.Length > nparams)
                    {
                        return true;
                    }
                    bool ok = args.Length == nparams && offset >= nparams && Level.ListEquals(fc.Levels, lvls);
                    for (int i = 0; ok && i < nparams; i++)
                    {
                        ok = args[i].IsBVarOf(offset - 1 - i);
                    }
                    if (!ok)
                    {
                        throw new KernelException($"invalid occurrence of datatype '{fc.Name}' being declared: it must be applied to the parameters and universe levels of the mutual declaration");
                    }
                    return false;
                });
            }
        }
    }

    internal static Level[] LParamsToLevels(Name[] ps)
    {
        var ls = new Level[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            ls[i] = Level.Param(ps[i]);
        }
        return ls;
    }

    private sealed class RecInfo
    {
        public Expr C = null!;          // the motive
        public List<Expr> Minors = new();
        public List<Expr> Indices = new();
        public Expr Major = null!;
    }

    /// <summary>The core of <c>add_inductive</c>: a mutual block with no nested occurrences.</summary>
    private sealed class AddInductiveFn
    {
        private readonly Environment _env;
        private readonly LocalContext _lctx = new();
        private readonly Name[] _lparams;
        private readonly int _nparams;
        private readonly bool _isUnsafe;
        private readonly InductiveType[] _indTypes;
        private readonly List<int> _nindices = new();
        private Level _resultLevel = Level.Zero;
        private Level[] _levels = [];
        private bool _isNotZero;
        private readonly List<Expr> _params = new();
        private readonly List<Expr> _indConsts = new();
        private Level _elimLevel = Level.Zero;
        private bool _kTarget;
        private readonly int _nnested;
        private readonly List<RecInfo> _recInfos = new();

        public AddInductiveFn(Environment env, InductiveDecl decl, int nnested)
        {
            _env = env;
            _lparams = decl.LevelParams;
            _nparams = decl.NumParams;
            _isUnsafe = decl.IsUnsafe;
            _indTypes = decl.Types;
            _nnested = nnested;
        }

        private TypeChecker Tc() => new(_env, _lctx, _isUnsafe ? DefinitionSafety.Unsafe : DefinitionSafety.Safe);

        private Expr GetParamType(int i) => _lctx.Get(_params[i]).Type;

        private Expr MkLocalDecl(Name n, Expr t, BinderInfo bi = BinderInfo.Default) => _lctx.MkLocalDecl(n, ExprOps.ConsumeTypeAnnotations(t), bi);

        private Expr MkLocalDeclFor(PiExpr t) => _lctx.MkLocalDecl(t.BinderName, ExprOps.ConsumeTypeAnnotations(t.Domain), t.Info);

        private Expr Whnf(Expr t) => Tc().Whnf(t);
        private Expr InferType(Expr t) => Tc().Infer(t);
        private bool IsDefEq(Expr a, Expr b) => Tc().IsDefEq(a, b);

        public void Run()
        {
            _env.CheckDuplicatedUnivParams(_lparams);
            CheckInductiveTypes();
            DeclareInductiveTypes();
            CheckConstructors();
            DeclareConstructors();
            InitElimLevel();
            InitKTarget();
            MkRecInfos();
            DeclareRecursors();
            CheckRecursors();
        }

        private void CheckInductiveTypes()
        {
            _levels = LParamsToLevels(_lparams);
            bool first = true;
            foreach (InductiveType indType in _indTypes)
            {
                Expr type = indType.Type;
                _env.CheckName(indType.Name);
                _env.CheckName(MkRecName(indType.Name));
                CheckNoFVar(indType.Name, type);
                Tc().Check(type, _lparams);
                _nindices.Add(0);
                int i = 0;
                type = Whnf(type);
                while (type is PiExpr pi)
                {
                    if (i < _nparams)
                    {
                        if (first)
                        {
                            Expr param = MkLocalDeclFor(pi);
                            _params.Add(param);
                            type = ExprOps.Instantiate1(pi.Body, param);
                        }
                        else
                        {
                            if (!IsDefEq(pi.Domain, GetParamType(i)))
                            {
                                throw new KernelException("parameters of all inductive datatypes must match");
                            }
                            type = ExprOps.Instantiate1(pi.Body, _params[i]);
                        }
                        i++;
                    }
                    else
                    {
                        Expr local = MkLocalDeclFor(pi);
                        type = ExprOps.Instantiate1(pi.Body, local);
                        _nindices[_nindices.Count - 1]++;
                    }
                    type = Whnf(type);
                }
                if (i != _nparams)
                {
                    throw new KernelException("number of parameters mismatch in inductive datatype declaration");
                }
                type = Tc().EnsureSort(type);
                Level lvl = ((SortExpr)type).Level;
                if (first)
                {
                    _resultLevel = lvl;
                    _isNotZero = lvl.IsNotZero();
                }
                else if (!Level.IsEquiv(lvl, _resultLevel))
                {
                    throw new KernelException("mutually inductive types must live in the same universe");
                }
                _indConsts.Add(Expr.Const(indType.Name, _levels));
                first = false;
            }
        }

        private bool IsRec()
        {
            foreach (InductiveType indType in _indTypes)
            {
                foreach (Constructor c in indType.Ctors)
                {
                    Expr t = c.Type;
                    while (t is PiExpr pi)
                    {
                        if (ExprOps.Find(pi.Domain, (e, _) => e is ConstExpr ce && _indConsts.Any(I => ((ConstExpr)I).Name.Equals(ce.Name))))
                        {
                            return true;
                        }
                        t = pi.Body;
                    }
                }
            }
            return false;
        }

        /// <summary>Reflexive: some constructor takes a function returning a type of the block.</summary>
        private bool IsReflexive()
        {
            foreach (InductiveType indType in _indTypes)
            {
                foreach (Constructor c in indType.Ctors)
                {
                    Expr t = c.Type;
                    while (t is PiExpr pi)
                    {
                        Expr argType = pi.Domain;
                        if (argType is PiExpr && HasIndOcc(argType))
                        {
                            return true;
                        }
                        Expr local = MkLocalDeclFor(pi);
                        t = ExprOps.Instantiate1(pi.Body, local);
                    }
                }
            }
            return false;
        }

        private Name[] AllInductiveNames() => _indTypes.Select(t => t.Name).ToArray();

        private void DeclareInductiveTypes()
        {
            bool rec = IsRec();
            bool reflexive = IsReflexive();
            Name[] all = AllInductiveNames();
            for (int idx = 0; idx < _indTypes.Length; idx++)
            {
                InductiveType indType = _indTypes[idx];
                Name[] ctorNames = indType.Ctors.Select(c => c.Name).ToArray();
                _env.CheckName(indType.Name);
                _env.AddCore(new InductiveInfo(indType.Name, _lparams, indType.Type, _nparams, _nindices[idx], all, ctorNames, _nnested, rec, _isUnsafe, reflexive));
            }
        }

        /// <summary><c>t</c> is <c>I params indices</c> for the <paramref name="i"/>-th type, with no occurrence of the block in the indices.</summary>
        private bool IsValidIndApp(Expr t, int i)
        {
            Expr I = t.GetAppArgs(out Expr[] args);
            if (!I.Equals(_indConsts[i]) || args.Length != _nparams + _nindices[i])
            {
                return false;
            }
            for (int j = 0; j < _nparams; j++)
            {
                if (!_params[j].Equals(args[j]))
                {
                    return false;
                }
            }
            for (int j = _nparams; j < args.Length; j++)
            {
                if (HasIndOcc(args[j]))
                {
                    return false;
                }
            }
            return true;
        }

        private int? IsValidIndApp(Expr t)
        {
            for (int i = 0; i < _indTypes.Length; i++)
            {
                if (IsValidIndApp(t, i))
                {
                    return i;
                }
            }
            return null;
        }

        private bool IsIndOcc(Expr e) => e is ConstExpr c && _indConsts.Any(I => ((ConstExpr)I).Name.Equals(c.Name));

        private bool HasIndOcc(Expr t) => ExprOps.Find(t, (e, _) => IsIndOcc(e));

        /// <summary>Index of the block type this argument recurses into, or null for a non-recursive argument.</summary>
        private int? IsRecArgument(Expr t)
        {
            t = Whnf(t);
            while (t is PiExpr pi)
            {
                Expr local = MkLocalDeclFor(pi);
                t = Whnf(ExprOps.Instantiate1(pi.Body, local));
            }
            return IsValidIndApp(t);
        }

        private void CheckPositivity(Expr t, Name ctorName, int argIdx)
        {
            t = Whnf(t);
            if (!HasIndOcc(t))
            {
                return;
            }
            if (t is PiExpr pi)
            {
                if (HasIndOcc(pi.Domain))
                {
                    throw new KernelException($"arg #{argIdx + 1} of '{ctorName}' has a non positive occurrence of the datatypes being declared");
                }
                Expr local = MkLocalDeclFor(pi);
                CheckPositivity(ExprOps.Instantiate1(pi.Body, local), ctorName, argIdx);
            }
            else if (IsValidIndApp(t) is null)
            {
                throw new KernelException($"arg #{argIdx + 1} of '{ctorName}' contains a non valid occurrence of the datatypes being declared");
            }
        }

        private void CheckConstructors()
        {
            for (int idx = 0; idx < _indTypes.Length; idx++)
            {
                InductiveType indType = _indTypes[idx];
                var found = new HashSet<Name>();
                foreach (Constructor c in indType.Ctors)
                {
                    Name n = c.Name;
                    if (!found.Add(n))
                    {
                        throw new KernelException($"duplicate constructor name '{n}'");
                    }
                    Expr t = c.Type;
                    _env.CheckName(n);
                    CheckNoFVar(n, t);
                    Tc().Check(t, _lparams);
                    int i = 0;
                    while (t is PiExpr pi)
                    {
                        if (i < _nparams)
                        {
                            if (!IsDefEq(pi.Domain, GetParamType(i)))
                            {
                                throw new KernelException($"arg #{i + 1} of '{n}' does not match inductive datatypes parameters'");
                            }
                            t = ExprOps.Instantiate1(pi.Body, _params[i]);
                        }
                        else
                        {
                            var s = (SortExpr)Tc().EnsureType(pi.Domain);
                            // the field's universe must fit the type's, unless the type is a proposition
                            if (!(Level.IsGeq(_resultLevel, s.Level) || _resultLevel.NormalizesToZero()))
                            {
                                throw new KernelException($"universe level of type_of(arg #{i + 1}) of '{n}' is too big for the corresponding inductive datatype");
                            }
                            if (!_isUnsafe)
                            {
                                CheckPositivity(pi.Domain, n, i);
                            }
                            Expr local = MkLocalDeclFor(pi);
                            t = ExprOps.Instantiate1(pi.Body, local);
                        }
                        i++;
                    }
                    if (!IsValidIndApp(t, idx))
                    {
                        throw new KernelException($"invalid return type for '{n}'");
                    }
                }
            }
        }

        private void DeclareConstructors()
        {
            for (int idx = 0; idx < _indTypes.Length; idx++)
            {
                InductiveType indType = _indTypes[idx];
                int cidx = 0;
                foreach (Constructor c in indType.Ctors)
                {
                    int arity = 0;
                    for (Expr it = c.Type; it is PiExpr pi; it = pi.Body)
                    {
                        arity++;
                    }
                    int nfields = arity - _nparams;
                    _env.CheckName(c.Name);
                    _env.AddCore(new ConstructorInfo(c.Name, _lparams, c.Type, indType.Name, cidx, _nparams, nfields, _isUnsafe));
                    cidx++;
                }
            }
        }

        /// <summary>Can the recursor only eliminate into Prop?</summary>
        private bool ElimOnlyAtUniverseZero()
        {
            if (_isNotZero)
            {
                return false;
            }
            if (_indTypes.Length > 1)
            {
                return true;
            }
            int numIntros = _indTypes[0].Ctors.Length;
            if (numIntros > 1)
            {
                return true;
            }
            if (numIntros == 0)
            {
                return false;
            }
            // One constructor: every non-Prop field must appear in the result type.
            Constructor ctor = _indTypes[0].Ctors[0];
            Expr type = ctor.Type;
            int i = 0;
            var toCheck = new List<Expr>();
            while (type is PiExpr pi)
            {
                Expr fvar = MkLocalDeclFor(pi);
                if (i >= _nparams)
                {
                    var s = (SortExpr)Tc().EnsureType(pi.Domain);
                    if (!s.Level.NormalizesToZero())
                    {
                        toCheck.Add(fvar);
                    }
                }
                type = ExprOps.Instantiate1(pi.Body, fvar);
                i++;
            }
            type.GetAppArgs(out Expr[] resultArgs);
            foreach (Expr arg in toCheck)
            {
                if (!resultArgs.Any(a => a.Equals(arg)))
                {
                    return true;
                }
            }
            return false;
        }

        private void InitElimLevel()
        {
            if (ElimOnlyAtUniverseZero())
            {
                _elimLevel = Level.Zero;
            }
            else
            {
                Name u = Name.Of("u");
                ulong i = 1;
                while (_lparams.Any(p => p.Equals(u)))
                {
                    u = Name.Of("u").AppendIndexAfter(i);
                    i++;
                }
                _elimLevel = Level.Param(u);
            }
        }

        private void InitKTarget()
        {
            _kTarget = _indTypes.Length == 1 && _resultLevel.NormalizesToZero() && _indTypes[0].Ctors.Length == 1;
            if (!_kTarget)
            {
                return;
            }
            Expr it = _indTypes[0].Ctors[0].Type;
            int i = 0;
            while (it is PiExpr pi)
            {
                if (i < _nparams)
                {
                    it = pi.Body;
                }
                else
                {
                    _kTarget = false;
                    break;
                }
                i++;
            }
        }

        private int GetIIndices(Expr t, List<Expr> indices)
        {
            int r = IsValidIndApp(t) ?? throw new KernelException("internal error: expected a valid inductive application");
            t.GetAppArgs(out Expr[] allArgs);
            for (int i = _nparams; i < allArgs.Length; i++)
            {
                indices.Add(allArgs[i]);
            }
            return r;
        }

        private void MkRecInfos()
        {
            int dIdx = 0;
            foreach (InductiveType indType in _indTypes)
            {
                var info = new RecInfo();
                Expr t = Whnf(indType.Type);
                int i = 0;
                while (t is PiExpr pi)
                {
                    if (i < _nparams)
                    {
                        t = ExprOps.Instantiate1(pi.Body, _params[i]);
                    }
                    else
                    {
                        Expr idx = MkLocalDeclFor(pi);
                        info.Indices.Add(idx);
                        t = ExprOps.Instantiate1(pi.Body, idx);
                    }
                    i++;
                    t = Whnf(t);
                }
                info.Major = MkLocalDecl(Name.Of("t"), Expr.MkApp(Expr.MkApp(_indConsts[dIdx], _params), info.Indices));
                Expr cTy = Expr.Sort(_elimLevel);
                cTy = _lctx.MkPi(info.Major, cTy);
                cTy = _lctx.MkPi(info.Indices, cTy);
                Name cName = Name.Of("motive");
                if (_indTypes.Length > 1)
                {
                    cName = cName.AppendIndexAfter((ulong)dIdx + 1);
                }
                info.C = MkLocalDecl(cName, cTy, BinderInfo.Implicit);
                _recInfos.Add(info);
                dIdx++;
            }
            dIdx = 0;
            foreach (InductiveType indType in _indTypes)
            {
                Name indTypeName = indType.Name;
                foreach (Constructor ctor in indType.Ctors)
                {
                    var bu = new List<Expr>();   // all fields
                    var u = new List<Expr>();    // recursive fields
                    var v = new List<Expr>();    // inductive hypotheses
                    Expr t = ctor.Type;
                    int i = 0;
                    while (t is PiExpr pi)
                    {
                        if (i < _nparams)
                        {
                            t = ExprOps.Instantiate1(pi.Body, _params[i]);
                        }
                        else
                        {
                            Expr l = MkLocalDeclFor(pi);
                            bu.Add(l);
                            if (IsRecArgument(pi.Domain) is not null)
                            {
                                u.Add(l);
                            }
                            t = ExprOps.Instantiate1(pi.Body, l);
                        }
                        i++;
                    }
                    var itIndices = new List<Expr>();
                    int itIdx = GetIIndices(t, itIndices);
                    Expr cApp = Expr.MkApp(_recInfos[itIdx].C, itIndices);
                    Expr introApp = Expr.MkApp(Expr.MkApp(Expr.Const(ctor.Name, _levels), _params), bu);
                    cApp = Expr.App(cApp, introApp);
                    foreach (Expr ui in u)
                    {
                        Expr uiTy = Whnf(InferType(ui));
                        var xs = new List<Expr>();
                        while (uiTy is PiExpr pi)
                        {
                            Expr x = MkLocalDeclFor(pi);
                            xs.Add(x);
                            uiTy = Whnf(ExprOps.Instantiate1(pi.Body, x));
                        }
                        var uIndices = new List<Expr>();
                        int uIdx = GetIIndices(uiTy, uIndices);
                        Expr cApp2 = Expr.MkApp(_recInfos[uIdx].C, uIndices);
                        Expr uApp = Expr.MkApp(ui, xs);
                        cApp2 = Expr.App(cApp2, uApp);
                        Expr viTy = _lctx.MkPi(xs, cApp2);
                        LocalDecl uiDecl = _lctx.Get(ui);
                        Expr vi = MkLocalDecl(uiDecl.UserName.AppendAfter("_ih"), viTy);
                        v.Add(vi);
                    }
                    Expr minorTy = _lctx.MkPi(bu, _lctx.MkPi(v, cApp));
                    Name minorName = ctor.Name.ReplacePrefix(indTypeName, Name.Anonymous);
                    Expr minor = MkLocalDecl(minorName, minorTy);
                    _recInfos[dIdx].Minors.Add(minor);
                }
                dIdx++;
            }
        }

        private Level[] GetRecLevels() => _elimLevel is ParamLevel ? [_elimLevel, .. _levels] : _levels;

        private Name[] GetRecLParams() => _elimLevel is ParamLevel p ? [p.Name, .. _lparams] : _lparams;

        private List<Expr> CollectCs() => _recInfos.Select(r => r.C).ToList();

        private List<Expr> CollectMinors()
        {
            var ms = new List<Expr>();
            foreach (RecInfo r in _recInfos)
            {
                ms.AddRange(r.Minors);
            }
            return ms;
        }

        private RecursorRule[] MkRecRules(int dIdx, List<Expr> cs, List<Expr> minors, ref int minorIdx)
        {
            InductiveType d = _indTypes[dIdx];
            Level[] lvls = GetRecLevels();
            var rules = new List<RecursorRule>();
            foreach (Constructor ctor in d.Ctors)
            {
                var bu = new List<Expr>();
                var u = new List<Expr>();
                Expr t = ctor.Type;
                int i = 0;
                while (t is PiExpr pi)
                {
                    if (i < _nparams)
                    {
                        t = ExprOps.Instantiate1(pi.Body, _params[i]);
                    }
                    else
                    {
                        Expr l = MkLocalDeclFor(pi);
                        bu.Add(l);
                        if (IsRecArgument(pi.Domain) is not null)
                        {
                            u.Add(l);
                        }
                        t = ExprOps.Instantiate1(pi.Body, l);
                    }
                    i++;
                }
                var v = new List<Expr>();
                foreach (Expr ui in u)
                {
                    Expr uiTy = Whnf(InferType(ui));
                    var xs = new List<Expr>();
                    while (uiTy is PiExpr pi)
                    {
                        Expr x = MkLocalDeclFor(pi);
                        xs.Add(x);
                        uiTy = Whnf(ExprOps.Instantiate1(pi.Body, x));
                    }
                    var itIndices = new List<Expr>();
                    int itIdx = GetIIndices(uiTy, itIndices);
                    Name recName = MkRecName(_indTypes[itIdx].Name);
                    Expr recApp = Expr.Const(recName, lvls);
                    recApp = Expr.MkApp(Expr.MkApp(Expr.MkApp(Expr.MkApp(Expr.MkApp(recApp, _params), cs), minors), itIndices), Expr.MkApp(ui, xs));
                    v.Add(_lctx.MkLambda(xs, recApp));
                }
                Expr eApp = Expr.MkApp(Expr.MkApp(minors[minorIdx], bu), v);
                Expr compRhs = _lctx.MkLambda(_params, _lctx.MkLambda(cs, _lctx.MkLambda(minors, _lctx.MkLambda(bu, eApp))));
                rules.Add(new RecursorRule(ctor.Name, bu.Count, compRhs));
                minorIdx++;
            }
            return rules.ToArray();
        }

        private void DeclareRecursors()
        {
            List<Expr> cs = CollectCs();
            List<Expr> minors = CollectMinors();
            int nminors = minors.Count;
            int nmotives = cs.Count;
            Name[] all = AllInductiveNames();
            int minorIdx = 0;
            for (int dIdx = 0; dIdx < _indTypes.Length; dIdx++)
            {
                RecInfo info = _recInfos[dIdx];
                Expr cApp = Expr.App(Expr.MkApp(info.C, info.Indices), info.Major);
                Expr recTy = _lctx.MkPi(info.Major, cApp);
                recTy = _lctx.MkPi(info.Indices, recTy);
                recTy = _lctx.MkPi(minors, recTy);
                recTy = _lctx.MkPi(cs, recTy);
                recTy = _lctx.MkPi(_params, recTy);
                recTy = ExprOps.InferImplicit(recTy, strict: true);
                RecursorRule[] rules = MkRecRules(dIdx, cs, minors, ref minorIdx);
                Name recName = MkRecName(_indTypes[dIdx].Name);
                _env.CheckName(recName);
                _env.AddCore(new RecursorInfo(recName, GetRecLParams(), recTy, all, _nparams, _nindices[dIdx], nmotives, nminors, rules, _kTarget, _isUnsafe));
            }
        }

        /// <summary>Defensive check: each recursor type is well typed and each computation rule preserves types.</summary>
        private void CheckRecursors()
        {
            List<Expr> cs = CollectCs();
            List<Expr> minors = CollectMinors();
            for (int dIdx = 0; dIdx < _indTypes.Length; dIdx++)
            {
                Name recName = MkRecName(_indTypes[dIdx].Name);
                ConstantInfo recCi = _env.Get(recName);
                Tc().Check(recCi.Type, GetRecLParams());
                Expr recPre = Expr.MkApp(Expr.MkApp(Expr.MkApp(Expr.Const(recName, GetRecLevels()), _params), cs), minors);
                foreach (Constructor ctor in _indTypes[dIdx].Ctors)
                {
                    var bu = new List<Expr>();
                    Expr t = ctor.Type;
                    int i = 0;
                    while (t is PiExpr pi)
                    {
                        if (i < _nparams)
                        {
                            t = ExprOps.Instantiate1(pi.Body, _params[i]);
                        }
                        else
                        {
                            Expr l = MkLocalDeclFor(pi);
                            bu.Add(l);
                            t = ExprOps.Instantiate1(pi.Body, l);
                        }
                        i++;
                    }
                    var itIndices = new List<Expr>();
                    GetIIndices(t, itIndices);
                    Expr introApp = Expr.MkApp(Expr.MkApp(Expr.Const(ctor.Name, _levels), _params), bu);
                    Expr lhs = Expr.App(Expr.MkApp(recPre, itIndices), introApp);
                    TypeChecker tc = Tc();
                    Expr expected = tc.Infer(lhs);
                    Expr reduct = tc.Whnf(lhs);
                    Expr actual = tc.Infer(reduct);
                    if (!tc.IsDefEq(actual, expected))
                    {
                        throw new KernelException($"generated recursor computation rule for '{ctor.Name}' is not type-preserving");
                    }
                }
            }
        }
    }

    // =========================================================================================
    // Nested inductives
    // =========================================================================================

    private sealed class ElimNestedResult
    {
        public List<Expr> Params = new();
        public LocalContext ParamsLctx = new();
        /// <summary>auxiliary type name → the nested occurrence <c>I Ds</c> it replaces (contains the params as fvars)</summary>
        public Dictionary<Name, Expr> Aux2Nested = new();
        public InductiveDecl AuxDecl = null!;

        public (Expr Nested, Name AuxName)? GetNestedIfAuxConstructor(Environment auxEnv, Name c)
        {
            if (auxEnv.Find(c) is not ConstructorInfo info)
            {
                return null;
            }
            return Aux2Nested.TryGetValue(info.Induct, out Expr? nested) ? (nested, info.Induct) : null;
        }

        public Name RestoreConstructorName(Environment auxEnv, Name ctorName)
        {
            var p = GetNestedIfAuxConstructor(auxEnv, ctorName)
                ?? throw new KernelException($"failed to restore nested inductive types, '{ctorName}' is not a constructor of an auxiliary type");
            if (p.Nested.GetAppFn() is not ConstExpr I)
            {
                throw new KernelException("failed to restore nested inductive types, nested occurrence is not an inductive type application");
            }
            return ctorName.ReplacePrefix(p.AuxName, I.Name);
        }

        /// <summary>Rewrite auxiliary types and constructors (and renamed recursors) back to the nested originals.</summary>
        public Expr RestoreNested(Expr e, Environment auxEnv, Dictionary<Name, Name>? auxRecNameMap = null)
        {
            var lctx = new LocalContext();
            var As = new List<Expr>();
            bool pi = e is PiExpr;
            for (int i = 0; i < Params.Count; i++)
            {
                if (e is not BindingExpr b)
                {
                    throw new KernelException("failed to restore nested inductive types, fewer binders than parameters");
                }
                As.Add(lctx.MkLocalDecl(b.BinderName, b.Domain, b.Info));
                e = ExprOps.Instantiate1(b.Body, As[^1]);
            }
            e = ExprOps.Replace(e, (t, _) =>
            {
                if (t is ConstExpr tc && auxRecNameMap is not null && auxRecNameMap.TryGetValue(tc.Name, out Name? recName))
                {
                    return Expr.Const(recName, tc.Levels);
                }
                if (t.GetAppFn() is ConstExpr fn)
                {
                    if (Aux2Nested.TryGetValue(fn.Name, out Expr? nested))
                    {
                        t.GetAppArgs(out Expr[] args);
                        if (args.Length < Params.Count)
                        {
                            throw new KernelException("failed to restore nested inductive types, auxiliary type is not applied to all parameters");
                        }
                        Expr newT = ExprOps.InstantiateRev(ExprOps.Abstract(nested, Params), As);
                        return Expr.MkApp(newT, args.AsSpan(Params.Count));
                    }
                    if (GetNestedIfAuxConstructor(auxEnv, fn.Name) is (Expr nested2, Name auxIName))
                    {
                        t.GetAppArgs(out Expr[] args);
                        if (args.Length < Params.Count)
                        {
                            throw new KernelException("failed to restore nested inductive types, auxiliary constructor is not applied to all parameters");
                        }
                        Expr newNested = ExprOps.InstantiateRev(ExprOps.Abstract(nested2, Params), As);
                        Expr I = newNested.GetAppArgs(out Expr[] iArgs);
                        if (I is not ConstExpr ic)
                        {
                            throw new KernelException("failed to restore nested inductive types, nested occurrence is not an inductive type application");
                        }
                        Name newFnName = fn.Name.ReplacePrefix(auxIName, ic.Name);
                        Expr newFn = Expr.Const(newFnName, ic.Levels);
                        return Expr.MkApp(Expr.MkApp(newFn, iArgs), args.AsSpan(Params.Count));
                    }
                }
                return null;
            });
            return pi ? lctx.MkPi(As, e) : lctx.MkLambda(As, e);
        }
    }

    /// <summary>
    /// Replace each nested occurrence <c>I Ds is</c> (a previously declared inductive applied to parameters that mention
    /// the block) by an auxiliary type <c>Iaux As is</c> added to the block, so that the result is an ordinary mutual block.
    /// </summary>
    private sealed class ElimNestedInductiveFn
    {
        private readonly Environment _env;
        private readonly InductiveDecl _d;
        private readonly LocalContext _paramsLctx = new();
        private readonly List<Expr> _params = new();
        private readonly List<(Expr Nested, Name AuxName)> _nestedAux = new();
        private readonly Level[] _lvls;
        private readonly List<InductiveType> _newTypes = new();
        private ulong _nextIdx = 1;

        public ElimNestedInductiveFn(Environment env, InductiveDecl d)
        {
            _env = env;
            _d = d;
            _lvls = LParamsToLevels(d.LevelParams);
        }

        private Name MkUniqueName(Name n)
        {
            while (true)
            {
                Name r = n.AppendIndexAfter(_nextIdx);
                _nextIdx++;
                if (!_env.Contains(r))
                {
                    return r;
                }
            }
        }

        private Expr ReplaceParams(Expr e, List<Expr> As) => ExprOps.InstantiateRev(ExprOps.Abstract(e, As), _params);

        /// <summary>Is <c>e</c> an application <c>I Ds is</c> of a previously declared inductive whose parameters mention the block?</summary>
        private InductiveInfo? IsNestedInductiveApp(Expr e)
        {
            if (e is not AppExpr || e.GetAppFn() is not ConstExpr fn || _env.Find(fn.Name) is not InductiveInfo info)
            {
                return null;
            }
            e.GetAppArgs(out Expr[] args);
            int nparams = info.NumParams;
            if (nparams > args.Length)
            {
                return null;
            }
            bool isNested = false;
            bool looseBVars = false;
            for (int i = 0; i < nparams; i++)
            {
                if (args[i].HasLooseBVars)
                {
                    looseBVars = true;
                }
                if (ExprOps.Find(args[i], (t, _) => t is ConstExpr c && _newTypes.Any(nt => nt.Name.Equals(c.Name))))
                {
                    isNested = true;
                }
            }
            if (!isNested)
            {
                return null;
            }
            if (looseBVars)
            {
                throw new KernelException($"invalid nested inductive datatype '{fn.Name}', nested inductive datatypes parameters cannot contain local variables.");
            }
            return info;
        }

        private Expr InstantiatePiParams(Expr e, ReadOnlySpan<Expr> ps)
        {
            for (int i = 0; i < ps.Length; i++)
            {
                if (e is not PiExpr pi)
                {
                    throw new KernelException("invalid nested inductive datatype, ill-formed declaration");
                }
                e = pi.Body;
            }
            return ExprOps.InstantiateRev(e, ps);
        }

        private Expr? ReplaceIfNested(LocalContext lctx, List<Expr> As, Expr e)
        {
            InductiveInfo? iVal = IsNestedInductiveApp(e);
            if (iVal is null)
            {
                return null;
            }
            var fn = (ConstExpr)e.GetAppArgs(out Expr[] args);
            Name iName = fn.Name;
            Level[] iLvls = fn.Levels;
            int iNparams = iVal.NumParams;
            Expr IAs = Expr.MkApp(fn, args.AsSpan(0, iNparams));
            Expr iParams = ReplaceParams(IAs, As);
            Name? auxIName = null;
            foreach (var (nested, auxName) in _nestedAux)
            {
                if (nested.Equals(iParams))
                {
                    auxIName = auxName;
                    break;
                }
            }
            if (auxIName is not null)
            {
                Expr auxI = Expr.MkApp(Expr.Const(auxIName, _lvls), As);
                return Expr.MkApp(auxI, args.AsSpan(iNparams));
            }
            Expr? result = null;
            // Copy every type J of the mutual block containing I as an auxiliary type.
            foreach (Name jName in iVal.All)
            {
                ConstantInfo jInfo = _env.Get(jName);
                Expr J = Expr.Const(jName, iLvls);
                Expr JAs = Expr.MkApp(J, args.AsSpan(0, iNparams));
                Name auxJName = MkUniqueName(NestedPrefix.Append(jName));
                Expr auxJType = ExprOps.InstantiateLevelParams(jInfo.Type, jInfo.LevelParams, iLvls);
                auxJType = InstantiatePiParams(auxJType, args.AsSpan(0, iNparams));
                auxJType = lctx.MkPi(As, auxJType);
                _nestedAux.Add((ReplaceParams(JAs, As), auxJName));
                if (jName.Equals(iName))
                {
                    Expr auxI = Expr.MkApp(Expr.Const(auxJName, _lvls), As);
                    result = Expr.MkApp(auxI, args.AsSpan(iNparams));
                }
                var auxJCtors = new List<Constructor>();
                foreach (Name jCtorName in ((InductiveInfo)jInfo).Ctors)
                {
                    ConstantInfo jCtorInfo = _env.Get(jCtorName);
                    Name auxJCtorName = jCtorName.ReplacePrefix(jName, auxJName);
                    // still refers to J; fixed when this auxiliary type is processed by the main loop
                    Expr auxJCtorType = ExprOps.InstantiateLevelParams(jCtorInfo.Type, jCtorInfo.LevelParams, iLvls);
                    auxJCtorType = InstantiatePiParams(auxJCtorType, args.AsSpan(0, iNparams));
                    auxJCtorType = lctx.MkPi(As, auxJCtorType);
                    auxJCtors.Add(new Constructor(auxJCtorName, auxJCtorType));
                }
                _newTypes.Add(new InductiveType(auxJName, auxJType, auxJCtors.ToArray()));
            }
            return result ?? throw new KernelException("internal error: nested inductive replacement produced no result");
        }

        private Expr ReplaceAllNested(LocalContext lctx, List<Expr> As, Expr e) => ExprOps.Replace(e, (t, _) => ReplaceIfNested(lctx, As, t));

        private Expr GetParams(Expr type, int nparams, LocalContext lctx, List<Expr> ps)
        {
            for (int i = 0; i < nparams; i++)
            {
                if (type is not PiExpr pi)
                {
                    throw new KernelException("invalid inductive datatype declaration, incorrect number of parameters");
                }
                ps.Add(lctx.MkLocalDecl(pi.BinderName, pi.Domain, pi.Info));
                type = ExprOps.Instantiate1(pi.Body, ps[^1]);
            }
            return type;
        }

        public ElimNestedResult Run()
        {
            int dNparams = _d.NumParams;
            _newTypes.AddRange(_d.Types);
            if (_newTypes.Count == 0)
            {
                throw new KernelException("invalid empty (mutual) inductive datatype declaration, it must contain at least one inductive type.");
            }
            GetParams(_newTypes[0].Type, dNparams, _paramsLctx, _params);
            int qhead = 0;
            while (qhead < _newTypes.Count)
            {
                InductiveType indType = _newTypes[qhead];
                var newCtors = new List<Constructor>();
                foreach (Constructor ctor in indType.Ctors)
                {
                    var lctx = new LocalContext();
                    var As = new List<Expr>();
                    // Re-create the parameters per constructor to preserve their binder infos.
                    Expr ctorType = GetParams(ctor.Type, dNparams, lctx, As);
                    Expr newCtorType = ReplaceAllNested(lctx, As, ctorType);
                    newCtorType = lctx.MkPi(As, newCtorType);
                    newCtors.Add(new Constructor(ctor.Name, newCtorType));
                }
                _newTypes[qhead] = new InductiveType(indType.Name, indType.Type, newCtors.ToArray());
                qhead++;
            }
            var res = new ElimNestedResult
            {
                Params = _params,
                ParamsLctx = _paramsLctx,
                AuxDecl = new InductiveDecl(_d.LevelParams, _d.NumParams, _newTypes.ToArray(), _d.IsUnsafe),
            };
            foreach (var (nested, auxName) in _nestedAux)
            {
                res.Aux2Nested[auxName] = nested;
            }
            return res;
        }
    }

    /// <summary>Recursor names for the auxiliary types become <c>Main.rec_1</c>, <c>Main.rec_2</c>, ...</summary>
    private static (List<Name> AuxRecNames, Dictionary<Name, Name> Map) MkAuxRecNameMap(Environment auxEnv, InductiveDecl d)
    {
        int ntypes = d.Types.Length;
        Name mainName = d.Types[0].Name;
        var mainInfo = (InductiveInfo)auxEnv.Get(mainName);
        var oldRecNames = new List<Name>();
        var map = new Dictionary<Name, Name>();
        int i = 0;
        ulong nextIdx = 1;
        foreach (Name indName in mainInfo.All)
        {
            if (i >= ntypes)
            {
                Name oldRec = MkRecName(indName);
                oldRecNames.Add(oldRec);
                map[oldRec] = MkRecName(mainName).AppendIndexAfter(nextIdx);
                nextIdx++;
            }
            i++;
        }
        return (oldRecNames, map);
    }

    private static void RestoreNested(Environment env, Environment auxEnv, InductiveDecl d, ElimNestedResult res)
    {
        Name[] allIndNames = d.Types.Select(t => t.Name).ToArray();
        var (auxRecNames, auxRecNameMap) = MkAuxRecNameMap(auxEnv, d);
        var newRecNames = new List<Name>();

        void ProcessRec(Name recName)
        {
            Name newRecName = auxRecNameMap.TryGetValue(recName, out Name? mapped) ? mapped : recName;
            var recInfo = (RecursorInfo)auxEnv.Get(recName);
            Expr newRecType = res.RestoreNested(recInfo.Type, auxEnv, auxRecNameMap);
            var newRules = new List<RecursorRule>();
            foreach (RecursorRule rule in recInfo.Rules)
            {
                Expr newRhs = res.RestoreNested(rule.Rhs, auxEnv, auxRecNameMap);
                Name newCtorName = rule.Ctor;
                if (!newRecName.Equals(recName))
                {
                    newCtorName = res.RestoreConstructorName(auxEnv, rule.Ctor);
                }
                newRules.Add(new RecursorRule(newCtorName, rule.NumFields, newRhs));
            }
            env.CheckName(newRecName);
            env.AddCore(new RecursorInfo(newRecName, recInfo.LevelParams, newRecType, allIndNames, recInfo.NumParams, recInfo.NumIndices,
                                         recInfo.NumMotives, recInfo.NumMinors, newRules.ToArray(), recInfo.K, recInfo.IsUnsafe));
            newRecNames.Add(newRecName);
        }

        foreach (InductiveType indType in d.Types)
        {
            var indVal = (InductiveInfo)auxEnv.Get(indType.Name);
            env.CheckName(indVal.Name);
            env.AddCore(new InductiveInfo(indVal.Name, indVal.LevelParams, indVal.Type, indVal.NumParams, indVal.NumIndices, allIndNames,
                                          indVal.Ctors, indVal.NumNested, indVal.IsRec, indVal.IsUnsafe, indVal.IsReflexive));
            foreach (Name ctorName in indVal.Ctors)
            {
                var ctorVal = (ConstructorInfo)auxEnv.Get(ctorName);
                Expr newType = res.RestoreNested(ctorVal.Type, auxEnv);
                env.CheckName(ctorVal.Name);
                env.AddCore(new ConstructorInfo(ctorVal.Name, ctorVal.LevelParams, newType, ctorVal.Induct, ctorVal.Cidx, ctorVal.NumParams, ctorVal.NumFields, ctorVal.IsUnsafe));
            }
            ProcessRec(MkRecName(indType.Name));
        }
        foreach (Name auxRec in auxRecNames)
        {
            ProcessRec(auxRec);
        }
        DefinitionSafety safety = d.IsUnsafe ? DefinitionSafety.Unsafe : DefinitionSafety.Safe;
        // The parametric arguments of the nested occurrences were not part of the auxiliary declaration: check them now.
        {
            var tc = new TypeChecker(env, res.ParamsLctx, safety);
            foreach (Expr nested in res.Aux2Nested.Values)
            {
                tc.Check(nested, d.LevelParams);
            }
        }
        // Re-check everything the restoration rewrote.
        {
            var tc = new TypeChecker(env, null, safety);
            foreach (InductiveType indType in d.Types)
            {
                foreach (Constructor ctor in indType.Ctors)
                {
                    tc.Check(env.Get(ctor.Name).Type, d.LevelParams);
                }
            }
            foreach (Name recName in newRecNames)
            {
                var recInfo = (RecursorInfo)env.Get(recName);
                tc.Check(recInfo.Type, recInfo.LevelParams);
                foreach (RecursorRule rule in recInfo.Rules)
                {
                    tc.Check(rule.Rhs, recInfo.LevelParams);
                }
            }
        }
    }
}

internal static class NameExtensions
{
    /// <summary>Concatenate two names: <c>a.b + c.d = a.b.c.d</c>.</summary>
    public static Name Append(this Name pre, Name n)
    {
        if (n.IsAnonymous)
        {
            return pre;
        }
        Name p = pre.Append(n.Prefix);
        if (n.TryGetStr(out _, out string? s))
        {
            return p.Str(s);
        }
        n.TryGetNum(out _, out ulong v);
        return p.Num(v);
    }
}
