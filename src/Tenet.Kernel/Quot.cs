namespace Tenet.Kernel;

/// <summary>Quotient types: the four built-in constants and their reduction rule.</summary>
public static class Quot
{
    public static readonly Name QuotName = Name.Of("Quot");
    public static readonly Name QuotMk = Name.Of("Quot", "mk");
    public static readonly Name QuotLift = Name.Of("Quot", "lift");
    public static readonly Name QuotInd = Name.Of("Quot", "ind");
    private static readonly Name EqName = Name.Of("Eq");

    public static bool IsQuotDecl(Name n) => n.Equals(QuotName) || n.Equals(QuotMk) || n.Equals(QuotLift) || n.Equals(QuotInd);

    /// <summary>The environment must already contain <c>Eq</c> with exactly the expected shape.</summary>
    private static void CheckEqType(Environment env)
    {
        ConstantInfo eqInfo = env.Find(EqName) ?? throw new KernelException("failed to initialize quot module, environment does not have 'Eq' type");
        if (eqInfo is not InductiveInfo eqVal)
        {
            throw new KernelException("failed to initialize quot module, environment does not have 'Eq' type");
        }
        if (eqInfo.LevelParams.Length != 1)
        {
            throw new KernelException("failed to initialize quot module, unexpected number of universe params at 'Eq' type");
        }
        if (eqVal.Ctors.Length != 1)
        {
            throw new KernelException("failed to initialize quot module, unexpected number of constructors for 'Eq' type");
        }
        var lctx = new LocalContext();
        {
            Level u = Level.Param(eqInfo.LevelParams[0]);
            Expr alpha = lctx.MkLocalDecl(Name.Of("α"), Expr.Sort(u), BinderInfo.Implicit);
            Expr expected = lctx.MkPi(alpha, Expr.Arrow(alpha, Expr.Arrow(alpha, Expr.Prop)));
            if (!expected.Equals(eqInfo.Type))
            {
                throw new KernelException("failed to initialize quot module, 'Eq' has an unexpected type");
            }
        }
        {
            ConstantInfo refl = env.Get(eqVal.Ctors[0]);
            if (refl.LevelParams.Length != 1)
            {
                throw new KernelException("failed to initialize quot module, unexpected type for 'Eq' type constructor");
            }
            Level u = Level.Param(refl.LevelParams[0]);
            Expr alpha = lctx.MkLocalDecl(Name.Of("α"), Expr.Sort(u), BinderInfo.Implicit);
            Expr a = lctx.MkLocalDecl(Name.Of("a"), alpha);
            Expr expected = lctx.MkPi([alpha, a], Expr.MkApp(Expr.Const(EqName, [u]), alpha, a, a));
            if (!expected.Equals(refl.Type))
            {
                throw new KernelException("failed to initialize quot module, unexpected type for 'Eq' type constructor");
            }
        }
    }

    /// <summary>Add <c>Quot</c>, <c>Quot.mk</c>, <c>Quot.lift</c>, and <c>Quot.ind</c> with their fixed types.</summary>
    public static void AddQuot(Environment env)
    {
        if (env.QuotInitialized)
        {
            return;
        }
        CheckEqType(env);
        env.CheckName(QuotName);
        env.CheckName(QuotMk);
        env.CheckName(QuotLift);
        env.CheckName(QuotInd);

        var uName = Name.Of("u");
        var vName = Name.Of("v");
        Level u = Level.Param(uName);
        Level v = Level.Param(vName);
        Expr sortU = Expr.Sort(u);
        Expr sortV = Expr.Sort(v);

        var lctx = new LocalContext();
        Expr alpha = lctx.MkLocalDecl(Name.Of("α"), sortU, BinderInfo.Implicit);
        Expr r = lctx.MkLocalDecl(Name.Of("r"), Expr.Arrow(alpha, Expr.Arrow(alpha, Expr.Prop)));
        // Quot.{u} {α : Sort u} (r : α → α → Prop) : Sort u
        env.AddCore(new QuotInfo(QuotName, [uName], lctx.MkPi([alpha, r], sortU), QuotKind.Type));
        Expr quotR = Expr.MkApp(Expr.Const(QuotName, [u]), alpha, r);
        Expr a = lctx.MkLocalDecl(Name.Of("a"), alpha);
        // Quot.mk.{u} {α : Sort u} (r : α → α → Prop) (a : α) : Quot r
        env.AddCore(new QuotInfo(QuotMk, [uName], lctx.MkPi([alpha, r, a], quotR), QuotKind.Ctor));

        // r becomes implicit for lift and ind
        lctx = new LocalContext();
        alpha = lctx.MkLocalDecl(Name.Of("α"), sortU, BinderInfo.Implicit);
        r = lctx.MkLocalDecl(Name.Of("r"), Expr.Arrow(alpha, Expr.Arrow(alpha, Expr.Prop)), BinderInfo.Implicit);
        quotR = Expr.MkApp(Expr.Const(QuotName, [u]), alpha, r);
        a = lctx.MkLocalDecl(Name.Of("a"), alpha);
        Expr beta = lctx.MkLocalDecl(Name.Of("β"), sortV, BinderInfo.Implicit);
        Expr f = lctx.MkLocalDecl(Name.Of("f"), Expr.Arrow(alpha, beta));
        Expr b = lctx.MkLocalDecl(Name.Of("b"), alpha);
        Expr rab = Expr.MkApp(r, a, b);
        Expr faEqFb = Expr.MkApp(Expr.Const(EqName, [v]), beta, Expr.App(f, a), Expr.App(f, b));
        Expr sanity = lctx.MkPi([a, b], Expr.Arrow(rab, faEqFb));
        // Quot.lift.{u v} {α : Sort u} {r : α → α → Prop} {β : Sort v} (f : α → β) : (∀ a b, r a b → f a = f b) → Quot r → β
        env.AddCore(new QuotInfo(QuotLift, [uName, vName], lctx.MkPi([alpha, r, beta, f], Expr.Arrow(sanity, Expr.Arrow(quotR, beta))), QuotKind.Lift));

        // {β : Quot r → Prop}
        beta = lctx.MkLocalDecl(Name.Of("β"), Expr.Arrow(quotR, Expr.Prop), BinderInfo.Implicit);
        Expr quotMkA = Expr.MkApp(Expr.Const(QuotMk, [u]), alpha, r, a);
        Expr allQuot = lctx.MkPi(a, Expr.App(beta, quotMkA));
        Expr q = lctx.MkLocalDecl(Name.Of("q"), quotR);
        Expr betaQ = Expr.App(beta, q);
        // Quot.ind.{u} {α : Sort u} {r : α → α → Prop} {β : Quot r → Prop} : (∀ a, β (Quot.mk r a)) → ∀ q, β q
        env.AddCore(new QuotInfo(QuotInd, [uName], lctx.MkPi([alpha, r, beta], Expr.Pi(Name.Of("mk"), allQuot, lctx.MkPi(q, betaQ))), QuotKind.Ind));
        env.MarkQuotInitialized();
    }

    /// <summary>Reduce <c>Quot.lift f h (Quot.mk r a)</c> to <c>f a</c> and <c>Quot.ind h (Quot.mk r a)</c> to <c>h a</c>.</summary>
    public static Expr? TryReduceRec(Expr e, Func<Expr, Expr> whnf)
    {
        if (e.GetAppFn() is not ConstExpr fn)
        {
            return null;
        }
        int mkPos, argPos;
        if (fn.Name.Equals(QuotLift))
        {
            mkPos = 5;
            argPos = 3;
        }
        else if (fn.Name.Equals(QuotInd))
        {
            mkPos = 4;
            argPos = 3;
        }
        else
        {
            return null;
        }
        e.GetAppArgs(out Expr[] args);
        if (args.Length <= mkPos)
        {
            return null;
        }
        Expr mk = whnf(args[mkPos]);
        if (!(mk.GetAppFn() is ConstExpr mkFn && mkFn.Name.Equals(QuotMk) && mk.GetAppNumArgs() == 3))
        {
            return null;
        }
        Expr r = Expr.App(args[argPos], ((AppExpr)mk).Arg);
        int elimArity = mkPos + 1;
        if (args.Length > elimArity)
        {
            r = Expr.MkApp(r, args.AsSpan(elimArity));
        }
        return r;
    }
}
