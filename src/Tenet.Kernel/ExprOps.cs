using System.Runtime.CompilerServices;

namespace Tenet.Kernel;

/// <summary>
/// Traversals and substitutions on expressions: replace, instantiate, abstract, lift, level instantiation,
/// beta reduction, and the small syntactic helpers the type checker and the inductive module need.
/// </summary>
public static class ExprOps
{
    private sealed class RefOffsetComparer : IEqualityComparer<(Expr, int)>
    {
        public static readonly RefOffsetComparer Instance = new();
        public bool Equals((Expr, int) x, (Expr, int) y) => ReferenceEquals(x.Item1, y.Item1) && x.Item2 == y.Item2;
        public int GetHashCode((Expr, int) p) => HashCode.Combine(RuntimeHelpers.GetHashCode(p.Item1), p.Item2);
    }

    /// <summary>
    /// Rebuild <paramref name="e"/> bottom-up. <paramref name="f"/> receives each subterm with the number of binders
    /// above it; returning non-null replaces that subterm without descending. Results for shared subterms are cached.
    /// </summary>
    public static Expr Replace(Expr e, Func<Expr, int, Expr?> f)
    {
        var cache = new Dictionary<(Expr, int), Expr>(RefOffsetComparer.Instance);
        return Go(e, 0);

        Expr Go(Expr t, int offset)
        {
            bool compound = t.Kind is ExprKind.App or ExprKind.Lam or ExprKind.Pi or ExprKind.Let or ExprKind.Proj;
            if (compound && cache.TryGetValue((t, offset), out Expr? hit))
            {
                return hit;
            }
            Expr? r = f(t, offset);
            if (r is null)
            {
                switch (t)
                {
                    case AppExpr a:
                        {
                            Expr nf = Go(a.Fn, offset);
                            Expr na = Go(a.Arg, offset);
                            r = ReferenceEquals(nf, a.Fn) && ReferenceEquals(na, a.Arg) ? t : Expr.App(nf, na);
                            break;
                        }
                    case BindingExpr b:
                        r = b.Update(Go(b.Domain, offset), Go(b.Body, offset + 1));
                        break;
                    case LetExpr l:
                        r = l.Update(Go(l.Type, offset), Go(l.Value, offset), Go(l.Body, offset + 1));
                        break;
                    case ProjExpr p:
                        r = p.Update(Go(p.Struct, offset));
                        break;
                    default:
                        r = t;
                        break;
                }
            }
            if (compound)
            {
                cache[(t, offset)] = r;
            }
            return r;
        }
    }

    /// <summary>Visit every subterm; the predicate returns false to skip a subterm's children. Shared subterms are visited once.</summary>
    private static string Trunc(string s) => s.Length > 300 ? s[..300] + "…" : s;

    /// <summary>
    /// Describe the first structurally different subterm of two expressions, or <c>"(equal)"</c>. A <c>let</c>'s
    /// <c>nonDep</c> flag is ignored: lean4export writes <c>false</c> for every let, so it cannot be compared against
    /// what an <c>.olean</c> stores. Used to compare a term read from Lean's binary format with the same term as
    /// Lean's own exporter wrote it.
    /// </summary>
    public static string FirstDifference(Expr a, Expr b, string path = "")
    {
        if (a.Equals(b))
        {
            return "(equal)";
        }
        if (a is LetExpr && b is LetExpr)
        {
            // fall through to the field-wise comparison below, which ignores nonDep
        }
        else if (a.Kind != b.Kind)
        {
            return $"{path}: kinds {a.Kind} vs {b.Kind}\n  a: {Trunc(a.ToString())}\n  b: {Trunc(b.ToString())}";
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
                    if (!x.BinderName.Equals(y.BinderName) || x.Info != y.Info)
                    {
                        return $"{path}: binder {x.BinderName}/{x.Info} vs {y.BinderName}/{y.Info}";
                    }
                    string d = FirstDifference(x.Domain, y.Domain, path + "/domain");
                    return d != "(equal)" ? d : FirstDifference(x.Body, y.Body, path + "/body");
                }
            case LetExpr x when b is LetExpr y:
                {
                    string t = FirstDifference(x.Type, y.Type, path + "/type");
                    if (t != "(equal)")
                    {
                        return t;
                    }
                    string v = FirstDifference(x.Value, y.Value, path + "/value");
                    return v != "(equal)" ? v : FirstDifference(x.Body, y.Body, path + "/body");
                }
            case ProjExpr x when b is ProjExpr y:
                return x.Idx != y.Idx || !x.StructName.Equals(y.StructName)
                    ? $"{path}: proj {x.StructName}.{x.Idx} vs {y.StructName}.{y.Idx}"
                    : FirstDifference(x.Struct, y.Struct, path + "/struct");
            default:
                return $"{path}: {a.Kind}\n  a: {Trunc(a.ToString())}\n  b: {Trunc(b.ToString())}";
        }
    }

    public static void ForEach(Expr e, Func<Expr, int, bool> f)
    {
        var visited = new HashSet<(Expr, int)>(RefOffsetComparer.Instance);
        Go(e, 0);

        void Go(Expr t, int offset)
        {
            bool compound = t.Kind is ExprKind.App or ExprKind.Lam or ExprKind.Pi or ExprKind.Let or ExprKind.Proj;
            if (compound && !visited.Add((t, offset)))
            {
                return;
            }
            if (!f(t, offset))
            {
                return;
            }
            switch (t)
            {
                case AppExpr a:
                    Go(a.Fn, offset);
                    Go(a.Arg, offset);
                    break;
                case BindingExpr b:
                    Go(b.Domain, offset);
                    Go(b.Body, offset + 1);
                    break;
                case LetExpr l:
                    Go(l.Type, offset);
                    Go(l.Value, offset);
                    Go(l.Body, offset + 1);
                    break;
                case ProjExpr p:
                    Go(p.Struct, offset);
                    break;
            }
        }
    }

    /// <summary>True when some subterm satisfies the predicate.</summary>
    public static bool Find(Expr e, Func<Expr, int, bool> pred)
    {
        bool found = false;
        ForEach(e, (t, off) =>
        {
            if (found)
            {
                return false;
            }
            if (pred(t, off))
            {
                found = true;
                return false;
            }
            return true;
        });
        return found;
    }

    /// <summary>Does the loose bound variable <paramref name="i"/> occur in <paramref name="e"/>?</summary>
    public static bool HasLooseBVar(Expr e, int i)
    {
        if (i >= e.LooseBVarRange)
        {
            return false;
        }
        return Find(e, (t, off) =>
        {
            if (i + off >= t.LooseBVarRange)
            {
                return false;
            }
            return t is BVarExpr b && b.Idx == i + off;
        });
    }

    /// <summary>Add <paramref name="d"/> to every loose bound variable with index at least <paramref name="s"/>.</summary>
    public static Expr LiftLooseBVars(Expr e, int s, int d)
    {
        if (d == 0 || s >= e.LooseBVarRange)
        {
            return e;
        }
        return Replace(e, (t, off) =>
        {
            if (s + off >= t.LooseBVarRange)
            {
                return t;
            }
            if (t is BVarExpr b && b.Idx >= s + off)
            {
                return Expr.BVar(b.Idx + d);
            }
            return null;
        });
    }

    public static Expr LiftLooseBVars(Expr e, int d) => LiftLooseBVars(e, 0, d);

    /// <summary>Subtract <paramref name="d"/> from every loose bound variable with index at least <paramref name="s"/>.</summary>
    public static Expr LowerLooseBVars(Expr e, int s, int d)
    {
        if (d == 0 || s >= e.LooseBVarRange)
        {
            return e;
        }
        return Replace(e, (t, off) =>
        {
            if (s + off >= t.LooseBVarRange)
            {
                return t;
            }
            if (t is BVarExpr b && b.Idx >= s + off)
            {
                return Expr.BVar(b.Idx - d);
            }
            return null;
        });
    }

    /// <summary>
    /// Replace loose bound variables <c>s, s+1, …, s+n-1</c> by <c>subst[0], …, subst[n-1]</c> (variable <c>s+i</c> gets
    /// <c>subst[i]</c>); higher variables are lowered by <c>n</c>.
    /// </summary>
    public static Expr Instantiate(Expr e, int s, Expr[] subst)
    {
        int n = subst.Length;
        if (s >= e.LooseBVarRange || n == 0)
        {
            return e;
        }
        return Replace(e, (m, off) =>
        {
            int s1 = s + off;
            if (s1 >= m.LooseBVarRange)
            {
                return m;
            }
            if (m is BVarExpr b && b.Idx >= s1)
            {
                int h = s1 + n;
                if (b.Idx < h)
                {
                    return LiftLooseBVars(subst[b.Idx - s1], off);
                }
                return Expr.BVar(b.Idx - n);
            }
            return null;
        });
    }

    public static Expr Instantiate(Expr e, Expr[] subst) => Instantiate(e, 0, subst);

    /// <summary>Replace loose bound variable 0 by <paramref name="v"/>.</summary>
    public static Expr Instantiate1(Expr e, Expr v)
    {
        if (!e.HasLooseBVars)
        {
            return e;
        }
        return Instantiate(e, 0, [v]);
    }

    /// <summary>
    /// Instantiate with the substitution reversed: variable <c>i</c> gets <c>subst[n-1-i]</c>. This is the natural
    /// direction when binders were peeled left to right.
    /// </summary>
    public static Expr InstantiateRev(Expr e, ReadOnlySpan<Expr> subst)
    {
        int n = subst.Length;
        if (!e.HasLooseBVars || n == 0)
        {
            return e;
        }
        Expr[] arr = subst.ToArray();
        return Replace(e, (m, off) =>
        {
            if (off >= m.LooseBVarRange)
            {
                return m;
            }
            if (m is BVarExpr b && b.Idx >= off)
            {
                int h = off + n;
                if (b.Idx < h)
                {
                    return LiftLooseBVars(arr[n - (b.Idx - off) - 1], off);
                }
                return Expr.BVar(b.Idx - n);
            }
            return null;
        });
    }

    public static Expr InstantiateRev(Expr e, IReadOnlyList<Expr> subst) => InstantiateRev(e, ToSpan(subst));

    /// <summary>
    /// Replace the free variables <paramref name="fvars"/> by bound variables: <c>fvars[i]</c> becomes
    /// <c>#(n-1-i)</c> at the top level, so that wrapping the result in binders for <c>fvars[0..n)</c> in order closes it.
    /// </summary>
    public static Expr Abstract(Expr e, ReadOnlySpan<Expr> fvars)
    {
        if (!e.HasFVar)
        {
            return e;
        }
        int n = fvars.Length;
        var ids = new FVarId[n];
        for (int i = 0; i < n; i++)
        {
            ids[i] = ((FVarExpr)fvars[i]).Id;
        }
        return Replace(e, (m, off) =>
        {
            if (!m.HasFVar)
            {
                return m;
            }
            if (m is FVarExpr fv)
            {
                for (int i = n - 1; i >= 0; i--)
                {
                    if (ids[i] == fv.Id)
                    {
                        return Expr.BVar(off + n - i - 1);
                    }
                }
            }
            return null;
        });
    }

    public static Expr Abstract(Expr e, IReadOnlyList<Expr> fvars) => Abstract(e, ToSpan(fvars));

    internal static ReadOnlySpan<Expr> ToSpan(IReadOnlyList<Expr> xs) => xs switch
    {
        Expr[] arr => arr,
        List<Expr> list => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list),
        _ => xs.ToArray(),
    };

    /// <summary>Replace universe parameters in sorts and constants.</summary>
    public static Expr InstantiateLevelParams(Expr e, IReadOnlyList<Name> ps, IReadOnlyList<Level> ls)
    {
        if (!e.HasLevelParam || ps.Count == 0)
        {
            return e;
        }
        return Replace(e, (t, _) =>
        {
            if (!t.HasLevelParam)
            {
                return t;
            }
            switch (t)
            {
                case ConstExpr c:
                    {
                        var nl = new Level[c.Levels.Length];
                        bool changed = false;
                        for (int i = 0; i < nl.Length; i++)
                        {
                            nl[i] = c.Levels[i].Instantiate(ps, ls);
                            changed |= !ReferenceEquals(nl[i], c.Levels[i]);
                        }
                        return changed ? Expr.Const(c.Name, nl) : t;
                    }
                case SortExpr s:
                    {
                        Level nl = s.Level.Instantiate(ps, ls);
                        return ReferenceEquals(nl, s.Level) ? t : Expr.Sort(nl);
                    }
                default:
                    return null;
            }
        });
    }

    /// <summary>Beta-reduce the head of an application repeatedly.</summary>
    public static Expr HeadBetaReduce(Expr e)
    {
        while (e is AppExpr && e.GetAppFn() is LamExpr)
        {
            Expr f = e.GetAppRevArgs(out Expr[] revArgs);
            e = ApplyBeta(f, revArgs);
        }
        return e;
    }

    /// <summary>Apply a lambda to reversed arguments, consuming as many binders as possible.</summary>
    private static Expr ApplyBeta(Expr f, Expr[] revArgs)
    {
        int n = revArgs.Length;
        int m = 0;
        Expr body = f;
        while (body is LamExpr l && m < n)
        {
            body = l.Body;
            m++;
        }
        // The m consumed arguments are the first m in application order = revArgs[n-m .. n).
        Expr r = Instantiate(body, 0, revArgs.AsSpan(n - m, m).ToArray());
        return Expr.MkRevApp(r, revArgs.AsSpan(0, n - m));
    }

    /// <summary>The kernel's <c>cheap_beta_reduce</c>: beta-reduce only when the result needs no substitution.</summary>
    public static Expr CheapBetaReduce(Expr e)
    {
        if (e is not AppExpr)
        {
            return e;
        }
        Expr fn = e.GetAppFn();
        if (fn is not LamExpr)
        {
            return e;
        }
        e.GetAppArgs(out Expr[] args);
        int i = 0;
        while (fn is LamExpr l && i < args.Length)
        {
            i++;
            fn = l.Body;
        }
        if (!fn.HasLooseBVars)
        {
            return Expr.MkApp(fn, args.AsSpan(i));
        }
        if (fn is BVarExpr b)
        {
            return Expr.MkApp(args[i - b.Idx - 1], args.AsSpan(i));
        }
        return e;
    }

    private static readonly Name OptParamName = Name.Of("optParam");
    private static readonly Name AutoParamName = Name.Of("autoParam");
    private static readonly Name OutParamName = Name.Of("outParam");
    private static readonly Name SemiOutParamName = Name.Of("semiOutParam");

    /// <summary>Strip <c>optParam</c>, <c>autoParam</c>, <c>outParam</c>, and <c>semiOutParam</c> wrappers.</summary>
    public static Expr ConsumeTypeAnnotations(Expr e)
    {
        while (true)
        {
            if (e.IsAppOfArity(OptParamName, 2) || e.IsAppOfArity(AutoParamName, 2))
            {
                e = ((AppExpr)((AppExpr)e).Fn).Arg;
            }
            else if (e.IsAppOfArity(OutParamName, 1) || e.IsAppOfArity(SemiOutParamName, 1))
            {
                e = ((AppExpr)e).Arg;
            }
            else
            {
                return e;
            }
        }
    }

    /// <summary>Mark as implicit those Pi binders that can be inferred from later explicit binders (the kernel's <c>infer_implicit</c>).</summary>
    public static Expr InferImplicit(Expr t, bool strict) => InferImplicit(t, int.MaxValue, strict);

    public static Expr InferImplicit(Expr t, int numParams, bool strict)
    {
        if (numParams == 0 || t is not PiExpr p)
        {
            return t;
        }
        Expr newBody = InferImplicit(p.Body, numParams - 1, strict);
        if (p.Info != BinderInfo.Default)
        {
            return p.Update(p.Domain, newBody);
        }
        if (HasLooseBVarsInDomain(newBody, 0, strict))
        {
            return p.Update(p.Domain, newBody, BinderInfo.Implicit);
        }
        return p.Update(p.Domain, newBody);
    }

    private static bool HasLooseBVarsInDomain(Expr b, int vidx, bool strict)
    {
        if (b is PiExpr p)
        {
            if (HasLooseBVar(p.Domain, vidx))
            {
                if (p.Info == BinderInfo.Default)
                {
                    return true;
                }
                if (HasLooseBVarsInDomain(p.Body, 0, strict))
                {
                    return true;
                }
            }
            return HasLooseBVarsInDomain(p.Body, vidx + 1, strict);
        }
        return !strict && HasLooseBVar(b, vidx);
    }
}
