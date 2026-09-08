using System.Numerics;

namespace Tenet.Kernel;

internal enum LBool : byte
{
    False,
    True,
    Undef,
}

/// <summary>
/// The type checker: type inference, weak head normalization, and definitional equality, following the algorithm of
/// the Lean 4 kernel (lazy delta reduction guided by reducibility hints, proof irrelevance, eta for functions and
/// structures, K-like reduction, native <c>Nat</c> and <c>String</c> literals). One instance checks one declaration;
/// its caches are private to it.
/// </summary>
public sealed class TypeChecker
{
    /// <summary>Upper bound on the size of a <c>Nat</c> literal the checker will compute, in bytes.</summary>
    public static long NatMaxSizeBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>
    /// Upper bound on definition unfoldings per checker. Lean bounds kernel work with a heartbeat limit; without a
    /// bound, an unsafe definition such as <c>unsafe def f : Nat := f</c> would make normalization spin forever.
    /// </summary>
    public static long MaxUnfolds { get; set; } = 100_000_000;

    private long _unfolds;

    public Environment Env { get; }
    public LocalContext Lctx { get; }
    private readonly DefinitionSafety _safety;

    /// <summary>Process-wide counters of kernel work, for diagnostics and for proving a run was not vacuous.</summary>
    public static class Stats
    {
        /// <summary>Counting is off by default: atomic increments on hot paths serialize parallel checking.</summary>
        public static bool Enabled { get; set; }

        private static long s_infer, s_whnf, s_whnfCore, s_defEq, s_unfold, s_iota, s_natLit;
        public static long Infer => s_infer;
        public static long Whnf => s_whnf;
        public static long WhnfCore => s_whnfCore;
        public static long DefEq => s_defEq;
        public static long Unfold => s_unfold;
        public static long Iota => s_iota;
        public static long NatLit => s_natLit;
        internal static void CountInfer()
        {
            if (Enabled)
            {
                Interlocked.Increment(ref s_infer);
            }
        }
        internal static void CountWhnf()
        {
            if (Enabled)
            {
                Interlocked.Increment(ref s_whnf);
            }
        }
        internal static void CountWhnfCore()
        {
            if (Enabled)
            {
                Interlocked.Increment(ref s_whnfCore);
            }
        }
        internal static void CountDefEq()
        {
            if (Enabled)
            {
                Interlocked.Increment(ref s_defEq);
            }
        }
        internal static void CountUnfold()
        {
            if (Enabled)
            {
                Interlocked.Increment(ref s_unfold);
            }
        }
        internal static void CountIota()
        {
            if (Enabled)
            {
                Interlocked.Increment(ref s_iota);
            }
        }
        internal static void CountNatLit()
        {
            if (Enabled)
            {
                Interlocked.Increment(ref s_natLit);
            }
        }
        public static void Reset()
        {
            s_infer = s_whnf = s_whnfCore = s_defEq = s_unfold = s_iota = s_natLit = 0;
        }
        public static string Summary => $"infer {Infer}, whnf {Whnf}, whnfCore {WhnfCore}, defEq {DefEq}, unfold {Unfold}, iota {Iota}, natLit {NatLit}";
    }
    private bool _eagerReduce;
    private Name[]? _lparams;

    private readonly Dictionary<Expr, Expr> _inferOnlyCache = new();
    private readonly Dictionary<Expr, Expr> _checkCache = new();
    private readonly Dictionary<Expr, Expr> _whnfCoreCache = new();
    private readonly Dictionary<Expr, Expr> _whnfCache = new();
    private readonly Dictionary<Expr, Expr> _unfoldCache = new();
    private readonly HashSet<(Expr, Expr)> _success = new();
    private readonly HashSet<(Expr, Expr)> _failure = new();

    private static readonly Name NatZero = Name.Of("Nat", "zero");
    private static readonly Name NatSucc = Name.Of("Nat", "succ");
    private static readonly Name NatAdd = Name.Of("Nat", "add");
    private static readonly Name NatSub = Name.Of("Nat", "sub");
    private static readonly Name NatMul = Name.Of("Nat", "mul");
    private static readonly Name NatPow = Name.Of("Nat", "pow");
    private static readonly Name NatGcd = Name.Of("Nat", "gcd");
    private static readonly Name NatMod = Name.Of("Nat", "mod");
    private static readonly Name NatDiv = Name.Of("Nat", "div");
    private static readonly Name NatBeq = Name.Of("Nat", "beq");
    private static readonly Name NatBle = Name.Of("Nat", "ble");
    private static readonly Name NatLand = Name.Of("Nat", "land");
    private static readonly Name NatLor = Name.Of("Nat", "lor");
    private static readonly Name NatXor = Name.Of("Nat", "xor");
    private static readonly Name NatShiftLeft = Name.Of("Nat", "shiftLeft");
    private static readonly Name NatShiftRight = Name.Of("Nat", "shiftRight");
    private static readonly Name BoolTrue = Name.Of("Bool", "true");
    private static readonly Name BoolFalse = Name.Of("Bool", "false");
    private static readonly Name EagerReduce = Name.Of("eagerReduce");
    private static readonly Name StringOfList = Name.Of("String", "ofList");
    private static readonly Name LeanReduceBool = Name.Of("Lean", "reduceBool");
    private static readonly Name LeanReduceNat = Name.Of("Lean", "reduceNat");

    private static readonly Expr NatZeroExpr = Expr.Const(NatZero, []);
    private static readonly Expr BoolTrueExpr = Expr.Const(BoolTrue, []);
    private static readonly Expr BoolFalseExpr = Expr.Const(BoolFalse, []);
    private static readonly Expr DontCare = Expr.Const(Name.Of("dontcare"), []);

    public TypeChecker(Environment env, LocalContext? lctx = null, DefinitionSafety safety = DefinitionSafety.Safe)
    {
        Env = env;
        Lctx = lctx ?? new LocalContext();
        _safety = safety;
    }

    // ------------------------------------------------------------------ public API

    /// <summary>Infer the type of <paramref name="e"/>, assuming it is well typed.</summary>
    public Expr Infer(Expr e) => InferTypeCore(e, inferOnly: true);

    /// <summary>Type check <paramref name="e"/>, allowing only the universe parameters <paramref name="lparams"/>, and return its type.</summary>
    public Expr Check(Expr e, Name[] lparams)
    {
        Name[]? saved = _lparams;
        _lparams = lparams;
        try
        {
            return InferTypeCore(e, inferOnly: false);
        }
        finally
        {
            _lparams = saved;
        }
    }

    /// <summary>Type check <paramref name="e"/> without restricting universe parameters.</summary>
    public Expr Check(Expr e)
    {
        Name[]? saved = _lparams;
        _lparams = null;
        try
        {
            return InferTypeCore(e, inferOnly: false);
        }
        finally
        {
            _lparams = saved;
        }
    }

    public Expr EnsureSort(Expr e, Expr s) => EnsureSortCore(e, s);
    public Expr EnsureSort(Expr e) => EnsureSortCore(e, e);
    public Expr EnsurePi(Expr e, Expr s) => EnsurePiCore(e, s);
    public Expr EnsurePi(Expr e) => EnsurePiCore(e, e);
    /// <summary>Ensure the type of <paramref name="e"/> is a sort and return that sort.</summary>
    public Expr EnsureType(Expr e) => EnsureSortCore(Infer(e), e);
    public Expr EnsureFun(Expr e) => EnsurePiCore(Infer(e), e);

    /// <summary>True when <paramref name="e"/> is a proposition, that is, its type is <c>Prop</c> up to level normalization.</summary>
    public bool IsProp(Expr e)
    {
        var s = (SortExpr)EnsureSort(Infer(e));
        return s.Level.NormalizesToZero();
    }

    // ------------------------------------------------------------------ inference

    private Expr EnsureSortCore(Expr e, Expr s)
    {
        if (e is SortExpr)
        {
            return e;
        }
        Expr n = Whnf(e);
        if (n is SortExpr)
        {
            return n;
        }
        throw new KernelException($"type expected, but got\n  {s}\nof type\n  {e}");
    }

    private Expr EnsurePiCore(Expr e, Expr s)
    {
        if (e is PiExpr)
        {
            return e;
        }
        Expr n = Whnf(e);
        if (n is PiExpr)
        {
            return n;
        }
        throw new KernelException($"function expected, but got\n  {s}\nof type\n  {e}");
    }

    private void CheckLevel(Level l)
    {
        if (_lparams is not null && l.GetUndefParam(_lparams) is Name n)
        {
            throw new KernelException($"invalid reference to undefined universe level parameter '{n}'");
        }
    }

    private Expr InferFVar(FVarExpr e) => Lctx.Find(e.Id)?.Type ?? throw new KernelException("unknown free variable");

    private Expr InferConstant(ConstExpr e, bool inferOnly)
    {
        ConstantInfo info = Env.Get(e.Name);
        if (info.LevelParams.Length != e.Levels.Length)
        {
            throw new KernelException($"incorrect number of universe levels parameters for '{e.Name}', #{info.LevelParams.Length} expected, #{e.Levels.Length} provided");
        }
        if (!inferOnly)
        {
            if (info.IsUnsafe && _safety != DefinitionSafety.Unsafe)
            {
                throw new KernelException($"invalid declaration, it uses unsafe declaration '{e.Name}'");
            }
            if (info is DefinitionInfo { Safety: DefinitionSafety.Partial } && _safety == DefinitionSafety.Safe)
            {
                throw new KernelException($"invalid declaration, safe declaration must not contain partial declaration '{e.Name}'");
            }
            foreach (Level l in e.Levels)
            {
                CheckLevel(l);
            }
        }
        return info.InstantiateTypeLevelParams(e.Levels);
    }

    private Expr InferLambda(Expr e, bool inferOnly)
    {
        var fvars = new List<Expr>();
        while (e is LamExpr lam)
        {
            Expr d = ExprOps.InstantiateRev(lam.Domain, fvars);
            if (!inferOnly)
            {
                EnsureSortCore(InferTypeCore(d, inferOnly), d);
            }
            fvars.Add(Lctx.MkLocalDecl(lam.BinderName, d, lam.Info));
            e = lam.Body;
        }
        Expr r = InferTypeCore(ExprOps.InstantiateRev(e, fvars), inferOnly);
        r = ExprOps.CheapBetaReduce(r);
        return Lctx.MkPi(fvars, r);
    }

    private Expr InferPi(Expr e, bool inferOnly)
    {
        var fvars = new List<Expr>();
        var us = new List<Level>();
        while (e is PiExpr pi)
        {
            Expr d = ExprOps.InstantiateRev(pi.Domain, fvars);
            var t1 = (SortExpr)EnsureSortCore(InferTypeCore(d, inferOnly), d);
            us.Add(t1.Level);
            fvars.Add(Lctx.MkLocalDecl(pi.BinderName, d, pi.Info));
            e = pi.Body;
        }
        e = ExprOps.InstantiateRev(e, fvars);
        var s = (SortExpr)EnsureSortCore(InferTypeCore(e, inferOnly), e);
        Level r = s.Level;
        for (int i = us.Count - 1; i >= 0; i--)
        {
            r = Level.MkIMax(us[i], r);
        }
        return Expr.Sort(r);
    }

    private static bool IsEagerReduce(Expr e) => e.IsAppOfArity(EagerReduce, 2);

    private Expr InferApp(AppExpr e, bool inferOnly)
    {
        if (!inferOnly)
        {
            var fType = (PiExpr)EnsurePiCore(InferTypeCore(e.Fn, inferOnly), e);
            Expr aType = InferTypeCore(e.Arg, inferOnly);
            Expr dType = fType.Domain;
            if (IsEagerReduce(e.Arg))
            {
                bool saved = _eagerReduce;
                _eagerReduce = true;
                try
                {
                    if (!IsDefEq(aType, dType))
                    {
                        throw AppTypeMismatch(e, fType, aType);
                    }
                }
                finally
                {
                    _eagerReduce = saved;
                }
            }
            else if (!IsDefEq(aType, dType))
            {
                throw AppTypeMismatch(e, fType, aType);
            }
            return ExprOps.Instantiate1(fType.Body, e.Arg);
        }
        else
        {
            Expr f = e.GetAppArgs(out Expr[] args);
            Expr fType = InferTypeCore(f, inferOnly: true);
            int j = 0;
            int nargs = args.Length;
            for (int i = 0; i < nargs; i++)
            {
                if (fType is PiExpr pi)
                {
                    fType = pi.Body;
                }
                else
                {
                    fType = ExprOps.InstantiateRev(fType, args.AsSpan(j, i - j));
                    fType = ((PiExpr)EnsurePiCore(fType, e)).Body;
                    j = i;
                }
            }
            return ExprOps.InstantiateRev(fType, args.AsSpan(j, nargs - j));
        }
    }

    private KernelException AppTypeMismatch(Expr app, PiExpr fType, Expr argType) =>
        new($"application type mismatch in\n  {ExprPrinter.Print(app, Lctx)}\nargument has type\n  {ExprPrinter.Print(argType, Lctx)}\nbut function has type\n  {ExprPrinter.Print(fType, Lctx)}");

    private Expr InferLet(Expr e, bool inferOnly)
    {
        var fvars = new List<Expr>();
        while (e is LetExpr let)
        {
            Expr type = ExprOps.InstantiateRev(let.Type, fvars);
            Expr val = ExprOps.InstantiateRev(let.Value, fvars);
            if (!inferOnly)
            {
                EnsureSortCore(InferTypeCore(type, inferOnly), type);
                Expr valType = InferTypeCore(val, inferOnly);
                if (!IsDefEq(valType, type))
                {
                    throw new KernelException($"type mismatch at let binding '{let.Name}'\n  value has type\n  {ExprPrinter.Print(valType, Lctx)}\nbut is expected to have type\n  {ExprPrinter.Print(type, Lctx)}");
                }
            }
            fvars.Add(Lctx.MkLetDecl(let.Name, type, val));
            e = let.Body;
        }
        Expr r = InferTypeCore(ExprOps.InstantiateRev(e, fvars), inferOnly);
        r = ExprOps.CheapBetaReduce(r);
        return Lctx.MkPi(fvars, r, removeDeadLet: true);
    }

    private Expr InferProj(ProjExpr e, bool inferOnly)
    {
        Expr type = Whnf(InferTypeCore(e.Struct, inferOnly));
        Expr I = type.GetAppArgs(out Expr[] args);
        if (I is not ConstExpr ic || !ic.Name.Equals(e.StructName))
        {
            throw InvalidProj(e);
        }
        if (Env.Get(ic.Name) is not InductiveInfo ival)
        {
            throw InvalidProj(e);
        }
        if (ival.Ctors.Length != 1 || args.Length != ival.NumParams + ival.NumIndices)
        {
            throw InvalidProj(e);
        }
        ConstantInfo cinfo = Env.Get(ival.Ctors[0]);
        Expr r = cinfo.InstantiateTypeLevelParams(ic.Levels);
        for (int i = 0; i < ival.NumParams; i++)
        {
            r = Whnf(r);
            if (r is not PiExpr pi)
            {
                throw InvalidProj(e);
            }
            r = ExprOps.Instantiate1(pi.Body, args[i]);
        }
        bool isPropType = IsProp(type);
        for (int i = 0; i < e.Idx; i++)
        {
            r = Whnf(r);
            if (r is not PiExpr pi)
            {
                throw InvalidProj(e);
            }
            if (pi.Body.HasLooseBVars)
            {
                if (isPropType && !IsProp(pi.Domain))
                {
                    throw InvalidProj(e);
                }
                r = ExprOps.Instantiate1(pi.Body, Expr.Proj(ic.Name, i, e.Struct));
            }
            else
            {
                r = pi.Body;
            }
        }
        r = Whnf(r);
        if (r is not PiExpr last)
        {
            throw InvalidProj(e);
        }
        r = last.Domain;
        if (isPropType && !IsProp(r))
        {
            throw InvalidProj(e);
        }
        return r;
    }

    private KernelException InvalidProj(ProjExpr e) => new($"invalid projection\n  {ExprPrinter.Print(e, Lctx)}");

    private static void CheckNatSize(long numBytes)
    {
        if (numBytes > NatMaxSizeBytes)
        {
            throw new KernelException("the kernel refused a `Nat` numeral because its size exceeds the maximum (TypeChecker.NatMaxSizeBytes)");
        }
    }

    private Expr InferLit(LitExpr e)
    {
        if (e.Value is NatLiteral n)
        {
            CheckNatSize(n.Value.GetByteCount());
        }
        return e.Value.Type;
    }

    private Expr InferTypeCore(Expr e, bool inferOnly)
    {
        if (e.HasLooseBVars)
        {
            throw new KernelException("type checker does not support loose bound variables, replace them with free variables before invoking it");
        }
        Dictionary<Expr, Expr> cache = inferOnly ? _inferOnlyCache : _checkCache;
        if (cache.TryGetValue(e, out Expr? cached))
        {
            return cached;
        }
        Stats.CountInfer();
        Expr r;
        switch (e)
        {
            case LitExpr lit:
                r = InferLit(lit);
                break;
            case ProjExpr p:
                r = InferProj(p, inferOnly);
                break;
            case FVarExpr f:
                r = InferFVar(f);
                break;
            case SortExpr s:
                if (!inferOnly)
                {
                    CheckLevel(s.Level);
                }
                r = Expr.Sort(Level.Succ(s.Level));
                break;
            case ConstExpr c:
                r = InferConstant(c, inferOnly);
                break;
            case LamExpr:
                r = InferLambda(e, inferOnly);
                break;
            case PiExpr:
                r = InferPi(e, inferOnly);
                break;
            case AppExpr a:
                r = InferApp(a, inferOnly);
                break;
            case LetExpr:
                r = InferLet(e, inferOnly);
                break;
            default:
                throw new KernelException("unexpected bound variable");
        }
        cache[e] = r;
        return r;
    }

    // ------------------------------------------------------------------ reduction

    private Expr? ReduceRecursor(Expr e, bool cheapRec, bool cheapProj)
    {
        if (Env.QuotInitialized)
        {
            Expr? q = Quot.TryReduceRec(e, Whnf);
            if (q is not null)
            {
                return q;
            }
        }
        return Inductive.TryReduceRec(Env, e,
            t => cheapRec ? WhnfCore(t, cheapRec, cheapProj) : Whnf(t),
            Infer, IsDefEq, IsProp);
    }

    private Expr WhnfFVar(FVarExpr e, bool cheapRec, bool cheapProj)
    {
        LocalDecl? d = Lctx.Find(e.Id);
        if (d?.Value is Expr v)
        {
            return WhnfCore(v, cheapRec, cheapProj);
        }
        return e;
    }

    private Expr? ReduceProjCore(Expr c, Name sname, int idx)
    {
        if (c.IsStrLit)
        {
            c = Whnf(Inductive.StringLitToConstructor(c));
        }
        Expr mk = c.GetAppArgs(out Expr[] args);
        if (mk is not ConstExpr mkc || Env.Find(mkc.Name) is not ConstructorInfo mkVal)
        {
            return null;
        }
        if (!mkVal.Induct.Equals(sname))
        {
            return null;
        }
        int nparams = mkVal.NumParams;
        return nparams + idx < args.Length ? args[nparams + idx] : null;
    }

    private Expr? ReduceProj(ProjExpr e, bool cheapRec, bool cheapProj)
    {
        Expr c = cheapProj ? WhnfCore(e.Struct, cheapRec, cheapProj) : Whnf(e.Struct);
        return ReduceProjCore(c, e.StructName, e.Idx);
    }

    private bool IsLetFVar(Expr e) => e is FVarExpr f && Lctx.Find(f.Id)?.Value is not null;

    /// <summary>Weak head normal form without delta reduction (beta, zeta, projection, iota, quotient).</summary>
    public Expr WhnfCore(Expr e, bool cheapRec = false, bool cheapProj = false)
    {
        switch (e.Kind)
        {
            case ExprKind.BVar:
            case ExprKind.Sort:
            case ExprKind.Pi:
            case ExprKind.Const:
            case ExprKind.Lam:
            case ExprKind.Lit:
                return e;
            case ExprKind.FVar:
                if (!IsLetFVar(e))
                {
                    return e;
                }
                break;
        }
        if (_whnfCoreCache.TryGetValue(e, out Expr? cached))
        {
            return cached;
        }
        Stats.CountWhnfCore();
        Expr r;
        switch (e)
        {
            case FVarExpr f:
                return WhnfFVar(f, cheapRec, cheapProj);
            case ProjExpr p:
                {
                    Expr? m = ReduceProj(p, cheapRec, cheapProj);
                    r = m is not null ? WhnfCore(m, cheapRec, cheapProj) : e;
                    break;
                }
            case AppExpr:
                {
                    Expr f0 = e.GetAppRevArgs(out Expr[] revArgs);
                    Expr f = WhnfCore(f0, cheapRec, cheapProj);
                    if (f is LamExpr)
                    {
                        int m = 1;
                        int numArgs = revArgs.Length;
                        Expr body = ((LamExpr)f).Body;
                        while (body is LamExpr inner && m < numArgs)
                        {
                            body = inner.Body;
                            m++;
                        }
                        Expr inst = ExprOps.Instantiate(body, 0, revArgs.AsSpan(numArgs - m, m).ToArray());
                        r = WhnfCore(Expr.MkRevApp(inst, revArgs.AsSpan(0, numArgs - m)), cheapRec, cheapProj);
                    }
                    else if (ReferenceEquals(f, f0))
                    {
                        Expr? red = ReduceRecursor(e, cheapRec, cheapProj);
                        if (red is not null)
                        {
                            Stats.CountIota();
                            return WhnfCore(red, cheapRec, cheapProj);
                        }
                        return e;
                    }
                    else
                    {
                        r = WhnfCore(Expr.MkRevApp(f, revArgs), cheapRec, cheapProj);
                    }
                    break;
                }
            case LetExpr let:
                r = WhnfCore(ExprOps.Instantiate1(let.Body, let.Value), cheapRec, cheapProj);
                break;
            default:
                throw new InvalidOperationException();
        }
        if (!cheapRec && !cheapProj)
        {
            _whnfCoreCache[e] = r;
        }
        return r;
    }

    /// <summary>The definition to unfold if the head of <paramref name="e"/> is a delta-reducible constant.</summary>
    private ConstantInfo? IsDelta(Expr e)
    {
        if (e.GetAppFn() is ConstExpr c && Env.Find(c.Name) is ConstantInfo info && info.HasValue && c.Levels.Length == info.LevelParams.Length)
        {
            return info;
        }
        return null;
    }

    private Expr? UnfoldDefinitionCore(Expr e)
    {
        if (e is ConstExpr c && IsDelta(e) is ConstantInfo d)
        {
            if (c.Levels.Length > 0)
            {
                if (_unfoldCache.TryGetValue(e, out Expr? hit))
                {
                    return hit;
                }
                Expr result = d.InstantiateValueLevelParams(c.Levels);
                _unfoldCache[e] = result;
                return result;
            }
            return d.InstantiateValueLevelParams(c.Levels);
        }
        return null;
    }

    /// <summary>Unfold the head constant of <paramref name="e"/>, if it is a definition or theorem.</summary>
    public Expr? UnfoldDefinition(Expr e)
    {
        if (e is AppExpr)
        {
            Expr f0 = e.GetAppRevArgs(out Expr[] revArgs);
            Expr? f = UnfoldDefinitionCore(f0);
            return f is not null ? Expr.MkRevApp(f, revArgs) : null;
        }
        return UnfoldDefinitionCore(e);
    }

    private static Expr? ReduceNative(Expr e)
    {
        if (e is AppExpr a && a.Arg is ConstExpr && a.Fn is ConstExpr f && (f.Name.Equals(LeanReduceBool) || f.Name.Equals(LeanReduceNat)))
        {
            throw new UnsupportedException($"'{f.Name}' requires running compiled code, which an external checker cannot trust; the declaration cannot be checked");
        }
        return null;
    }

    private static bool IsNatLitExt(Expr e) => e.Equals(NatZeroExpr) || e.IsNatLit;

    private static BigInteger GetNatVal(Expr e) => e is LitExpr { Value: NatLiteral n } ? n.Value : BigInteger.Zero;

    private Expr? ReduceBinNatOp(Func<BigInteger, BigInteger, BigInteger> f, AppExpr e, bool checkSize = false)
    {
        Expr arg1 = Whnf(((AppExpr)e.Fn).Arg);
        if (!IsNatLitExt(arg1))
        {
            return null;
        }
        Expr arg2 = Whnf(e.Arg);
        if (!IsNatLitExt(arg2))
        {
            return null;
        }
        BigInteger r = f(GetNatVal(arg1), GetNatVal(arg2));
        if (checkSize)
        {
            CheckNatSize(r.GetByteCount());
        }
        return Expr.NatLit(r);
    }

    private Expr? ReduceBinNatPred(Func<BigInteger, BigInteger, bool> f, AppExpr e)
    {
        Expr arg1 = Whnf(((AppExpr)e.Fn).Arg);
        if (!IsNatLitExt(arg1))
        {
            return null;
        }
        Expr arg2 = Whnf(e.Arg);
        if (!IsNatLitExt(arg2))
        {
            return null;
        }
        return f(GetNatVal(arg1), GetNatVal(arg2)) ? BoolTrueExpr : BoolFalseExpr;
    }

    private static int GetCountArg(BigInteger count, string op)
    {
        if (count > uint.MaxValue)
        {
            throw new KernelException($"the kernel refused to evaluate `{op}` because its second argument does not fit in a 32-bit unsigned integer");
        }
        return (int)Math.Min((long)count, int.MaxValue);
    }

    private Expr? ReducePow(AppExpr e)
    {
        Expr arg1 = Whnf(((AppExpr)e.Fn).Arg);
        if (!IsNatLitExt(arg1))
        {
            return null;
        }
        Expr arg2 = Whnf(e.Arg);
        if (!IsNatLitExt(arg2))
        {
            return null;
        }
        BigInteger b = GetNatVal(arg1);
        BigInteger ex = GetNatVal(arg2);
        int k = GetCountArg(ex, "Nat.pow");
        if (b > 1 && k != 0 && b.GetByteCount() > NatMaxSizeBytes / k)
        {
            throw new KernelException("the kernel refused to evaluate `Nat.pow` because the result would exceed the maximum numeral size");
        }
        return Expr.NatLit(BigInteger.Pow(b, k));
    }

    private Expr? ReduceShiftLeft(AppExpr e)
    {
        Expr arg1 = Whnf(((AppExpr)e.Fn).Arg);
        if (!IsNatLitExt(arg1))
        {
            return null;
        }
        Expr arg2 = Whnf(e.Arg);
        if (!IsNatLitExt(arg2))
        {
            return null;
        }
        BigInteger v = GetNatVal(arg1);
        BigInteger shift = GetNatVal(arg2);
        if (v.IsZero)
        {
            return Expr.NatLit(BigInteger.Zero);
        }
        int k = GetCountArg(shift, "Nat.shiftLeft");
        CheckNatSize(v.GetByteCount() + k / 8 + 1);
        return Expr.NatLit(v << k);
    }

    private Expr? ReduceNat(Expr e)
    {
        int nargs = e.GetAppNumArgs();
        if (nargs == 1)
        {
            var a = (AppExpr)e;
            if (a.Fn.IsConstOf(NatSucc) && a.Fn is ConstExpr { Levels.Length: 0 })
            {
                Expr arg = Whnf(a.Arg);
                if (!IsNatLitExt(arg))
                {
                    return null;
                }
                BigInteger v = GetNatVal(arg) + 1;
                CheckNatSize(v.GetByteCount());
                return Expr.NatLit(v);
            }
        }
        else if (nargs == 2)
        {
            var a = (AppExpr)e;
            if (((AppExpr)a.Fn).Fn is not ConstExpr f || f.Levels.Length != 0)
            {
                return null;
            }
            Name n = f.Name;
            if (n.Equals(NatAdd))
            {
                return ReduceBinNatOp((x, y) => x + y, a, checkSize: true);
            }
            if (n.Equals(NatSub))
            {
                return ReduceBinNatOp((x, y) => x >= y ? x - y : BigInteger.Zero, a, checkSize: true);
            }
            if (n.Equals(NatMul))
            {
                return ReduceBinNatOp((x, y) => x * y, a, checkSize: true);
            }
            if (n.Equals(NatPow))
            {
                return ReducePow(a);
            }
            if (n.Equals(NatGcd))
            {
                return ReduceBinNatOp(BigInteger.GreatestCommonDivisor, a);
            }
            if (n.Equals(NatMod))
            {
                return ReduceBinNatOp((x, y) => y.IsZero ? x : x % y, a);
            }
            if (n.Equals(NatDiv))
            {
                return ReduceBinNatOp((x, y) => y.IsZero ? BigInteger.Zero : x / y, a);
            }
            if (n.Equals(NatBeq))
            {
                return ReduceBinNatPred((x, y) => x == y, a);
            }
            if (n.Equals(NatBle))
            {
                return ReduceBinNatPred((x, y) => x <= y, a);
            }
            if (n.Equals(NatLand))
            {
                return ReduceBinNatOp((x, y) => x & y, a);
            }
            if (n.Equals(NatLor))
            {
                return ReduceBinNatOp((x, y) => x | y, a);
            }
            if (n.Equals(NatXor))
            {
                return ReduceBinNatOp((x, y) => x ^ y, a);
            }
            if (n.Equals(NatShiftLeft))
            {
                return ReduceShiftLeft(a);
            }
            if (n.Equals(NatShiftRight))
            {
                return ReduceBinNatOp((x, y) => y > int.MaxValue ? BigInteger.Zero : x >> (int)y, a);
            }
        }
        return null;
    }

    /// <summary>Weak head normal form, including delta reduction and literal arithmetic.</summary>
    public Expr Whnf(Expr e)
    {
        switch (e.Kind)
        {
            case ExprKind.BVar:
            case ExprKind.Sort:
            case ExprKind.Pi:
            case ExprKind.Lit:
                return e;
            case ExprKind.FVar:
                if (!IsLetFVar(e))
                {
                    return e;
                }
                break;
        }
        if (_whnfCache.TryGetValue(e, out Expr? cached))
        {
            return cached;
        }
        Stats.CountWhnf();
        Expr t = e;
        while (true)
        {
            Expr t1 = WhnfCore(t);
            Expr? v = ReduceNative(t1);
            if (v is not null)
            {
                _whnfCache[e] = v;
                return v;
            }
            v = ReduceNat(t1);
            if (v is not null)
            {
                Stats.CountNatLit();
                _whnfCache[e] = v;
                return v;
            }
            Expr? next = UnfoldDefinition(t1);
            if (next is not null)
            {
                Stats.CountUnfold();
                if (++_unfolds > MaxUnfolds)
                {
                    throw new KernelException($"deterministic timeout: more than {MaxUnfolds} definition unfoldings while checking one declaration (TypeChecker.MaxUnfolds)");
                }
                t = next;
            }
            else
            {
                _whnfCache[e] = t1;
                return t1;
            }
        }
    }

    // ------------------------------------------------------------------ definitional equality

    private bool IsDefEqBinding(BindingExpr t, BindingExpr s)
    {
        ExprKind k = t.Kind;
        var subst = new List<Expr>();
        Expr tt = t;
        Expr ss = s;
        do
        {
            var tb = (BindingExpr)tt;
            var sb = (BindingExpr)ss;
            Expr? varSType = null;
            if (!tb.Domain.Equals(sb.Domain))
            {
                varSType = ExprOps.InstantiateRev(sb.Domain, subst);
                Expr varTType = ExprOps.InstantiateRev(tb.Domain, subst);
                if (!IsDefEq(varTType, varSType))
                {
                    return false;
                }
            }
            if (tb.Body.HasLooseBVars || sb.Body.HasLooseBVars)
            {
                varSType ??= ExprOps.InstantiateRev(sb.Domain, subst);
                subst.Add(Lctx.MkLocalDecl(sb.BinderName, varSType, sb.Info));
            }
            else
            {
                subst.Add(DontCare);
            }
            tt = tb.Body;
            ss = sb.Body;
        }
        while (tt.Kind == k && ss.Kind == k);
        return IsDefEq(ExprOps.InstantiateRev(tt, subst), ExprOps.InstantiateRev(ss, subst));
    }

    private static bool IsDefEqLevels(Level[] a, Level[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (!Level.IsEquiv(a[i], b[i]))
            {
                return false;
            }
        }
        return true;
    }

    private LBool QuickIsDefEq(Expr t, Expr s)
    {
        if (t.Equals(s) || SucceededBefore(t, s))
        {
            return LBool.True;
        }
        if (t.Kind == s.Kind)
        {
            switch (t)
            {
                case BindingExpr tb:
                    return ToLBool(IsDefEqBinding(tb, (BindingExpr)s));
                case SortExpr ts:
                    return ToLBool(Level.IsEquiv(ts.Level, ((SortExpr)s).Level));
                case LitExpr tl:
                    return ToLBool(tl.Value.Equals(((LitExpr)s).Value));
            }
        }
        return LBool.Undef;
    }

    private static LBool ToLBool(bool b) => b ? LBool.True : LBool.False;

    private bool IsDefEqArgs(Expr t, Expr s)
    {
        while (t is AppExpr ta && s is AppExpr sa)
        {
            if (!IsDefEq(ta.Arg, sa.Arg))
            {
                return false;
            }
            t = ta.Fn;
            s = sa.Fn;
        }
        return t is not AppExpr && s is not AppExpr;
    }

    /// <summary>Try to solve <c>(fun x, B) =?= s</c> by eta-expanding <paramref name="s"/>.</summary>
    private bool TryEtaExpansionCore(Expr t, Expr s)
    {
        if (t is LamExpr && s is not LamExpr)
        {
            Expr sType = Whnf(Infer(s));
            if (sType is not PiExpr pi)
            {
                return false;
            }
            Expr newS = Expr.Lam(pi.BinderName, pi.Domain, Expr.App(s, Expr.BVar(0)), pi.Info);
            return IsDefEq(t, newS);
        }
        return false;
    }

    private bool TryEtaExpansion(Expr t, Expr s) => TryEtaExpansionCore(t, s) || TryEtaExpansionCore(s, t);

    /// <summary>Check whether <paramref name="s"/> is <c>mk t.1 ... t.n</c> for a structure constructor <c>mk</c>.</summary>
    private bool TryEtaStructCore(Expr t, Expr s)
    {
        if (s.GetAppFn() is not ConstExpr f || Env.Find(f.Name) is not ConstructorInfo fVal)
        {
            return false;
        }
        if (s.GetAppNumArgs() != fVal.NumParams + fVal.NumFields)
        {
            return false;
        }
        if (!Inductive.IsNonRecStructure(Env, fVal.Induct))
        {
            return false;
        }
        if (!IsDefEq(Infer(t), Infer(s)))
        {
            return false;
        }
        s.GetAppArgs(out Expr[] sArgs);
        for (int i = fVal.NumParams; i < sArgs.Length; i++)
        {
            Expr proj = Expr.Proj(fVal.Induct, i - fVal.NumParams, t);
            if (!IsDefEq(proj, sArgs[i]))
            {
                return false;
            }
        }
        return true;
    }

    private bool TryEtaStruct(Expr t, Expr s) => TryEtaStructCore(t, s) || TryEtaStructCore(s, t);

    private bool IsDefEqApp(Expr t, Expr s)
    {
        if (t is AppExpr && s is AppExpr)
        {
            Expr tFn = t.GetAppArgs(out Expr[] tArgs);
            Expr sFn = s.GetAppArgs(out Expr[] sArgs);
            if (tArgs.Length == sArgs.Length && IsDefEq(tFn, sFn))
            {
                for (int i = 0; i < tArgs.Length; i++)
                {
                    if (!IsDefEq(tArgs[i], sArgs[i]))
                    {
                        return false;
                    }
                }
                return true;
            }
        }
        return false;
    }

    private LBool IsDefEqProofIrrel(Expr t, Expr s)
    {
        Expr tType = Infer(t);
        if (!IsProp(tType))
        {
            return LBool.Undef;
        }
        Expr sType = Infer(s);
        return ToLBool(IsDefEq(tType, sType));
    }

    private static (Expr, Expr) OrderPair(Expr t, Expr s) => t.Hash <= s.Hash ? (t, s) : (s, t);

    private bool FailedBefore(Expr t, Expr s) =>
        t.Hash == s.Hash ? _failure.Contains((t, s)) || _failure.Contains((s, t)) : _failure.Contains(OrderPair(t, s));

    private void CacheFailure(Expr t, Expr s) => _failure.Add(OrderPair(t, s));

    private bool SucceededBefore(Expr t, Expr s) =>
        t.Hash == s.Hash ? _success.Contains((t, s)) || _success.Contains((s, t)) : _success.Contains(OrderPair(t, s));

    private void CacheSuccess(Expr t, Expr s) => _success.Add(OrderPair(t, s));

    /// <summary>If <paramref name="e"/> is <c>s.i ...</c> and can be reduced by <c>whnf_core</c>, return the reduct.</summary>
    private Expr? TryUnfoldProjApp(Expr e)
    {
        if (e.GetAppFn() is ProjExpr)
        {
            Expr n = WhnfCore(e);
            return n.Equals(e) ? null : n;
        }
        return null;
    }

    private enum ReductionStatus : byte
    {
        Continue,
        DefUnknown,
        DefEqual,
        DefDiff,
    }

    private ReductionStatus LazyDeltaReductionStep(ref Expr tn, ref Expr sn)
    {
        if (++_unfolds > MaxUnfolds)
        {
            throw new KernelException($"deterministic timeout: more than {MaxUnfolds} definition unfoldings while checking one declaration (TypeChecker.MaxUnfolds)");
        }
        ConstantInfo? dt = IsDelta(tn);
        ConstantInfo? ds = IsDelta(sn);
        if (dt is null && ds is null)
        {
            return ReductionStatus.DefUnknown;
        }
        if (dt is not null && ds is null)
        {
            Expr? snNew = TryUnfoldProjApp(sn);
            if (snNew is not null)
            {
                sn = snNew;
            }
            else
            {
                tn = WhnfCore(UnfoldDefinition(tn)!, false, true);
            }
        }
        else if (dt is null && ds is not null)
        {
            Expr? tnNew = TryUnfoldProjApp(tn);
            if (tnNew is not null)
            {
                tn = tnNew;
            }
            else
            {
                sn = WhnfCore(UnfoldDefinition(sn)!, false, true);
            }
        }
        else
        {
            int c = ReducibilityHints.Compare(HintsOf(dt!), HintsOf(ds!));
            if (c < 0)
            {
                tn = WhnfCore(UnfoldDefinition(tn)!, false, true);
            }
            else if (c > 0)
            {
                sn = WhnfCore(UnfoldDefinition(sn)!, false, true);
            }
            else
            {
                if (tn is AppExpr && sn is AppExpr && ReferenceEquals(dt, ds) && HintsOf(dt!).IsRegular)
                {
                    // Same head with the same height: if the arguments agree we are done without unfolding.
                    if (!FailedBefore(tn, sn))
                    {
                        if (IsDefEqLevels(((ConstExpr)tn.GetAppFn()).Levels, ((ConstExpr)sn.GetAppFn()).Levels) && IsDefEqArgs(tn, sn))
                        {
                            return ReductionStatus.DefEqual;
                        }
                        CacheFailure(tn, sn);
                    }
                }
                tn = WhnfCore(UnfoldDefinition(tn)!, false, true);
                sn = WhnfCore(UnfoldDefinition(sn)!, false, true);
            }
        }
        return QuickIsDefEq(tn, sn) switch
        {
            LBool.True => ReductionStatus.DefEqual,
            LBool.False => ReductionStatus.DefDiff,
            _ => ReductionStatus.Continue,
        };
    }

    /// <summary>Theorems and non-regular definitions unfold like opaque-hinted definitions.</summary>
    private static ReducibilityHints HintsOf(ConstantInfo c) => c is DefinitionInfo d ? d.Hints : ReducibilityHints.Opaque;

    private static bool IsNatZero(Expr t) => t.Equals(NatZeroExpr) || (t is LitExpr { Value: NatLiteral n } && n.Value.IsZero);

    private static Expr? IsNatSucc(Expr t)
    {
        if (t is LitExpr { Value: NatLiteral n } && !n.Value.IsZero)
        {
            return Expr.NatLit(n.Value - 1);
        }
        if (t is AppExpr a && a.Fn is ConstExpr { Levels.Length: 0 } c && c.Name.Equals(NatSucc))
        {
            return a.Arg;
        }
        return null;
    }

    private LBool IsDefEqOffset(Expr t, Expr s)
    {
        if (IsNatZero(t) && IsNatZero(s))
        {
            return LBool.True;
        }
        Expr? predT = IsNatSucc(t);
        Expr? predS = IsNatSucc(s);
        if (predT is not null && predS is not null)
        {
            return ToLBool(IsDefEqCore(predT, predS));
        }
        return LBool.Undef;
    }

    private LBool LazyDeltaReduction(ref Expr tn, ref Expr sn)
    {
        while (true)
        {
            LBool r = IsDefEqOffset(tn, sn);
            if (r != LBool.Undef)
            {
                return r;
            }
            if ((!tn.HasFVar && !sn.HasFVar) || _eagerReduce)
            {
                Expr? tv = ReduceNat(tn);
                if (tv is not null)
                {
                    return ToLBool(IsDefEqCore(tv, sn));
                }
                Expr? sv = ReduceNat(sn);
                if (sv is not null)
                {
                    return ToLBool(IsDefEqCore(tn, sv));
                }
            }
            Expr? tnat = ReduceNative(tn);
            if (tnat is not null)
            {
                return ToLBool(IsDefEqCore(tnat, sn));
            }
            Expr? snat = ReduceNative(sn);
            if (snat is not null)
            {
                return ToLBool(IsDefEqCore(tn, snat));
            }
            switch (LazyDeltaReductionStep(ref tn, ref sn))
            {
                case ReductionStatus.Continue:
                    break;
                case ReductionStatus.DefUnknown:
                    return LBool.Undef;
                case ReductionStatus.DefEqual:
                    return LBool.True;
                case ReductionStatus.DefDiff:
                    return LBool.False;
            }
        }
    }

    private bool LazyDeltaProjReduction(ref Expr tn, ref Expr sn, Name sname, int idx)
    {
        while (true)
        {
            switch (LazyDeltaReductionStep(ref tn, ref sn))
            {
                case ReductionStatus.Continue:
                    break;
                case ReductionStatus.DefEqual:
                    return true;
                default:
                    {
                        Expr? t = ReduceProjCore(tn, sname, idx);
                        if (t is not null)
                        {
                            Expr? s = ReduceProjCore(sn, sname, idx);
                            if (s is not null)
                            {
                                return IsDefEqCore(t, s);
                            }
                        }
                        return IsDefEqCore(tn, sn);
                    }
            }
        }
    }

    private LBool TryStringLitExpansionCore(Expr t, Expr s)
    {
        if (t.IsStrLit && s is AppExpr sa && sa.Fn is ConstExpr { Levels.Length: 0 } c && c.Name.Equals(StringOfList))
        {
            return ToLBool(IsDefEqCore(Whnf(Inductive.StringLitToConstructor(t)), s));
        }
        return LBool.Undef;
    }

    private LBool TryStringLitExpansion(Expr t, Expr s)
    {
        LBool r = TryStringLitExpansionCore(t, s);
        return r != LBool.Undef ? r : TryStringLitExpansionCore(s, t);
    }

    /// <summary>Two terms of a structure type with one constructor and no fields are definitionally equal.</summary>
    private bool IsDefEqUnitLike(Expr t, Expr s)
    {
        Expr tType = Whnf(Infer(t));
        if (tType.GetAppFn() is not ConstExpr I || !Inductive.IsNonRecStructure(Env, I.Name))
        {
            return false;
        }
        var ival = (InductiveInfo)Env.Get(I.Name);
        var ctor = (ConstructorInfo)Env.Get(ival.Ctors[0]);
        if (ctor.NumFields != 0)
        {
            return false;
        }
        return IsDefEqCore(tType, Infer(s));
    }

    private bool IsDefEqCore(Expr t, Expr s)
    {
        Stats.CountDefEq();
        LBool r = QuickIsDefEq(t, s);
        if (r != LBool.Undef)
        {
            return r == LBool.True;
        }

        // Proofs by reflection: a closed term against `Bool.true` is fully reduced.
        if ((!t.HasFVar || _eagerReduce) && s.IsConstOf(BoolTrue))
        {
            if (Whnf(t).IsConstOf(BoolTrue))
            {
                return true;
            }
        }

        Expr tn = WhnfCore(t, false, true);
        Expr sn = WhnfCore(s, false, true);

        if (!ReferenceEquals(tn, t) || !ReferenceEquals(sn, s))
        {
            r = QuickIsDefEq(tn, sn);
            if (r != LBool.Undef)
            {
                return r == LBool.True;
            }
        }

        r = IsDefEqProofIrrel(tn, sn);
        if (r != LBool.Undef)
        {
            return r == LBool.True;
        }

        r = LazyDeltaReduction(ref tn, ref sn);
        if (r != LBool.Undef)
        {
            return r == LBool.True;
        }

        if (tn is ConstExpr tc && sn is ConstExpr sc && tc.Name.Equals(sc.Name) && IsDefEqLevels(tc.Levels, sc.Levels))
        {
            return true;
        }
        if (tn is FVarExpr tf && sn is FVarExpr sf && tf.Id == sf.Id)
        {
            return true;
        }
        if (tn is ProjExpr tp && sn is ProjExpr sp && tp.StructName.Equals(sp.StructName) && tp.Idx == sp.Idx)
        {
            Expr tcStruct = tp.Struct;
            Expr scStruct = sp.Struct;
            if (LazyDeltaProjReduction(ref tcStruct, ref scStruct, tp.StructName, tp.Idx))
            {
                return true;
            }
        }

        // whnf_core again, now reducing projections with full whnf.
        Expr tnn = WhnfCore(tn);
        Expr snn = WhnfCore(sn);
        if (!ReferenceEquals(tnn, tn) || !ReferenceEquals(snn, sn))
        {
            return IsDefEqCore(tnn, snn);
        }

        if (IsDefEqApp(tn, sn))
        {
            return true;
        }
        if (TryEtaExpansion(tn, sn))
        {
            return true;
        }
        if (TryEtaStruct(tn, sn))
        {
            return true;
        }
        r = TryStringLitExpansion(tn, sn);
        if (r != LBool.Undef)
        {
            return r == LBool.True;
        }
        if (IsDefEqUnitLike(tn, sn))
        {
            return true;
        }
        return false;
    }

    /// <summary>Definitional equality. Sound but incomplete; results are cached per checker.</summary>
    public bool IsDefEq(Expr t, Expr s)
    {
        bool r = IsDefEqCore(t, s);
        if (r)
        {
            CacheSuccess(t, s);
        }
        return r;
    }

    /// <summary>Eta-expand <paramref name="e"/> to a lambda over all arguments of its Pi type.</summary>
    public Expr EtaExpand(Expr e)
    {
        var fvars = new List<Expr>();
        Expr it = e;
        while (it is LamExpr lam)
        {
            Expr d = ExprOps.InstantiateRev(lam.Domain, fvars);
            fvars.Add(Lctx.MkLocalDecl(lam.BinderName, d, lam.Info));
            it = lam.Body;
        }
        it = ExprOps.InstantiateRev(it, fvars);
        Expr itType = Whnf(Infer(it));
        if (itType is not PiExpr)
        {
            return e;
        }
        var args = new List<Expr>();
        while (itType is PiExpr pi)
        {
            Expr arg = Lctx.MkLocalDecl(pi.BinderName, pi.Domain, pi.Info);
            args.Add(arg);
            fvars.Add(arg);
            itType = Whnf(ExprOps.Instantiate1(pi.Body, arg));
        }
        return Lctx.MkLambda(fvars, Expr.MkApp(it, args));
    }
}
