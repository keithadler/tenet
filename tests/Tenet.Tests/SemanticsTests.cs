using Tenet.Export;
using Tenet.Kernel;
using Xunit;
using Environment = Tenet.Kernel.Environment;

namespace Tenet.Tests;

/// <summary>The definitional-equality rules that make or break soundness, exercised on the fixture environment.</summary>
public class SemanticsTests
{
    private static Environment LoadFixture()
    {
        ExportFile file = NdjsonReader.ReadFile(Path.Combine(AppContext.BaseDirectory, "fixtures", "Nat.add_succ.ndjson"));
        return ExportChecker.Check(file).Environment;
    }

    private static readonly Name Nat = Name.Of("Nat");
    private static readonly Name Eq = Name.Of("Eq");

    private static Expr NatE => Expr.Const(Nat, []);
    private static Expr EqNat(Expr a, Expr b) => Expr.MkApp(Expr.Const(Eq, [Level.One]), NatE, a, b);
    private static Expr Succ(Expr n) => Expr.App(Expr.Const(Name.Of("Nat", "succ"), []), n);
    private static Expr Add(Expr a, Expr b) => Expr.MkApp(Expr.Const(Name.Of("Nat", "add"), []), a, b);

    [Fact]
    public void ProofIrrelevance()
    {
        Environment env = LoadFixture();
        var tc = new TypeChecker(env);
        Expr prop = EqNat(Expr.NatLit(0), Expr.NatLit(0));
        Expr h1 = tc.Lctx.MkLocalDecl(Name.Of("h1"), prop);
        Expr h2 = tc.Lctx.MkLocalDecl(Name.Of("h2"), prop);
        Assert.True(tc.IsDefEq(h1, h2));
        // but not for data
        Expr n1 = tc.Lctx.MkLocalDecl(Name.Of("n1"), NatE);
        Expr n2 = tc.Lctx.MkLocalDecl(Name.Of("n2"), NatE);
        Assert.False(tc.IsDefEq(n1, n2));
    }

    [Fact]
    public void NatLiteralArithmeticIsDefinitional()
    {
        Environment env = LoadFixture();
        var tc = new TypeChecker(env);
        Assert.Equal(Expr.NatLit(5), tc.Whnf(Add(Expr.NatLit(2), Expr.NatLit(3))));
        Assert.True(tc.IsDefEq(Succ(Expr.NatLit(4)), Expr.NatLit(5)));
        Assert.True(tc.IsDefEq(Expr.NatLit(0), Expr.Const(Name.Of("Nat", "zero"), [])));
        Assert.False(tc.IsDefEq(Expr.NatLit(4), Expr.NatLit(5)));
        // a literal reduces to a constructor application when a recursor demands it: Nat.rec on 2
        Expr motive = Expr.Lam(Name.Of("_"), NatE, NatE);
        Expr zeroCase = Expr.NatLit(100);
        Expr succCase = Expr.Lam(Name.Of("n"), NatE, Expr.Lam(Name.Of("ih"), NatE, Succ(Expr.BVar(0))));
        Expr recApp = Expr.MkApp(Expr.Const(Name.Of("Nat", "rec"), [Level.One]), motive, zeroCase, succCase, Expr.NatLit(2));
        Assert.True(tc.IsDefEq(recApp, Expr.NatLit(102)));
    }

    [Fact]
    public void KLikeReductionOnEq()
    {
        Environment env = LoadFixture();
        var rec = (RecursorInfo)env.Get(Name.Of("Eq", "rec"));
        Assert.True(rec.K);
        var tc = new TypeChecker(env);
        Expr n = tc.Lctx.MkLocalDecl(Name.Of("n"), NatE);
        // h : n = n is a free variable, not a constructor, yet Eq.rec must reduce because Eq is a subsingleton in Prop.
        Expr h = tc.Lctx.MkLocalDecl(Name.Of("h"), EqNat(n, n));
        // @Eq.rec.{1, 1} Nat n (fun _ _ => Nat) 7 n h : Nat
        Expr motive = Expr.Lam(Name.Of("b"), NatE, Expr.Lam(Name.Of("_"), EqNat(n, Expr.BVar(0)), NatE));
        Expr recApp = Expr.MkApp(Expr.Const(Name.Of("Eq", "rec"), [Level.One, Level.One]), NatE, n, motive, Expr.NatLit(7), n, h);
        Assert.Equal(NatE, tc.Whnf(tc.Infer(recApp)));
        Assert.Equal(Expr.NatLit(7), tc.Whnf(recApp));
        // With an fvar m not known equal to n, h : n = m does not reduce.
        Expr m = tc.Lctx.MkLocalDecl(Name.Of("m"), NatE);
        Expr h2 = tc.Lctx.MkLocalDecl(Name.Of("h2"), EqNat(n, m));
        Expr stuck = Expr.MkApp(Expr.Const(Name.Of("Eq", "rec"), [Level.One, Level.One]), NatE, n, motive, Expr.NatLit(7), m, h2);
        Assert.NotEqual(Expr.NatLit(7), tc.Whnf(stuck));
    }

    [Fact]
    public void StructureEta()
    {
        Environment env = LoadFixture();
        // HAdd Nat Nat Nat is a one-field structure: inst =?= HAdd.mk (inst.hAdd)
        var tc = new TypeChecker(env);
        Expr hadd = Expr.MkApp(Expr.Const(Name.Of("HAdd"), [Level.Zero, Level.Zero, Level.Zero]), NatE, NatE, NatE);
        Expr inst = tc.Lctx.MkLocalDecl(Name.Of("inst"), hadd);
        Expr field = Expr.MkApp(Expr.Const(Name.Of("HAdd", "hAdd"), [Level.Zero, Level.Zero, Level.Zero]), NatE, NatE, NatE, inst);
        Expr rebuilt = Expr.MkApp(Expr.Const(Name.Of("HAdd", "mk"), [Level.Zero, Level.Zero, Level.Zero]), NatE, NatE, NatE, field);
        Assert.True(tc.IsDefEq(inst, rebuilt));
        // and projections of a constructor reduce
        Expr proj = Expr.Proj(Name.Of("HAdd"), 0, rebuilt);
        Assert.True(tc.IsDefEq(proj, field));
    }

    [Fact]
    public void EtaAndUnfoldingInteract()
    {
        Environment env = LoadFixture();
        var tc = new TypeChecker(env);
        // fun (a b : Nat) => Nat.add a b  =?=  Nat.add
        Expr lam = Expr.Lam(Name.Of("a"), NatE, Expr.Lam(Name.Of("b"), NatE, Add(Expr.BVar(1), Expr.BVar(0))));
        Assert.True(tc.IsDefEq(lam, Expr.Const(Name.Of("Nat", "add"), [])));
        // n + 0 reduces to n by unfolding Nat.add (structural recursion on the second argument)
        Expr n = tc.Lctx.MkLocalDecl(Name.Of("n"), NatE);
        Assert.True(tc.IsDefEq(Add(n, Expr.NatLit(0)), n));
        // 0 + n does not reduce to n definitionally (that needs induction)
        Assert.False(tc.IsDefEq(Add(Expr.NatLit(0), n), n));
    }

    [Fact]
    public void UniverseLevelsMustMatch()
    {
        Environment env = LoadFixture();
        var tc = new TypeChecker(env);
        // Eq.{1} and Eq.{2} applied to the same arguments are not definitionally equal
        Expr e1 = Expr.MkApp(Expr.Const(Eq, [Level.One]), Expr.Type0, NatE, NatE);
        Expr e2 = Expr.MkApp(Expr.Const(Eq, [Level.Succ(Level.One)]), Expr.Type0, NatE, NatE);
        Assert.False(tc.IsDefEq(e1, e2));
        // and a constant applied to the wrong number of universe levels is rejected
        Assert.Throws<KernelException>(() => tc.Check(Expr.Const(Eq, []), []));
    }
}
