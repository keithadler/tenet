using Tenet.Export;
using Tenet.Kernel;
using Xunit;
using Environment = Tenet.Kernel.Environment;

namespace Tenet.Tests;

/// <summary>Declaration kinds and safety rules that real exports rarely contain.</summary>
public class SafetyTests
{
    private static Environment LoadFixture()
    {
        ExportFile file = NdjsonReader.ReadFile(Path.Combine(AppContext.BaseDirectory, "fixtures", "Nat.add_succ.ndjson"));
        return ExportChecker.Check(file).Environment;
    }

    private static readonly Expr NatE = Expr.Const(Name.Of("Nat"), []);
    private static Expr NatToNat => Expr.Arrow(NatE, NatE);

    [Fact]
    public void UnsafeMutualDefinitionsMayRecurse()
    {
        Environment env = LoadFixture();
        // unsafe def f : Nat → Nat := fun n => g n ; unsafe def g : Nat → Nat := fun n => f n
        var f = new DefinitionDecl(Name.Of("f"), [], NatToNat, Expr.Lam(Name.Of("n"), NatE, Expr.App(Expr.Const(Name.Of("g"), []), Expr.BVar(0))),
                                   ReducibilityHints.Opaque, DefinitionSafety.Unsafe, [Name.Of("f"), Name.Of("g")]);
        var g = new DefinitionDecl(Name.Of("g"), [], NatToNat, Expr.Lam(Name.Of("n"), NatE, Expr.App(Expr.Const(Name.Of("f"), []), Expr.BVar(0))),
                                   ReducibilityHints.Opaque, DefinitionSafety.Unsafe, [Name.Of("f"), Name.Of("g")]);
        env.Add(new MutualDefinitionDecl([f, g]));
        Assert.True(env.Get(Name.Of("f")).IsUnsafe);
        Assert.True(env.Get(Name.Of("g")).IsUnsafe);
    }

    [Fact]
    public void SafeMutualBlockIsRejected()
    {
        Environment env = LoadFixture();
        var f = new DefinitionDecl(Name.Of("f"), [], NatToNat, Expr.Lam(Name.Of("n"), NatE, Expr.BVar(0)), ReducibilityHints.Regular(1), DefinitionSafety.Safe);
        var ex = Assert.Throws<KernelException>(() => env.Add(new MutualDefinitionDecl([f])));
        Assert.Contains("unsafe/partial", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SafeCodeMayNotUseUnsafeCode()
    {
        Environment env = LoadFixture();
        env.Add(new AxiomDecl(Name.Of("bad"), [], NatE, isUnsafe: true));
        var safe = new DefinitionDecl(Name.Of("uses"), [], NatE, Expr.Const(Name.Of("bad"), []), ReducibilityHints.Regular(1), DefinitionSafety.Safe);
        var ex = Assert.Throws<KernelException>(() => env.Add(safe));
        Assert.Contains("unsafe", ex.Message, StringComparison.Ordinal);
        // an unsafe definition may
        var unsafeDef = new DefinitionDecl(Name.Of("uses"), [], NatE, Expr.Const(Name.Of("bad"), []), ReducibilityHints.Regular(1), DefinitionSafety.Unsafe);
        env.Add(unsafeDef);
        Assert.NotNull(env.Find(Name.Of("uses")));
    }

    [Fact]
    public void PartialDefinitionsMayNotDependOnEachOther()
    {
        Environment env = LoadFixture();
        var p1 = new DefinitionDecl(Name.Of("p1"), [], NatE, Expr.NatLit(0), ReducibilityHints.Opaque, DefinitionSafety.Partial);
        env.Add(p1);
        var p2 = new DefinitionDecl(Name.Of("p2"), [], NatE, Expr.Const(Name.Of("p1"), []), ReducibilityHints.Opaque, DefinitionSafety.Partial);
        var ex = Assert.Throws<KernelException>(() => env.Add(p2));
        Assert.Contains("partial", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpaqueValuesAreCheckedButNeverUnfolded()
    {
        Environment env = LoadFixture();
        env.Add(new OpaqueDecl(Name.Of("secret"), [], NatE, Expr.NatLit(42), isUnsafe: false));
        var tc = new TypeChecker(env);
        Assert.False(tc.IsDefEq(Expr.Const(Name.Of("secret"), []), Expr.NatLit(42)));
        Assert.Equal(Expr.Const(Name.Of("secret"), []), tc.Whnf(Expr.Const(Name.Of("secret"), [])));
        // a wrong opaque value is still rejected
        Assert.Throws<KernelException>(() => env.Add(new OpaqueDecl(Name.Of("wrong"), [], NatE, Expr.Prop, isUnsafe: false)));
    }

    [Fact]
    public void NativeReductionIsRefused()
    {
        Environment env = LoadFixture();
        env.Add(new AxiomDecl(Name.Of("Lean", "reduceBool"), [], Expr.Arrow(NatE, NatE), isUnsafe: false));
        env.Add(new DefinitionDecl(Name.Of("c"), [], NatE, Expr.NatLit(1), ReducibilityHints.Regular(1), DefinitionSafety.Safe));
        var tc = new TypeChecker(env);
        Expr app = Expr.App(Expr.Const(Name.Of("Lean", "reduceBool"), []), Expr.Const(Name.Of("c"), []));
        var ex = Assert.Throws<UnsupportedException>(() => tc.Whnf(app));
        Assert.Contains("compiled code", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyFilterChecksJustTheNamedDeclarations()
    {
        ExportFile file = NdjsonReader.ReadFile(Path.Combine(AppContext.BaseDirectory, "fixtures", "Nat.add_succ.ndjson"));
        var options = new CheckOptions { Only = [Name.Of("Nat", "add_succ")] };
        CheckResult r = ExportChecker.Check(file, options);
        Assert.True(r.Success);
        Assert.Equal(1, r.Checked);
        Assert.Equal(file.Decls.Count - 1, r.Skipped);
        CheckResult rp = ExportChecker.Check(file, new CheckOptions { Only = [Name.Of("Nat", "add_succ")], Jobs = 4 });
        Assert.Equal(1, rp.Checked);
    }
}
