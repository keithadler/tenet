using Tenet.Kernel;
using Xunit;

namespace Tenet.Tests;

/// <summary>
/// What <c>tenet axioms</c>, <c>tenet why</c> and <c>tenet audit</c> all rest on: the walk from a declaration to
/// the assumptions underneath it.
///
/// Every way of being wrong here that matters is a way of reporting too few. A tool that over-reports is annoying;
/// one that under-reports says a proof resting on <c>sorry</c> is complete, which is the single claim these
/// commands exist to make and the one thing they must not get wrong. Both bugs below were live and neither was
/// caught by any existing test, because a clean answer and a missed edge look identical from outside.
///
/// The expectations are not invented. Each is what Lean 4.34.0 printed for the same declaration, recorded beside
/// the case; <c>tests/fixtures/axioms/Axioms.lean</c> is the source that produced them, and the integration
/// workflow re-runs the comparison against a real toolchain so these cannot drift from Lean without CI noticing.
/// </summary>
public class AxiomCollectionTests
{
    private static Expr C(string n) => Expr.Const(Name.Of(n), []);

    /// <summary>
    /// The bug: an inductive never mentions its constructors in its own type, so a walk that only reads types
    /// never reaches them. An axiom used in a field type was invisible behind the type that carried it.
    ///
    /// Lean, on `structure ViaCtor where x : Fin (pick + 1)`:
    ///   'ViaCtor' depends on axioms: [Classical.choice]
    /// Tenet, before this was fixed:
    ///   ViaCtor depends on 1 constants and these axioms: (none)
    /// </summary>
    [Fact]
    public void AnAxiomReachableOnlyThroughAConstructorIsStillReported()
    {
        var env = new Tenet.Kernel.Environment();
        env.Add(new AxiomDecl(Name.Of("Secret"), [], Expr.Prop, false));
        env.Add(new InductiveDecl([], 0, [new InductiveType(Name.Of("T"), Expr.Prop,
            [new Constructor(Name.Of("T", "mk"), Expr.Arrow(C("Secret"), C("T")))])], false));

        // The constructor was always right. The type it belongs to was not, and the type is what anyone asks about.
        Assert.Equal([Name.Of("Secret")], Replay.AxiomsOf(env.Find, Name.Of("T", "mk")).Axioms);
        Assert.Equal([Name.Of("Secret")], Replay.AxiomsOf(env.Find, Name.Of("T")).Axioms);
    }

    /// <summary>
    /// The second bug: reaching an axiom ended the walk. An axiom whose own statement is written in terms of
    /// another axiom reported only itself, so the assumption underneath it never appeared.
    ///
    /// Lean, on `axiom viaAxiomType : Fin (pick + 1)`:
    ///   'viaAxiomType' depends on axioms: [viaAxiomType, Classical.choice]
    /// Tenet, before this was fixed:
    ///   viaAxiomType depends on 1 constants and these axioms: viaAxiomType
    /// </summary>
    [Fact]
    public void AnAxiomStatedInTermsOfAnotherReportsBoth()
    {
        var env = new Tenet.Kernel.Environment();
        env.Add(new AxiomDecl(Name.Of("P"), [], Expr.Prop, false));
        env.Add(new AxiomDecl(Name.Of("Q"), [], C("P"), false));

        Assert.Equal([Name.Of("P"), Name.Of("Q")], Replay.AxiomsOf(env.Find, Name.Of("Q")).Axioms);
    }

    /// <summary>
    /// The two together, in the shape that costs something. A structure whose field type rests on <c>sorry</c>
    /// compiles, and before the fix `tenet axioms` called it clean. `audit` reports a project's holes by this same
    /// walk, so it would have counted the type as standing unconditionally.
    ///
    /// Lean, on `def holed : Nat := sorry` and `structure SorryViaCtor where y : Fin (holed + 1)`:
    ///   'SorryViaCtor' depends on axioms: [sorryAx]
    /// </summary>
    [Fact]
    public void ASorryBehindAConstructorIsNotInvisible()
    {
        var env = new Tenet.Kernel.Environment();
        env.Add(new AxiomDecl(Name.Of("sorryAx"), [], Expr.Prop, false));
        env.Add(new InductiveDecl([], 0, [new InductiveType(Name.Of("Holed"), Expr.Prop,
            [new Constructor(Name.Of("Holed", "mk"), Expr.Arrow(C("sorryAx"), C("Holed")))])], false));

        Assert.Contains(Name.Of("sorryAx"), Replay.AxiomsOf(env.Find, Name.Of("Holed")).Axioms);
    }

    /// <summary>
    /// And the other half, without which the three above prove nothing. A walk that reported every name it touched
    /// would pass all of them and be useless. An inductive that rests on nothing has to come back empty, and an
    /// axiom reached through a constructor must be the constructor's, not any axiom that happens to be in scope.
    /// </summary>
    [Fact]
    public void AnInductiveRestingOnNothingReportsNothing()
    {
        var env = new Tenet.Kernel.Environment();
        env.Add(new AxiomDecl(Name.Of("Unrelated"), [], Expr.Prop, false));
        env.Add(new InductiveDecl([], 0, [new InductiveType(Name.Of("Clean"), Expr.Prop,
            [new Constructor(Name.Of("Clean", "mk"), C("Clean"))])], false));

        Assert.Empty(Replay.AxiomsOf(env.Find, Name.Of("Clean")).Axioms);
        Assert.Empty(Replay.AxiomsOf(env.Find, Name.Of("Clean", "mk")).Axioms);
    }

    /// <summary>
    /// `why` explains what `axioms` reports, so it walks the same edges. Before the fix it could not produce a
    /// chain for a dependency that `axioms` named, which is the inconsistency a reader would hit first.
    /// </summary>
    [Fact]
    public void WhyCanExplainADependencyThatRunsThroughAConstructor()
    {
        var env = new Tenet.Kernel.Environment();
        env.Add(new AxiomDecl(Name.Of("Secret"), [], Expr.Prop, false));
        env.Add(new InductiveDecl([], 0, [new InductiveType(Name.Of("T"), Expr.Prop,
            [new Constructor(Name.Of("T", "mk"), Expr.Arrow(C("Secret"), C("T")))])], false));

        List<Name>? path = Replay.PathTo(env.Find, Name.Of("T"), Name.Of("Secret"));
        Assert.NotNull(path);
        Assert.Equal([Name.Of("T"), Name.Of("T", "mk"), Name.Of("Secret")], path!);
    }
}
