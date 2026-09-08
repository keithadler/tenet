using System.Numerics;
using System.Runtime.CompilerServices;

namespace Tenet.Kernel;

public enum ExprKind : byte
{
    BVar,
    FVar,
    Sort,
    Const,
    App,
    Lam,
    Pi,
    Let,
    Lit,
    Proj,
}

public enum BinderInfo : byte
{
    Default,
    Implicit,
    StrictImplicit,
    InstImplicit,
}

/// <summary>Identity of a free variable. Fresh identities are globally unique within a process.</summary>
public readonly record struct FVarId(ulong Value)
{
    private static int s_nextThread;
    [ThreadStatic] private static ulong t_prefix;
    [ThreadStatic] private static ulong t_next;

    /// <summary>Globally unique without contention: each thread owns a block of identifiers.</summary>
    public static FVarId Fresh()
    {
        if (t_prefix == 0)
        {
            t_prefix = (ulong)Interlocked.Increment(ref s_nextThread) << 40;
        }
        return new(t_prefix | ++t_next);
    }

    public override string ToString() => "_fvar." + Value;
}

/// <summary>A literal: a natural number or a string.</summary>
public abstract class Literal : IEquatable<Literal>
{
    public abstract Expr Type { get; }
    public abstract bool Equals(Literal? other);
    public override bool Equals(object? obj) => obj is Literal l && Equals(l);
    public abstract override int GetHashCode();
}

public sealed class NatLiteral : Literal
{
    public readonly BigInteger Value;
    public NatLiteral(BigInteger value) => Value = value;
    public override Expr Type => Expr.NatType;
    public override bool Equals(Literal? other) => other is NatLiteral n && n.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value.ToString();
}

public sealed class StrLiteral : Literal
{
    public readonly string Value;
    public StrLiteral(string value) => Value = value;
    public override Expr Type => Expr.StringType;
    public override bool Equals(Literal? other) => other is StrLiteral s && s.Value == Value;
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
    public override string ToString() => "\"" + Value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

/// <summary>
/// A kernel expression in locally nameless form. Immutable. Structural equality ignores binder names and binder
/// annotations, exactly as Lean's kernel does; the hash is consistent with that equality.
/// </summary>
public abstract class Expr : IEquatable<Expr>
{
    public readonly ExprKind Kind;
    public readonly int Hash;
    /// <summary>One more than the largest loose bound variable index, or zero if the expression is closed.</summary>
    public readonly int LooseBVarRange;
    public readonly bool HasFVar;
    public readonly bool HasLevelParam;

    private protected Expr(ExprKind kind, int hash, int looseBVarRange, bool hasFVar, bool hasLevelParam)
    {
        Kind = kind;
        Hash = hash;
        LooseBVarRange = looseBVarRange;
        HasFVar = hasFVar;
        HasLevelParam = hasLevelParam;
    }

    public bool HasLooseBVars => LooseBVarRange > 0;

    // ---- well-known constants ----

    public static readonly Expr Prop = Sort(Level.Zero);
    public static readonly Expr Type0 = Sort(Level.One);
    public static readonly Expr NatType = Const(Name.Of("Nat"), []);
    public static readonly Expr StringType = Const(Name.Of("String"), []);

    // ---- constructors ----

    public static Expr BVar(int idx) => idx < BVarCache.Length ? BVarCache[idx] : new BVarExpr(idx);
    private static readonly BVarExpr[] BVarCache = CreateBVarCache();
    private static BVarExpr[] CreateBVarCache()
    {
        var arr = new BVarExpr[64];
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i] = new BVarExpr(i);
        }
        return arr;
    }

    public static Expr FVar(FVarId id) => new FVarExpr(id);
    public static Expr Sort(Level l) => new SortExpr(l);
    public static Expr Const(Name name, Level[] levels) => new ConstExpr(name, levels);
    public static Expr App(Expr fn, Expr arg) => new AppExpr(fn, arg);
    public static Expr Lam(Name binderName, Expr domain, Expr body, BinderInfo info = BinderInfo.Default) => new LamExpr(binderName, domain, body, info);
    public static Expr Pi(Name binderName, Expr domain, Expr body, BinderInfo info = BinderInfo.Default) => new PiExpr(binderName, domain, body, info);
    public static Expr Let(Name name, Expr type, Expr value, Expr body, bool nonDep = false) => new LetExpr(name, type, value, body, nonDep);
    public static Expr Lit(Literal lit) => new LitExpr(lit);
    public static Expr NatLit(BigInteger n) => new LitExpr(new NatLiteral(n));
    public static Expr StrLit(string s) => new LitExpr(new StrLiteral(s));
    public static Expr Proj(Name structName, int idx, Expr e) => new ProjExpr(structName, idx, e);

    /// <summary>A non-dependent function type <c>a → b</c>.</summary>
    public static Expr Arrow(Expr a, Expr b) => Pi(DefaultBinderName, a, b);
    private static readonly Name DefaultBinderName = Name.Of("a");

    /// <summary>Apply <paramref name="f"/> to <paramref name="args"/> in order.</summary>
    public static Expr MkApp(Expr f, ReadOnlySpan<Expr> args)
    {
        foreach (Expr a in args)
        {
            f = App(f, a);
        }
        return f;
    }

    public static Expr MkApp(Expr f, IEnumerable<Expr> args)
    {
        foreach (Expr a in args)
        {
            f = App(f, a);
        }
        return f;
    }

    public static Expr MkApp(Expr f, params Expr[] args) => MkApp(f, args.AsSpan());

    /// <summary>Apply <paramref name="f"/> to arguments given in reverse order (last argument first).</summary>
    public static Expr MkRevApp(Expr f, ReadOnlySpan<Expr> revArgs)
    {
        for (int i = revArgs.Length - 1; i >= 0; i--)
        {
            f = App(f, revArgs[i]);
        }
        return f;
    }

    // ---- application spine helpers ----

    public Expr GetAppFn()
    {
        Expr e = this;
        while (e is AppExpr a)
        {
            e = a.Fn;
        }
        return e;
    }

    public int GetAppNumArgs()
    {
        int n = 0;
        for (Expr e = this; e is AppExpr a; e = a.Fn)
        {
            n++;
        }
        return n;
    }

    /// <summary>Return the head of the application spine, storing arguments in order.</summary>
    public Expr GetAppArgs(out Expr[] args)
    {
        int n = GetAppNumArgs();
        args = new Expr[n];
        Expr e = this;
        for (int i = n - 1; i >= 0; i--)
        {
            var a = (AppExpr)e;
            args[i] = a.Arg;
            e = a.Fn;
        }
        return e;
    }

    /// <summary>Return the head of the application spine, storing arguments in reverse order.</summary>
    public Expr GetAppRevArgs(out Expr[] revArgs)
    {
        int n = GetAppNumArgs();
        revArgs = new Expr[n];
        Expr e = this;
        for (int i = 0; i < n; i++)
        {
            var a = (AppExpr)e;
            revArgs[i] = a.Arg;
            e = a.Fn;
        }
        return e;
    }

    public bool IsConstOf(Name n) => this is ConstExpr c && c.Name.Equals(n);
    public bool IsAppOf(Name n) => GetAppFn().IsConstOf(n);
    public bool IsAppOfArity(Name n, int arity) => GetAppNumArgs() == arity && GetAppFn().IsConstOf(n);
    public bool IsBVarOf(int idx) => this is BVarExpr b && b.Idx == idx;
    public bool IsNatLit => this is LitExpr { Value: NatLiteral };
    public bool IsStrLit => this is LitExpr { Value: StrLiteral };

    // ---- equality ----

    public bool Equals(Expr? other) => other is not null && Eq(this, other);
    public override bool Equals(object? obj) => obj is Expr e && Eq(this, e);
    public override int GetHashCode() => Hash;

    /// <summary>Structural equality ignoring binder names and binder infos.</summary>
    public static bool Eq(Expr a, Expr b)
    {
        var fn = new EqFn();
        return fn.Apply(a, b);
    }

    private struct EqFn
    {
        private HashSet<(Expr, Expr)>? _cache;
        private int _steps;

        public bool Apply(Expr a, Expr b)
        {
            while (true)
            {
                if (ReferenceEquals(a, b))
                {
                    return true;
                }
                if (a.Hash != b.Hash || a.Kind != b.Kind)
                {
                    return false;
                }
                switch (a)
                {
                    case BVarExpr x:
                        return x.Idx == ((BVarExpr)b).Idx;
                    case LitExpr x:
                        return x.Value.Equals(((LitExpr)b).Value);
                    case FVarExpr x:
                        return x.Id == ((FVarExpr)b).Id;
                    case SortExpr x:
                        return x.Level.Equals(((SortExpr)b).Level);
                    case ConstExpr x:
                        {
                            var y = (ConstExpr)b;
                            return x.Name.Equals(y.Name) && Level.ListEquals(x.Levels, y.Levels);
                        }
                }
                // Compound nodes. After enough work we assume the inputs are shared DAGs and start
                // caching visited pairs, which bounds the total work by the DAG size.
                if (++_steps > 4096)
                {
                    _cache ??= new HashSet<(Expr, Expr)>(RefPairComparer.Instance);
                    if (!_cache.Add((a, b)))
                    {
                        return true;
                    }
                }
                switch (a)
                {
                    case ProjExpr x:
                        {
                            var y = (ProjExpr)b;
                            if (x.Idx != y.Idx || !x.StructName.Equals(y.StructName))
                            {
                                return false;
                            }
                            a = x.Struct;
                            b = y.Struct;
                            continue;
                        }
                    case AppExpr x:
                        {
                            var y = (AppExpr)b;
                            if (!Apply(x.Arg, y.Arg))
                            {
                                return false;
                            }
                            a = x.Fn;
                            b = y.Fn;
                            continue;
                        }
                    case BindingExpr x:
                        {
                            var y = (BindingExpr)b;
                            if (!Apply(x.Domain, y.Domain))
                            {
                                return false;
                            }
                            a = x.Body;
                            b = y.Body;
                            continue;
                        }
                    case LetExpr x:
                        {
                            var y = (LetExpr)b;
                            if (x.NonDep != y.NonDep || !Apply(x.Type, y.Type) || !Apply(x.Value, y.Value))
                            {
                                return false;
                            }
                            a = x.Body;
                            b = y.Body;
                            continue;
                        }
                    default:
                        throw new InvalidOperationException();
                }
            }
        }
    }

    internal sealed class RefPairComparer : IEqualityComparer<(Expr, Expr)>
    {
        public static readonly RefPairComparer Instance = new();
        public bool Equals((Expr, Expr) x, (Expr, Expr) y) => ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);
        public int GetHashCode((Expr, Expr) p) => HashCode.Combine(RuntimeHelpers.GetHashCode(p.Item1), RuntimeHelpers.GetHashCode(p.Item2));
    }

    public override string ToString() => ExprPrinter.Print(this);
}

public sealed class BVarExpr : Expr
{
    public readonly int Idx;
    internal BVarExpr(int idx) : base(ExprKind.BVar, HashCode.Combine(11, idx), idx + 1, false, false) => Idx = idx;
}

public sealed class FVarExpr : Expr
{
    public readonly FVarId Id;
    internal FVarExpr(FVarId id) : base(ExprKind.FVar, HashCode.Combine(13, id.Value), 0, true, false) => Id = id;
}

public sealed class SortExpr : Expr
{
    public readonly Level Level;
    internal SortExpr(Level level) : base(ExprKind.Sort, HashCode.Combine(17, level.Hash), 0, false, level.HasParam) => Level = level;
}

public sealed class ConstExpr : Expr
{
    public readonly Name Name;
    public readonly Level[] Levels;
    internal ConstExpr(Name name, Level[] levels)
        : base(ExprKind.Const, ComputeHash(name, levels), 0, false, Level.ListHasParam(levels))
    {
        Name = name;
        Levels = levels;
    }

    private static int ComputeHash(Name name, Level[] levels)
    {
        var h = new HashCode();
        h.Add(19);
        h.Add(name.GetHashCode());
        foreach (Level l in levels)
        {
            h.Add(l.Hash);
        }
        return h.ToHashCode();
    }
}

public sealed class AppExpr : Expr
{
    public readonly Expr Fn;
    public readonly Expr Arg;
    internal AppExpr(Expr fn, Expr arg)
        : base(ExprKind.App, HashCode.Combine(23, fn.Hash, arg.Hash), Math.Max(fn.LooseBVarRange, arg.LooseBVarRange),
               fn.HasFVar || arg.HasFVar, fn.HasLevelParam || arg.HasLevelParam)
    {
        Fn = fn;
        Arg = arg;
    }
}

/// <summary>Common shape of lambda and Pi.</summary>
public abstract class BindingExpr : Expr
{
    public readonly Name BinderName;
    public readonly Expr Domain;
    public readonly Expr Body;
    public readonly BinderInfo Info;

    private protected BindingExpr(ExprKind kind, int salt, Name binderName, Expr domain, Expr body, BinderInfo info)
        : base(kind, HashCode.Combine(salt, domain.Hash, body.Hash),
               Math.Max(domain.LooseBVarRange, Math.Max(body.LooseBVarRange - 1, 0)),
               domain.HasFVar || body.HasFVar, domain.HasLevelParam || body.HasLevelParam)
    {
        BinderName = binderName;
        Domain = domain;
        Body = body;
        Info = info;
    }

    public bool IsLambda => Kind == ExprKind.Lam;

    /// <summary>Rebuild with a new domain and body, preserving name and info; returns this when unchanged.</summary>
    public Expr Update(Expr domain, Expr body)
    {
        if (ReferenceEquals(domain, Domain) && ReferenceEquals(body, Body))
        {
            return this;
        }
        return IsLambda ? Lam(BinderName, domain, body, Info) : Pi(BinderName, domain, body, Info);
    }

    public Expr Update(Expr domain, Expr body, BinderInfo info)
    {
        if (ReferenceEquals(domain, Domain) && ReferenceEquals(body, Body) && info == Info)
        {
            return this;
        }
        return IsLambda ? Lam(BinderName, domain, body, info) : Pi(BinderName, domain, body, info);
    }
}

public sealed class LamExpr : BindingExpr
{
    internal LamExpr(Name binderName, Expr domain, Expr body, BinderInfo info) : base(ExprKind.Lam, 29, binderName, domain, body, info) { }
}

public sealed class PiExpr : BindingExpr
{
    internal PiExpr(Name binderName, Expr domain, Expr body, BinderInfo info) : base(ExprKind.Pi, 31, binderName, domain, body, info) { }
}

public sealed class LetExpr : Expr
{
    public readonly Name Name;
    public readonly Expr Type;
    public readonly Expr Value;
    public readonly Expr Body;
    public readonly bool NonDep;

    internal LetExpr(Name name, Expr type, Expr value, Expr body, bool nonDep)
        : base(ExprKind.Let, HashCode.Combine(37, type.Hash, value.Hash, body.Hash),
               Math.Max(Math.Max(type.LooseBVarRange, value.LooseBVarRange), Math.Max(body.LooseBVarRange - 1, 0)),
               type.HasFVar || value.HasFVar || body.HasFVar, type.HasLevelParam || value.HasLevelParam || body.HasLevelParam)
    {
        Name = name;
        Type = type;
        Value = value;
        Body = body;
        NonDep = nonDep;
    }

    public Expr Update(Expr type, Expr value, Expr body) =>
        ReferenceEquals(type, Type) && ReferenceEquals(value, Value) && ReferenceEquals(body, Body) ? this : Let(Name, type, value, body, NonDep);
}

public sealed class LitExpr : Expr
{
    public readonly Literal Value;
    internal LitExpr(Literal value) : base(ExprKind.Lit, HashCode.Combine(41, value.GetHashCode()), 0, false, false) => Value = value;
}

public sealed class ProjExpr : Expr
{
    public readonly Name StructName;
    public readonly int Idx;
    public readonly Expr Struct;

    internal ProjExpr(Name structName, int idx, Expr e)
        : base(ExprKind.Proj, HashCode.Combine(43, structName.GetHashCode(), idx, e.Hash), e.LooseBVarRange, e.HasFVar, e.HasLevelParam)
    {
        StructName = structName;
        Idx = idx;
        Struct = e;
    }

    public Expr Update(Expr e) => ReferenceEquals(e, Struct) ? this : Proj(StructName, Idx, e);
}
