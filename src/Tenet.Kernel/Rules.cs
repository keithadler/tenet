using System.Threading;

namespace Tenet.Kernel;

/// <summary>
/// The kernel's rules, named one by one, and how often each fired.
///
/// "All of Mathlib checks with no failures" is the headline evidence for this project, and on its own it does not
/// say what was exercised. A rule no corpus ever reaches is untested however many declarations passed through, and
/// nothing in a green run distinguishes a rule that works from a rule that is never called. Counting them turns
/// that from an assumption into a measurement: run the corpora, print the table, and any rule sitting at zero is a
/// hole in the evidence rather than a rule known to be right.
///
/// The catalog is also the skeleton of a specification. Each entry names one typing or reduction rule and the
/// method that implements it, so the list can be read against the type theory rather than against the code.
///
/// Counting is off unless <see cref="TypeChecker.Stats.Enabled"/> is set, because an atomic increment on
/// <see cref="Rule.Beta"/> would serialize every worker.
/// </summary>
public enum Rule
{
    // Typing: one per syntactic form, from InferTypeCore and its helpers.
    InferBVar, InferFVar, InferSort, InferConst, InferApp, InferLam, InferPi, InferLet, InferProj, InferLit,

    // Reduction, from WhnfCore and Whnf.
    Beta,              // (fun x => b) a
    Zeta,              // let x := v; b
    ZetaFVar,          // a let-bound free variable in the local context
    Delta,             // unfolding a definition in whnf
    DeltaLazy,         // unfolding during lazy delta reduction, where only one side needs it
    Iota,              // a recursor applied to a constructor
    IotaK,             // K-like reduction: the major premise forced to a constructor by its type
    Proj,              // projecting a field out of a constructor application
    QuotLift,          // Quot.lift f h (Quot.mk r a)
    QuotInd,           // Quot.ind p (Quot.mk r a)
    NatLitOp,          // an accelerated Nat operation on literals
    NatLitPred,        // an accelerated Nat predicate on literals
    StringLitToCtor,   // a string literal expanded to its constructor form
    NativeReduce,      // Lean.reduceBool and Lean.reduceNat

    // Definitional equality, from IsDefEqCore and its helpers.
    DefEqSyntactic,    // structurally equal, no reduction needed
    DefEqSort,         // two sorts, decided by level equivalence
    DefEqConst,        // the same constant at equivalent levels
    DefEqApp,          // congruence on an application spine
    DefEqBinding,      // congruence under a lambda or pi
    DefEqEta,          // eta for functions: f and fun x => f x
    DefEqEtaStruct,    // eta for structures: s and S.mk s.1 ... s.n
    DefEqUnitLike,     // any two elements of a unit-like type
    DefEqProofIrrel,   // any two proofs of the same proposition
    DefEqStringLit,    // a string literal against its constructor form
    DefEqOffset,       // Nat literals and successor offsets compared without unfolding
    DefEqLazyDelta,    // the lazy delta loop chose which side to unfold
}

/// <summary>Per-rule hit counts. See <see cref="Rule"/> for why this exists.</summary>
public static class Rules
{
    private static readonly long[] s_hits = new long[System.Enum.GetValues<Rule>().Length];

    /// <summary>Record that a rule fired. A no-op unless counting is enabled.</summary>
    public static void Hit(Rule r)
    {
        if (TypeChecker.Stats.Enabled)
        {
            Interlocked.Increment(ref s_hits[(int)r]);
        }
    }

    public static long Count(Rule r) => Interlocked.Read(ref s_hits[(int)r]);

    public static void Reset() => System.Array.Clear(s_hits);

    /// <summary>Every rule with its count, in declaration order, including the ones that never fired.</summary>
    public static IEnumerable<(Rule Rule, long Hits)> All() =>
        System.Enum.GetValues<Rule>().Select(r => (r, Count(r)));

    /// <summary>The rules no run has reached. Each one is a part of the kernel the evidence does not cover.</summary>
    public static IEnumerable<Rule> Unexercised() => All().Where(x => x.Hits == 0).Select(x => x.Rule);
}
