namespace Tenet.Kernel;

/// <summary>A free variable's declaration: its user-facing name, type, optional let-value, and binder annotation.</summary>
public sealed class LocalDecl
{
    public readonly FVarId Id;
    public readonly Name UserName;
    public readonly Expr Type;
    public readonly Expr? Value;
    public readonly BinderInfo Info;

    public LocalDecl(FVarId id, Name userName, Expr type, Expr? value, BinderInfo info)
    {
        Id = id;
        UserName = userName;
        Type = type;
        Value = value;
        Info = info;
    }

    public bool IsLet => Value is not null;
}

/// <summary>
/// The local context: declarations of the free variables introduced while checking. Free variable identities are
/// globally unique and declarations never change, so the context only grows; there is no need to save and restore it.
/// </summary>
public sealed class LocalContext
{
    private readonly Dictionary<FVarId, LocalDecl> _decls = new();

    public int Count => _decls.Count;

    /// <summary>Introduce a fresh free variable with the given type and return it as an expression.</summary>
    public Expr MkLocalDecl(Name userName, Expr type, BinderInfo info = BinderInfo.Default)
    {
        var id = FVarId.Fresh();
        _decls[id] = new LocalDecl(id, userName, type, null, info);
        return Expr.FVar(id);
    }

    /// <summary>Introduce a fresh let-bound free variable.</summary>
    public Expr MkLetDecl(Name userName, Expr type, Expr value)
    {
        var id = FVarId.Fresh();
        _decls[id] = new LocalDecl(id, userName, type, value, BinderInfo.Default);
        return Expr.FVar(id);
    }

    public LocalDecl? Find(FVarId id) => _decls.TryGetValue(id, out LocalDecl? d) ? d : null;

    public LocalDecl? Find(Expr fvar) => fvar is FVarExpr f ? Find(f.Id) : null;

    public LocalDecl Get(Expr fvar) => Find(fvar) ?? throw new KernelException("unknown free variable " + fvar);

    /// <summary>Copy all declarations from another context (used to seed a checker with an outer scope).</summary>
    public void AddAll(LocalContext other)
    {
        foreach (var kv in other._decls)
        {
            _decls[kv.Key] = kv.Value;
        }
    }

    /// <summary>Close <paramref name="body"/> under Pi binders for <paramref name="fvars"/>, in order. Let-variables become lets.</summary>
    public Expr MkPi(ReadOnlySpan<Expr> fvars, Expr body, bool removeDeadLet = false) => MkBinding(false, fvars, body, removeDeadLet);

    public Expr MkPi(IReadOnlyList<Expr> fvars, Expr body, bool removeDeadLet = false) => MkPi(ExprOps.ToSpan(fvars), body, removeDeadLet);

    public Expr MkPi(Expr fvar, Expr body) => MkBinding(false, [fvar], body, false);

    /// <summary>Close <paramref name="body"/> under lambda binders for <paramref name="fvars"/>, in order.</summary>
    public Expr MkLambda(ReadOnlySpan<Expr> fvars, Expr body, bool removeDeadLet = false) => MkBinding(true, fvars, body, removeDeadLet);

    public Expr MkLambda(IReadOnlyList<Expr> fvars, Expr body, bool removeDeadLet = false) => MkLambda(ExprOps.ToSpan(fvars), body, removeDeadLet);

    public Expr MkLambda(Expr fvar, Expr body) => MkBinding(true, [fvar], body, false);

    private Expr MkBinding(bool isLambda, ReadOnlySpan<Expr> fvars, Expr body, bool removeDeadLet)
    {
        Expr r = ExprOps.Abstract(body, fvars);
        for (int i = fvars.Length - 1; i >= 0; i--)
        {
            LocalDecl decl = Get(fvars[i]);
            ReadOnlySpan<Expr> prefix = fvars[..i];
            if (decl.Value is Expr val)
            {
                if (!removeDeadLet || ExprOps.HasLooseBVar(r, 0))
                {
                    Expr type = ExprOps.Abstract(decl.Type, prefix);
                    Expr value = ExprOps.Abstract(val, prefix);
                    r = Expr.Let(decl.UserName, type, value, r);
                }
                else
                {
                    r = ExprOps.LowerLooseBVars(r, 1, 1);
                }
            }
            else
            {
                Expr type = ExprOps.Abstract(decl.Type, prefix);
                r = isLambda ? Expr.Lam(decl.UserName, type, r, decl.Info) : Expr.Pi(decl.UserName, type, r, decl.Info);
            }
        }
        return r;
    }
}
