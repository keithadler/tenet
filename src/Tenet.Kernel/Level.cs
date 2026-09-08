namespace Tenet.Kernel;

public enum LevelKind : byte
{
    Zero,
    Succ,
    Max,
    IMax,
    Param,
}

/// <summary>
/// A universe level: zero, successor, max, impredicative max, or a universe parameter.
/// Levels are immutable, compare structurally, and cache their hash, depth, and whether they mention a parameter.
/// </summary>
public abstract class Level : IEquatable<Level>
{
    public static readonly Level Zero = new ZeroLevel();
    public static readonly Level One = new SuccLevel(Zero);

    public readonly LevelKind Kind;
    public readonly int Hash;
    /// <summary>Height of the level expression tree.</summary>
    public readonly int Depth;
    public readonly bool HasParam;

    private protected Level(LevelKind kind, int hash, int depth, bool hasParam)
    {
        Kind = kind;
        Hash = hash;
        Depth = depth;
        HasParam = hasParam;
    }

    // ---- raw constructors (no simplification; used when reading exported data) ----

    public static Level Succ(Level l) => new SuccLevel(l);
    public static Level MaxRaw(Level a, Level b) => new MaxLevel(a, b);
    public static Level IMaxRaw(Level a, Level b) => new IMaxLevel(a, b);
    public static Level Param(Name n) => new ParamLevel(n);

    // ---- simplifying constructors (used when the kernel builds levels itself) ----

    /// <summary>Iterated successor.</summary>
    public static Level MkSucc(Level l, int k)
    {
        for (int i = 0; i < k; i++)
        {
            l = new SuccLevel(l);
        }
        return l;
    }

    /// <summary>Simplifying max, as the Lean kernel's <c>mk_max</c>.</summary>
    public static Level MkMax(Level l1, Level l2)
    {
        if (l1.IsExplicit && l2.IsExplicit)
        {
            return l1.Depth >= l2.Depth ? l1 : l2;
        }
        if (l1.Equals(l2))
        {
            return l1;
        }
        if (l1.Kind == LevelKind.Zero)
        {
            return l2;
        }
        if (l2.Kind == LevelKind.Zero)
        {
            return l1;
        }
        if (l2 is MaxLevel m2 && (m2.Lhs.Equals(l1) || m2.Rhs.Equals(l1)))
        {
            return l2;
        }
        if (l1 is MaxLevel m1 && (m1.Lhs.Equals(l2) || m1.Rhs.Equals(l2)))
        {
            return l1;
        }
        var (b1, k1) = l1.ToOffset();
        var (b2, k2) = l2.ToOffset();
        if (b1.Equals(b2))
        {
            return k1 > k2 ? l1 : l2;
        }
        return new MaxLevel(l1, l2);
    }

    /// <summary>Simplifying imax, as the Lean kernel's <c>mk_imax</c>.</summary>
    public static Level MkIMax(Level l1, Level l2)
    {
        if (l2.IsNotZero())
        {
            return MkMax(l1, l2);
        }
        if (l2.Kind == LevelKind.Zero)
        {
            return l2;
        }
        if (l1.Kind == LevelKind.Zero || l1.Equals(One))
        {
            return l2;
        }
        if (l1.Equals(l2))
        {
            return l1;
        }
        return new IMaxLevel(l1, l2);
    }

    /// <summary>True when the level is <c>succ^k zero</c> for some k.</summary>
    public bool IsExplicit
    {
        get
        {
            Level l = this;
            while (l is SuccLevel s)
            {
                l = s.Of;
            }
            return l.Kind == LevelKind.Zero;
        }
    }

    /// <summary>Decompose <c>succ^k l</c> into <c>(l, k)</c>.</summary>
    public (Level Base, int Offset) ToOffset()
    {
        Level l = this;
        int k = 0;
        while (l is SuccLevel s)
        {
            l = s.Of;
            k++;
        }
        return (l, k);
    }

    /// <summary>True when the level is provably nonzero for every parameter assignment.</summary>
    public bool IsNotZero() => this switch
    {
        SuccLevel => true,
        MaxLevel m => m.Lhs.IsNotZero() || m.Rhs.IsNotZero(),
        IMaxLevel m => m.Rhs.IsNotZero(),
        _ => false,
    };

    /// <summary>True when the level normalizes to zero (denotes <c>Prop</c>).</summary>
    public bool NormalizesToZero() => this switch
    {
        ZeroLevel => true,
        MaxLevel m => m.Lhs.NormalizesToZero() && m.Rhs.NormalizesToZero(),
        IMaxLevel m => m.Rhs.NormalizesToZero(),
        _ => false,
    };

    /// <summary>Replace parameters by levels, position-wise.</summary>
    public Level Instantiate(IReadOnlyList<Name> ps, IReadOnlyList<Level> ls)
    {
        if (!HasParam)
        {
            return this;
        }
        switch (this)
        {
            case ParamLevel p:
                for (int i = 0; i < ps.Count && i < ls.Count; i++)
                {
                    if (ps[i].Equals(p.Name))
                    {
                        return ls[i];
                    }
                }
                return this;
            case SuccLevel s:
                {
                    Level n = s.Of.Instantiate(ps, ls);
                    return ReferenceEquals(n, s.Of) ? this : new SuccLevel(n);
                }
            case MaxLevel m:
                {
                    Level a = m.Lhs.Instantiate(ps, ls);
                    Level b = m.Rhs.Instantiate(ps, ls);
                    return ReferenceEquals(a, m.Lhs) && ReferenceEquals(b, m.Rhs) ? this : MkMax(a, b);
                }
            case IMaxLevel m:
                {
                    Level a = m.Lhs.Instantiate(ps, ls);
                    Level b = m.Rhs.Instantiate(ps, ls);
                    return ReferenceEquals(a, m.Lhs) && ReferenceEquals(b, m.Rhs) ? this : MkIMax(a, b);
                }
            default:
                return this;
        }
    }

    /// <summary>The first parameter occurring in this level that is not in <paramref name="ps"/>, or null.</summary>
    public Name? GetUndefParam(IReadOnlyList<Name> ps)
    {
        if (!HasParam)
        {
            return null;
        }
        switch (this)
        {
            case ParamLevel p:
                foreach (Name n in ps)
                {
                    if (n.Equals(p.Name))
                    {
                        return null;
                    }
                }
                return p.Name;
            case SuccLevel s:
                return s.Of.GetUndefParam(ps);
            case MaxLevel m:
                return m.Lhs.GetUndefParam(ps) ?? m.Rhs.GetUndefParam(ps);
            case IMaxLevel m:
                return m.Lhs.GetUndefParam(ps) ?? m.Rhs.GetUndefParam(ps);
            default:
                return null;
        }
    }

    // ---- normalization and ordering ----

    /// <summary>Structural equality up to normalization: the kernel's notion of level equality.</summary>
    public static bool IsEquiv(Level a, Level b) => a.Equals(b) || a.Normalize().Equals(b.Normalize());

    /// <summary>True when <paramref name="l1"/> is at least <paramref name="l2"/> for every parameter assignment (sound, incomplete).</summary>
    public static bool IsGeq(Level l1, Level l2) => IsGeqCore(l1.Normalize(), l2.Normalize());

    private static bool IsGeqCore(Level l1, Level l2)
    {
        if (l1.Equals(l2) || l2.Kind == LevelKind.Zero)
        {
            return true;
        }
        if (l2 is MaxLevel m2)
        {
            return IsGeq(l1, m2.Lhs) && IsGeq(l1, m2.Rhs);
        }
        if (l1 is MaxLevel m1 && (IsGeq(m1.Lhs, l2) || IsGeq(m1.Rhs, l2)))
        {
            return true;
        }
        if (l2 is IMaxLevel im2)
        {
            return IsGeq(l1, im2.Lhs) && IsGeq(l1, im2.Rhs);
        }
        if (l1 is IMaxLevel im1)
        {
            return IsGeq(im1.Rhs, l2);
        }
        var (b1, k1) = l1.ToOffset();
        var (b2, k2) = l2.ToOffset();
        if (b1.Equals(b2) || b2.Kind == LevelKind.Zero)
        {
            return k1 >= k2;
        }
        if (k1 == k2 && k1 > 0)
        {
            return IsGeq(b1, b2);
        }
        return false;
    }

    /// <summary>Put a level into the kernel's normal form.</summary>
    public Level Normalize()
    {
        var (r, k) = ToOffset();
        switch (r)
        {
            case ZeroLevel:
            case ParamLevel:
                return this;
            case IMaxLevel im:
                return MkSucc(MkIMax(im.Lhs.Normalize(), im.Rhs.Normalize()), k);
            case MaxLevel:
                {
                    var todo = new List<Level>();
                    PushMaxArgs(r, todo);
                    var args = new List<Level>();
                    foreach (Level a in todo)
                    {
                        PushMaxArgs(a.Normalize(), args);
                    }
                    args.Sort(NormLtComparer.Instance);
                    var rargs = new List<Level>();
                    int i = 0;
                    if (args[i].IsExplicit)
                    {
                        while (i + 1 < args.Count && args[i + 1].IsExplicit)
                        {
                            i++;
                        }
                        int kk = args[i].ToOffset().Offset;
                        int j = i + 1;
                        for (; j < args.Count; j++)
                        {
                            if (args[j].ToOffset().Offset >= kk)
                            {
                                break;
                            }
                        }
                        if (j < args.Count)
                        {
                            i++;
                        }
                    }
                    rargs.Add(args[i]);
                    var prev = args[i].ToOffset();
                    i++;
                    for (; i < args.Count; i++)
                    {
                        var curr = args[i].ToOffset();
                        if (prev.Base.Equals(curr.Base))
                        {
                            if (prev.Offset < curr.Offset)
                            {
                                prev = curr;
                                rargs[rargs.Count - 1] = args[i];
                            }
                        }
                        else
                        {
                            prev = curr;
                            rargs.Add(args[i]);
                        }
                    }
                    for (int t = 0; t < rargs.Count; t++)
                    {
                        rargs[t] = MkSucc(rargs[t], k);
                    }
                    return MkMaxList(rargs);
                }
            default:
                throw new InvalidOperationException();
        }
    }

    private static void PushMaxArgs(Level l, List<Level> r)
    {
        if (l is MaxLevel m)
        {
            PushMaxArgs(m.Lhs, r);
            PushMaxArgs(m.Rhs, r);
        }
        else
        {
            r.Add(l);
        }
    }

    private static Level MkMaxList(List<Level> args)
    {
        int n = args.Count;
        if (n == 1)
        {
            return args[0];
        }
        Level r = MkMax(args[n - 2], args[n - 1]);
        for (int i = n - 3; i >= 0; i--)
        {
            r = MkMax(args[i], r);
        }
        return r;
    }

    /// <summary>The total order used by normalization: succ is the immediate successor, zero is least.</summary>
    private static bool IsNormLt(Level a, Level b)
    {
        if (ReferenceEquals(a, b))
        {
            return false;
        }
        var (l1, k1) = a.ToOffset();
        var (l2, k2) = b.ToOffset();
        if (!l1.Equals(l2))
        {
            if (l1.Kind != l2.Kind)
            {
                return l1.Kind < l2.Kind;
            }
            switch (l1)
            {
                case ParamLevel p1:
                    return p1.Name.CompareTo(((ParamLevel)l2).Name) < 0;
                case MaxLevel m1:
                    {
                        var m2 = (MaxLevel)l2;
                        return m1.Lhs.Equals(m2.Lhs) ? IsNormLt(m1.Rhs, m2.Rhs) : IsNormLt(m1.Lhs, m2.Lhs);
                    }
                case IMaxLevel im1:
                    {
                        var im2 = (IMaxLevel)l2;
                        return im1.Lhs.Equals(im2.Lhs) ? IsNormLt(im1.Rhs, im2.Rhs) : IsNormLt(im1.Lhs, im2.Lhs);
                    }
                default:
                    throw new InvalidOperationException();
            }
        }
        return k1 < k2;
    }

    private sealed class NormLtComparer : IComparer<Level>
    {
        public static readonly NormLtComparer Instance = new();
        public int Compare(Level? x, Level? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }
            if (x is null)
            {
                return -1;
            }
            if (y is null)
            {
                return 1;
            }
            if (IsNormLt(x, y))
            {
                return -1;
            }
            return IsNormLt(y, x) ? 1 : 0;
        }
    }

    // ---- equality ----

    public bool Equals(Level? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (other is null || Kind != other.Kind || Hash != other.Hash || Depth != other.Depth)
        {
            return false;
        }
        return (this, other) switch
        {
            (ZeroLevel, ZeroLevel) => true,
            (ParamLevel a, ParamLevel b) => a.Name.Equals(b.Name),
            (SuccLevel a, SuccLevel b) => a.Of.Equals(b.Of),
            (MaxLevel a, MaxLevel b) => a.Lhs.Equals(b.Lhs) && a.Rhs.Equals(b.Rhs),
            (IMaxLevel a, IMaxLevel b) => a.Lhs.Equals(b.Lhs) && a.Rhs.Equals(b.Rhs),
            _ => false,
        };
    }

    public override bool Equals(object? obj) => obj is Level l && Equals(l);
    public override int GetHashCode() => Hash;

    public static bool ListEquals(IReadOnlyList<Level> a, IReadOnlyList<Level> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }
        return true;
    }

    public static bool ListHasParam(IReadOnlyList<Level> ls)
    {
        foreach (Level l in ls)
        {
            if (l.HasParam)
            {
                return true;
            }
        }
        return false;
    }

    public override string ToString()
    {
        if (IsExplicit)
        {
            return Depth.ToString();
        }
        switch (this)
        {
            case ParamLevel p:
                return p.Name.ToString();
            case SuccLevel s:
                {
                    var (b, k) = ToOffset();
                    return Child(b) + "+" + k;
                }
            case MaxLevel m:
                return "max " + Child(m.Lhs) + " " + Child(m.Rhs);
            case IMaxLevel m:
                return "imax " + Child(m.Lhs) + " " + Child(m.Rhs);
            default:
                return "0";
        }

        static string Child(Level l) => l.IsExplicit || l is ParamLevel ? l.ToString() : "(" + l + ")";
    }
}

public sealed class ZeroLevel : Level
{
    internal ZeroLevel() : base(LevelKind.Zero, 0x2a, 0, false) { }
}

public sealed class SuccLevel : Level
{
    public readonly Level Of;
    internal SuccLevel(Level of) : base(LevelKind.Succ, HashCode.Combine(1, of.Hash), of.Depth + 1, of.HasParam) => Of = of;
}

public sealed class MaxLevel : Level
{
    public readonly Level Lhs;
    public readonly Level Rhs;
    internal MaxLevel(Level lhs, Level rhs)
        : base(LevelKind.Max, HashCode.Combine(2, lhs.Hash, rhs.Hash), Math.Max(lhs.Depth, rhs.Depth) + 1, lhs.HasParam || rhs.HasParam)
    {
        Lhs = lhs;
        Rhs = rhs;
    }
}

public sealed class IMaxLevel : Level
{
    public readonly Level Lhs;
    public readonly Level Rhs;
    internal IMaxLevel(Level lhs, Level rhs)
        : base(LevelKind.IMax, HashCode.Combine(3, lhs.Hash, rhs.Hash), Math.Max(lhs.Depth, rhs.Depth) + 1, lhs.HasParam || rhs.HasParam)
    {
        Lhs = lhs;
        Rhs = rhs;
    }
}

public sealed class ParamLevel : Level
{
    public readonly Name Name;
    internal ParamLevel(Name name) : base(LevelKind.Param, HashCode.Combine(4, name.GetHashCode()), 0, true) => Name = name;
}
