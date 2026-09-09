using System.Numerics;
using Tenet.Kernel;
using Xunit;
using Environment = Tenet.Kernel.Environment;

namespace Tenet.Tests;

/// <summary>
/// The classic ways to derive False in a dependent type theory, each stated as a declaration the kernel
/// must reject (or a reduction it must refuse). A checker that accepts any of these is not a checker.
/// </summary>
public class AttackTests
{
    private static readonly Name Bad = Name.Of("Bad");
    private static Expr BadT => Expr.Const(Bad, []);
    private static readonly Name Anon = Name.Of("x");

    private static InductiveDecl Single(Expr type, params (string ctor, Expr ctorType)[] ctors) =>
        new([], 0, [new InductiveType(Bad, type, ctors.Select(c => new Constructor(Name.Of("Bad", c.ctor), c.ctorType)).ToArray())], false);

    [Fact]
    public void NonPositiveOccurrenceIsRejected()
    {
        // inductive Bad | mk : (Bad → Bad) → Bad        (Bad in a negative position: Curry's paradox)
        var decl = Single(Expr.Type0, ("mk", Expr.Pi(Anon, Expr.Pi(Anon, BadT, BadT), BadT)));
        var ex = Assert.Throws<KernelException>(() => new Environment().Add(decl));
        Assert.Contains("non positive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorArgumentInATooLargeUniverseIsRejected()
    {
        // inductive Bad : Type | mk : Type → Bad        (Type : Type 1 does not fit in Type: Girard's paradox)
        var decl = Single(Expr.Type0, ("mk", Expr.Pi(Anon, Expr.Type0, BadT)));
        var ex = Assert.Throws<KernelException>(() => new Environment().Add(decl));
        Assert.Contains("too big", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorMustReturnItsOwnType()
    {
        // inductive Bad : Type | mk : Type
        var decl = Single(Expr.Type0, ("mk", Expr.Type0));
        var ex = Assert.Throws<KernelException>(() => new Environment().Add(decl));
        Assert.Contains("return type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IllTypedIndexIsRejected()
    {
        // inductive Bad : Type → Type | mk : Bad Type      (the index Type : Type 1, but Bad expects a Type)
        var decl = Single(Expr.Pi(Anon, Expr.Type0, Expr.Type0), ("mk", Expr.App(BadT, Expr.Type0)));
        var ex = Assert.Throws<KernelException>(() => new Environment().Add(decl));
        Assert.Contains("application type mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PropWithSeveralConstructorsOnlyEliminatesIntoProp()
    {
        // inductive Or' (a b : Prop) : Prop | inl : a → Or' a b | inr : b → Or' a b
        // Its recursor must have no universe parameter: eliminating Or' into Type would decide propositions.
        Name or = Name.Of("Or'");
        Expr orT = Expr.Const(or, []);
        Expr type = Expr.Pi(Name.Of("a"), Expr.Prop, Expr.Pi(Name.Of("b"), Expr.Prop, Expr.Prop));
        // ∀ (a b : Prop), a → Or' a b   with de Bruijn indices: a = #1, b = #0 inside, then the argument shifts them
        Expr inl = Expr.Pi(Name.Of("a"), Expr.Prop, Expr.Pi(Name.Of("b"), Expr.Prop, Expr.Pi(Anon, Expr.BVar(1), Expr.MkApp(orT, Expr.BVar(2), Expr.BVar(1)))));
        Expr inr = Expr.Pi(Name.Of("a"), Expr.Prop, Expr.Pi(Name.Of("b"), Expr.Prop, Expr.Pi(Anon, Expr.BVar(0), Expr.MkApp(orT, Expr.BVar(2), Expr.BVar(1)))));
        var env = new Environment();
        env.Add(new InductiveDecl([], 2, [new InductiveType(or, type, [new Constructor(Name.Of("Or'", "inl"), inl), new Constructor(Name.Of("Or'", "inr"), inr)])], false));
        var rec = Assert.IsType<RecursorInfo>(env.Get(Name.Of("Or'", "rec")));
        Assert.Empty(rec.LevelParams);

        // whereas a Prop with one constructor and no data (True) eliminates into any universe
        Name tr = Name.Of("True'");
        var env2 = new Environment();
        env2.Add(new InductiveDecl([], 0, [new InductiveType(tr, Expr.Prop, [new Constructor(Name.Of("True'", "intro"), Expr.Const(tr, []))])], false));
        var rec2 = Assert.IsType<RecursorInfo>(env2.Get(Name.Of("True'", "rec")));
        Assert.Single(rec2.LevelParams);
    }

    [Fact]
    public void ProofsOfDifferentPropositionsAreNotEqual()
    {
        var env = new Environment();
        env.Add(new AxiomDecl(Name.Of("p"), [], Expr.Prop, false));
        env.Add(new AxiomDecl(Name.Of("q"), [], Expr.Prop, false));
        var tc = new TypeChecker(env);
        Expr hp = tc.Lctx.MkLocalDecl(Name.Of("hp"), Expr.Const(Name.Of("p"), []));
        Expr hp2 = tc.Lctx.MkLocalDecl(Name.Of("hp2"), Expr.Const(Name.Of("p"), []));
        Expr hq = tc.Lctx.MkLocalDecl(Name.Of("hq"), Expr.Const(Name.Of("q"), []));
        Assert.True(tc.IsDefEq(hp, hp2));
        Assert.False(tc.IsDefEq(hp, hq));
    }

    [Fact]
    public void HugeLiteralArithmeticIsRefusedNotComputed()
    {
        var env = new Environment();
        Name nat = Name.Of("Nat");
        env.Add(new AxiomDecl(nat, [], Expr.Type0, false));
        Expr natT = Expr.Const(nat, []);
        env.Add(new AxiomDecl(Name.Of("Nat", "pow"), [], Expr.Pi(Anon, natT, Expr.Pi(Anon, natT, natT)), false));
        var tc = new TypeChecker(env);
        Expr e = Expr.MkApp(Expr.Const(Name.Of("Nat", "pow"), []), Expr.NatLit(2), Expr.NatLit(BigInteger.Pow(2, 40)));
        var ex = Assert.Throws<KernelException>(() => tc.Whnf(e));
        Assert.Contains("refused", ex.Message, StringComparison.Ordinal);
        // and a merely large one computes
        Expr ok = Expr.MkApp(Expr.Const(Name.Of("Nat", "pow"), []), Expr.NatLit(2), Expr.NatLit(100));
        Assert.Equal(Expr.NatLit(BigInteger.Pow(2, 100)), tc.Whnf(ok));
    }

    [Fact]
    public void UniverseCannotContainItself()
    {
        // def bad.{u} : Sort u := Sort u
        Name u = Name.Of("u");
        var decl = new DefinitionDecl(Name.Of("bad"), [u], Expr.Sort(Level.Param(u)), Expr.Sort(Level.Param(u)), ReducibilityHints.Regular(1), DefinitionSafety.Safe);
        var ex = Assert.Throws<KernelException>(() => new Environment().Add(decl));
        Assert.Contains("type mismatch", ex.Message, StringComparison.Ordinal);
    }

    // A declaration installed unchecked can name a constant that does not exist. Lean looks such a name up with
    // `get` in `is_non_rec_structure`, `get_first_cnstr`, `reduce_proj_core` and `try_eta_struct_core`, so the
    // check fails with "unknown constant"; a `find` that answered "not a structure" would let the check go on and
    // succeed by another route. Differential seed 59, variant 013: with `Eq.refl`'s type renamed to a bare `refl`,
    // Lean rejected twenty `noConfusion` declarations that Tenet accepted.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReducingARecursorWhoseMajorTypeIsUnknownIsARejection(bool kLike)
    {
        var env = new Environment();
        Name bogus = Name.Of("Bogus");
        Name u = Name.Of("u");
        Expr bogusT = Expr.Const(bogus, []);
        // T.rec : ∀ (motive : Bogus → Sort u) (t : Bogus), motive t   (no parameters or minors: the major premise is argument 1)
        Expr recType = Expr.Pi(Name.Of("motive"), Expr.Pi(Anon, bogusT, Expr.Sort(Level.Param(u))),
                               Expr.Pi(Name.Of("t"), bogusT, Expr.App(Expr.BVar(1), Expr.BVar(0))));
        env.AddCore(new RecursorInfo(Name.Of("T", "rec"), [u], recType, [Name.Of("T")], numParams: 0, numIndices: 0, numMotives: 1, numMinors: 0,
                                     rules: [], k: kLike, isUnsafe: false));
        var tc = new TypeChecker(env);
        Expr motive = tc.Lctx.MkLocalDecl(Name.Of("motive"), Expr.Pi(Anon, bogusT, Expr.Sort(Level.One)));
        Expr t = tc.Lctx.MkLocalDecl(Name.Of("t"), bogusT);
        Expr e = Expr.MkApp(Expr.Const(Name.Of("T", "rec"), [Level.One]), motive, t);
        var ex = Assert.Throws<KernelException>(() => tc.Whnf(e));
        Assert.Contains("unknown constant 'Bogus'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectingFromATermHeadedByAnUnknownConstantIsARejection()
    {
        var tc = new TypeChecker(new Environment());
        Expr e = Expr.Proj(Name.Of("S"), 0, Expr.App(Expr.Const(Name.Of("Bogus"), []), Expr.NatLit(0)));
        var ex = Assert.Throws<KernelException>(() => tc.Whnf(e));
        Assert.Contains("unknown constant 'Bogus'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparingWithATermHeadedByAnUnknownConstantIsARejection()
    {
        var env = new Environment();
        env.Add(new AxiomDecl(Name.Of("S"), [], Expr.Type0, false));
        var tc = new TypeChecker(env);
        Expr x = tc.Lctx.MkLocalDecl(Name.Of("x"), Expr.Const(Name.Of("S"), []));
        Expr y = Expr.App(Expr.Const(Name.Of("Bogus"), []), x);
        var ex = Assert.Throws<KernelException>(() => tc.IsDefEq(x, y));
        Assert.Contains("unknown constant 'Bogus'", ex.Message, StringComparison.Ordinal);
    }
}
