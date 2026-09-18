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

    /// <summary>
    /// A term deeper than the stack is rejected rather than taken as a reason to abort the process. Reduction can
    /// grow a term without bound on ill-typed input, and a .NET stack overflow cannot be caught: it would kill
    /// every other declaration being checked and leave no report. This runs on a deliberately small stack so the
    /// limit is reached in a fraction of a second; on a real run the stack is 512 MB.
    /// </summary>
    [Fact]
    public void ATermDeeperThanTheStackIsRejectedRatherThanCrashing()
    {
        // f (f (f ... (f Nat))), deep enough that no plausible stack holds it.
        Expr deep = NatE;
        for (int i = 0; i < 2_000_000; i++)
        {
            deep = Expr.App(NatE, deep);
        }

        Exception? caught = null;
        var t = new Thread(() =>
        {
            try
            {
                ExprOps.ForEach(deep, (_, _) => true);
                caught = new InvalidOperationException("traversal returned without hitting the limit");
            }
            catch (Exception e)
            {
                caught = e;
            }
        }, 1 * 1024 * 1024);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromMinutes(2)), "the traversal neither finished nor hit the limit");
        Assert.IsType<RecursionLimitException>(caught);
        Assert.Contains("too deep", caught!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// FirstDifference answers what one decoder produced against what another did, so it must not call two terms
    /// equal when the kernel's own equality calls them different. It used to do exactly that for a let's nonDep
    /// flag, and to answer "(equal)" for binder-only differences before the comparison naming them could run,
    /// which left crosscheck unable to report either class of reader defect.
    /// </summary>
    [Fact]
    public void FirstDifferenceSeesWhatKernelEqualityIgnoresAndWhatItDoesNot()
    {
        Expr letDep = Expr.Let(Name.Of("x"), NatE, Expr.NatLit(1), Expr.BVar(0), nonDep: false);
        Expr letNon = Expr.Let(Name.Of("x"), NatE, Expr.NatLit(1), Expr.BVar(0), nonDep: true);
        Assert.False(letDep.Equals(letNon));   // the kernel's equality distinguishes the flag
        Assert.Contains("nonDep", ExprOps.FirstDifference(letDep, letNon), StringComparison.Ordinal);

        Expr piA = Expr.Pi(Name.Of("a"), NatE, NatE);
        Expr piB = Expr.Pi(Name.Of("b"), NatE, NatE);
        Assert.True(piA.Equals(piB));          // the kernel ignores binder names, and must keep ignoring them
        Assert.False(Expr.EqStrict(piA, piB));
        Assert.Contains("binder", ExprOps.FirstDifference(piA, piB), StringComparison.Ordinal);

        Expr impl = Expr.Pi(Name.Of("a"), NatE, NatE, BinderInfo.Implicit);
        Assert.True(piA.Equals(impl));
        Assert.Contains("binder", ExprOps.FirstDifference(piA, impl), StringComparison.Ordinal);

        // And nothing is invented where nothing differs.
        Assert.Equal("(equal)", ExprOps.FirstDifference(piA, Expr.Pi(Name.Of("a"), NatE, NatE)));
    }

    /// <summary>Build f (f (f ... x)) nested in the argument position, which is where each level costs a frame.</summary>
    private static Expr DeepTerm(int depth)
    {
        Expr e = NatE;
        for (int i = 0; i < depth; i++)
        {
            e = Expr.App(NatE, e);
        }
        return e;
    }

    /// <summary>Run <paramref name="body"/> on a deliberately small stack and return what it threw, if anything.</summary>
    private static Exception? OnASmallStack(Action body)
    {
        Exception? caught = null;
        var t = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                caught = e;
            }
        }, 1 * 1024 * 1024);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromMinutes(2)), "the work neither finished nor hit the limit");
        return caught;
    }

    /// <summary>
    /// Structural equality walks two terms at once, and only the child off the spine costs a frame. Two deep terms
    /// that are equal recurse all the way down, so this is a path a deep term reaches without ever being reduced.
    /// </summary>
    [Fact]
    public void ComparingTwoTermsDeeperThanTheStackIsRejected()
    {
        // Built twice, so nothing is reference-equal and the comparison really descends.
        Expr a = DeepTerm(2_000_000), b = DeepTerm(2_000_000);
        Exception? e = OnASmallStack(() => a.Equals(b));
        Assert.IsType<RecursionLimitException>(e);
    }

    /// <summary>
    /// The printer truncates rather than throwing. It is what writes the message for some other error, and
    /// replacing a type mismatch with "expression too deep" would throw away the error being reported.
    /// </summary>
    [Fact]
    public void PrintingATermDeeperThanTheStackTruncates()
    {
        Expr deep = DeepTerm(2_000_000);
        string? printed = null;
        Exception? e = OnASmallStack(() => printed = ExprPrinter.Print(deep));
        Assert.Null(e);
        Assert.Contains("\u2026", printed!, StringComparison.Ordinal);
    }

    /// <summary>A universe level nested deeper than the stack is rejected the same way a term is.</summary>
    [Fact]
    public void NormalizingALevelDeeperThanTheStackIsRejected()
    {
        // Distinct parameters, because MkMax folds max 0 u, max u u and max u (max u v) on the way in; if any of
        // those fired the level would stay shallow and this test would pass without testing anything.
        const int Levels = 200_000;
        Level deep = Level.Param(Name.Of("u0"));
        for (int i = 1; i < Levels; i++)
        {
            deep = Level.MkMax(Level.Param(Name.Of("u" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))), deep);
        }
        Assert.Equal(Levels - 1, deep.Depth);
        Exception? e = OnASmallStack(() => deep.Normalize());
        Assert.IsType<RecursionLimitException>(e);
    }

    [Fact]
    public void NonTerminatingUnsafeDefinitionHitsTheUnfoldLimit()
    {
        Environment env = LoadFixture();
        // unsafe def loop : Nat := loop
        env.Add(new DefinitionDecl(Name.Of("loop"), [], NatE, Expr.Const(Name.Of("loop"), []), ReducibilityHints.Regular(1), DefinitionSafety.Unsafe));
        long saved = TypeChecker.MaxUnfolds;
        TypeChecker.MaxUnfolds = 10_000;
        try
        {
            var tc = new TypeChecker(env, safety: DefinitionSafety.Unsafe);
            var ex = Assert.Throws<DeterministicTimeoutException>(() => tc.Whnf(Expr.Const(Name.Of("loop"), [])));
            Assert.Contains("deterministic timeout", ex.Message, StringComparison.Ordinal);
            var tc2 = new TypeChecker(env, safety: DefinitionSafety.Unsafe);
            Assert.Throws<DeterministicTimeoutException>(() => tc2.IsDefEq(Expr.Const(Name.Of("loop"), []), Expr.NatLit(3)));
            // A declaration that hits the limit is rejected with this exception type, which Environment.Add
            // excludes from the faithful re-check (re-running without the failure cache would only repeat the work).
            // unsafe def : Eq Nat loop 3 := Eq.refl Nat 3   forces loop =?= 3, which unfolds forever
            Expr eq = Expr.MkApp(Expr.Const(Name.Of("Eq"), [Level.One]), NatE, Expr.Const(Name.Of("loop"), []), Expr.NatLit(3));
            Expr refl = Expr.MkApp(Expr.Const(Name.Of("Eq", "refl"), [Level.One]), NatE, Expr.NatLit(3));
            Assert.Throws<DeterministicTimeoutException>(() => env.Add(new DefinitionDecl(Name.Of("loop2"), [], eq, refl, ReducibilityHints.Regular(1), DefinitionSafety.Unsafe)));
            Assert.Null(env.Find(Name.Of("loop2")));
        }
        finally
        {
            TypeChecker.MaxUnfolds = saved;
        }
    }

    [Fact]
    public void NegativeIndicesAreKernelErrors()
    {
        Assert.Throws<KernelException>(() => Expr.BVar(-1));
        Assert.Throws<KernelException>(() => Expr.Proj(Name.Of("Prod"), -1, Expr.Prop));
    }
}
