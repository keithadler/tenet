using Tenet.Kernel;
using Environment = Tenet.Kernel.Environment;
using Xunit;

namespace Tenet.Tests;

public class TypeCheckerTests
{
    private static readonly Name U = Name.Of("u");

    [Fact]
    public void PolymorphicIdentityHasExpectedType()
    {
        var env = new Environment();
        // fun {α : Sort u} (a : α) => a
        Expr value = Expr.Lam(Name.Of("α"), Expr.Sort(Level.Param(U)), Expr.Lam(Name.Of("a"), Expr.BVar(0), Expr.BVar(0)), BinderInfo.Implicit);
        Expr type = Expr.Pi(Name.Of("α"), Expr.Sort(Level.Param(U)), Expr.Pi(Name.Of("a"), Expr.BVar(0), Expr.BVar(1)), BinderInfo.Implicit);
        env.Add(new DefinitionDecl(Name.Of("id"), [U], type, value, ReducibilityHints.Abbrev, DefinitionSafety.Safe));
        Assert.NotNull(env.Find(Name.Of("id")));
        var tc = new TypeChecker(env);
        Expr inferred = tc.Infer(Expr.Const(Name.Of("id"), [Level.One]));
        Assert.Equal(ExprOps.InstantiateLevelParams(type, [U], [Level.One]), inferred);
    }

    [Fact]
    public void PropIsImpredicative()
    {
        var tc = new TypeChecker(new Environment());
        // ∀ (p : Prop), p  :  Prop
        Expr e = Expr.Pi(Name.Of("p"), Expr.Prop, Expr.BVar(0));
        Assert.Equal(Expr.Prop, tc.Check(e, []));
        // ∀ (α : Type), α  :  Type 1
        Expr e2 = Expr.Pi(Name.Of("α"), Expr.Type0, Expr.BVar(0));
        Assert.Equal(Expr.Sort(Level.Succ(Level.One)), tc.Check(e2, []));
    }

    [Fact]
    public void TypeMismatchIsRejected()
    {
        var env = new Environment();
        // def bad : Prop := Type   (Type : Type 1, not Prop)
        var decl = new DefinitionDecl(Name.Of("bad"), [], Expr.Prop, Expr.Type0, ReducibilityHints.Regular(1), DefinitionSafety.Safe);
        var ex = Assert.Throws<KernelException>(() => env.Add(decl));
        Assert.Contains("type mismatch", ex.Message, StringComparison.Ordinal);
        Assert.Null(env.Find(Name.Of("bad")));
    }

    [Fact]
    public void UndefinedUniverseIsRejected()
    {
        var env = new Environment();
        var decl = new AxiomDecl(Name.Of("ax"), [], Expr.Sort(Level.Param(U)), false);
        var ex = Assert.Throws<KernelException>(() => env.Add(decl));
        Assert.Contains("universe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateNameIsRejected()
    {
        var env = new Environment();
        env.Add(new AxiomDecl(Name.Of("ax"), [], Expr.Prop, false));
        Assert.Throws<KernelException>(() => env.Add(new AxiomDecl(Name.Of("ax"), [], Expr.Prop, false)));
    }

    [Fact]
    public void TheoremMustBeAProp()
    {
        var env = new Environment();
        var decl = new TheoremDecl(Name.Of("t"), [], Expr.Type0, Expr.Prop);
        var ex = Assert.Throws<KernelException>(() => env.Add(decl));
        Assert.Contains("not a proposition", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BetaAndDeltaAreDefinitional()
    {
        var env = new Environment();
        env.Add(new DefinitionDecl(Name.Of("T"), [], Expr.Type0, Expr.Prop, ReducibilityHints.Regular(1), DefinitionSafety.Safe));
        var tc = new TypeChecker(env);
        Expr app = Expr.App(Expr.Lam(Name.Of("x"), Expr.Type0, Expr.BVar(0)), Expr.Const(Name.Of("T"), []));
        Assert.True(tc.IsDefEq(app, Expr.Prop));
        Assert.Equal(Expr.Prop, tc.Whnf(app));
        Assert.False(tc.IsDefEq(app, Expr.Type0));
    }

    [Fact]
    public void EtaForFunctions()
    {
        var env = new Environment();
        env.Add(new AxiomDecl(Name.Of("f"), [], Expr.Arrow(Expr.Prop, Expr.Prop), false));
        var tc = new TypeChecker(env);
        Expr f = Expr.Const(Name.Of("f"), []);
        Expr etaF = Expr.Lam(Name.Of("x"), Expr.Prop, Expr.App(f, Expr.BVar(0)));
        Assert.True(tc.IsDefEq(f, etaF));
    }

    [Fact]
    public void RejectedMutualBlockLeavesNothingBehind()
    {
        // unsafe def a : Type 1 := Type   (fine)
        // unsafe def b : Prop := Type     (type mismatch, found after both constants were added)
        var a = new DefinitionDecl(Name.Of("a"), [], Expr.Sort(Level.Succ(Level.One)), Expr.Type0, ReducibilityHints.Regular(1), DefinitionSafety.Unsafe);
        var b = new DefinitionDecl(Name.Of("b"), [], Expr.Prop, Expr.Type0, ReducibilityHints.Regular(1), DefinitionSafety.Unsafe);
        foreach (bool faithful in new[] { false, true })
        {
            using var scope = faithful ? new TypeChecker.FaithfulScope() : default;
            var env = new Environment();
            Assert.Throws<KernelException>(() => env.Add(new MutualDefinitionDecl([a, b])));
            Assert.Null(env.Find(Name.Of("a")));
            Assert.Null(env.Find(Name.Of("b")));
            Assert.Empty(env.OwnConstants);
            env.Add(new MutualDefinitionDecl([a]));
            Assert.NotNull(env.Find(Name.Of("a")));
        }
    }

    [Fact]
    public void FaithfulScopeDisablesFailureCaching()
    {
        Assert.False(TypeChecker.InFaithfulScope);
        using (new TypeChecker.FaithfulScope())
        {
            Assert.True(TypeChecker.InFaithfulScope);
        }
        Assert.False(TypeChecker.InFaithfulScope);
    }
}
