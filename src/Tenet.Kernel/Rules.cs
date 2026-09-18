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
    // ---------------------------------------------------------------- typing
    // Γ ⊢ e : T, one case per syntactic form, from InferTypeCore and its helpers.

    /// <summary>A loose bound variable reaching the checker, which a well-formed term never does. The error path.</summary>
    InferBVar,

    /// <summary>Γ, x : A ⊢ x : A. The local context holds the type.</summary>
    InferFVar,

    /// <summary>⊢ Sort u : Sort (u+1).</summary>
    InferSort,

    /// <summary>⊢ c.{v̄} : T[ū := v̄] for c : T declared with universe parameters ū.</summary>
    InferConst,

    /// <summary>Γ ⊢ f : (x : A) → B, Γ ⊢ a : A' with A ≡ A', therefore Γ ⊢ f a : B[x := a].</summary>
    InferApp,

    /// <summary>Γ, x : A ⊢ b : B therefore Γ ⊢ (fun x : A => b) : (x : A) → B.</summary>
    InferLam,

    /// <summary>Γ ⊢ A : Sort u, Γ, x : A ⊢ B : Sort v, therefore Γ ⊢ ((x : A) → B) : Sort (imax u v).</summary>
    InferPi,

    /// <summary>Γ ⊢ v : A, Γ, x : A := v ⊢ b : B, therefore Γ ⊢ (let x : A := v; b) : B[x := v].</summary>
    InferLet,

    /// <summary>Γ ⊢ e : S ā for a structure S, therefore Γ ⊢ e.i : the i-th field type, earlier fields substituted.</summary>
    InferProj,

    /// <summary>A natural number literal has type Nat, a string literal has type String.</summary>
    InferLit,

    // ---------------------------------------------------------------- reduction
    // e ↝ e', from WhnfCore and Whnf.

    /// <summary>(fun x => b) a ↝ b[x := a].</summary>
    Beta,

    /// <summary>(let x := v; b) ↝ b[x := v].</summary>
    Zeta,

    /// <summary>x ↝ v for a let-bound x := v in the local context.</summary>
    ZetaFVar,

    /// <summary>c.{v̄} ↝ its value, for a definition c, during weak head normalization.</summary>
    Delta,

    /// <summary>The same unfolding during lazy delta reduction, where only the side that needs it is unfolded.</summary>
    DeltaLazy,

    /// <summary>I.rec ... (I.ctor_i ā) ↝ the i-th minor premise applied to ā and the recursive results.</summary>
    Iota,

    /// <summary>
    /// K-like reduction. For an inductive proposition with one constructor and no fields, a major premise whose
    /// type is that proposition is replaced by the constructor even when it is a variable, so iota can proceed.
    /// </summary>
    IotaK,

    /// <summary>(S.mk ā).i ↝ aᵢ.</summary>
    Proj,

    /// <summary>Quot.lift f h (Quot.mk r a) ↝ f a.</summary>
    QuotLift,

    /// <summary>Quot.ind p (Quot.mk r a) ↝ p a.</summary>
    QuotInd,

    /// <summary>An arithmetic operation on Nat literals computed directly: add, sub, mul, div, mod, pow, gcd, and the bitwise ones.</summary>
    NatLitOp,

    /// <summary>A predicate on Nat literals computed directly: decEq, beq, ble.</summary>
    NatLitPred,

    /// <summary>A string literal expanded into its constructor form over a list of characters.</summary>
    StringLitToCtor,

    /// <summary>
    /// Lean.reduceBool and Lean.reduceNat, which would require running compiled code. Tenet refuses rather than
    /// trusting it, so reaching this rule is a rejection and not a reduction.
    /// </summary>
    NativeReduce,

    // ---------------------------------------------------------------- definitional equality
    // Γ ⊢ t ≡ s, from IsDefEqCore and its helpers.

    /// <summary>Structurally equal up to binder names and binder annotations, which the kernel ignores.</summary>
    DefEqSyntactic,

    /// <summary>Sort u ≡ Sort v when u and v denote the same universe, decided by level normalization.</summary>
    DefEqSort,

    /// <summary>c.{ū} ≡ c.{v̄} when the two level lists are equivalent pointwise.</summary>
    DefEqConst,

    /// <summary>f ā ≡ g b̄ when f ≡ g and the spines are equal pointwise, without unfolding either head.</summary>
    DefEqApp,

    /// <summary>Congruence under a binder: domains equal, then bodies equal under the extended context.</summary>
    DefEqBinding,

    /// <summary>f ≡ (fun x => f x), eta for functions.</summary>
    DefEqEta,

    /// <summary>s ≡ S.mk s.1 ... s.n for a structure S, eta for structures.</summary>
    DefEqEtaStruct,

    /// <summary>Any two elements of a type with one constructor taking no fields are equal.</summary>
    DefEqUnitLike,

    /// <summary>Γ ⊢ h : p, Γ ⊢ h' : p, p : Prop, therefore h ≡ h'. Proof irrelevance.</summary>
    DefEqProofIrrel,

    /// <summary>
    /// A string literal against an application of the string constructor. Keyed on String.ofList where it exists and
    /// String.mk otherwise, matching Lean's g_string_mk, and placed after lazy delta as Lean places it. On current
    /// Lean that ordering makes the rule unreachable in both kernels, since String.ofList is now a definition that
    /// delta unfolds first; no corpus on any version reaches it, and SafetyTests covers it directly.
    /// </summary>
    DefEqStringLit,

    /// <summary>Γ, x : A ⊢ x ≡ x. The same free variable on both sides.</summary>
    DefEqFVar,

    /// <summary>
    /// A closed term against Bool.true, reduced rather than compared. Proofs by reflection end here, which is why
    /// the kernel reduces one side fully instead of looking for a shared structure.
    /// </summary>
    DefEqReflect,

    /// <summary>
    /// Two projections of the same field, compared by unfolding the structures they project from rather than by
    /// comparing those structures whole.
    /// </summary>
    DefEqLazyDeltaProj,

    /// <summary>
    /// A projection applied to arguments, unfolded through the projection function during lazy delta so that a
    /// structure built by a definition can meet one built by another.
    /// </summary>
    UnfoldProjApp,

    /// <summary>Nat literals and successor offsets compared by arithmetic rather than by unfolding Nat.succ.</summary>
    DefEqOffset,

    /// <summary>
    /// The lazy delta loop: where both sides have delta-reducible heads, unfold the one with the greater height,
    /// so a shared definition is not unfolded on both sides at once.
    /// </summary>
    DefEqLazyDelta,
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

    /// <summary>
    /// Rules that no input can reach, because an earlier check decides the same case first. They are kept because
    /// Lean's kernel has the branch in the same place and this catalog is a correspondence, not an inventory of
    /// live code, but a coverage table that lumps them in with untested rules is lying in the direction that
    /// matters: it reports work still to do where there is none, and hides the rules that genuinely have no test.
    /// </summary>
    public static string? SubsumedBy(Rule r) => r switch
    {
        // Two free variables with one id are structurally equal, so DefEqSyntactic settles them at the top of
        // QuickIsDefEq and the branch in IsDefEqCore is never entered. Lean's is_def_eq_core has the same branch,
        // after the same syntactic check, and it is unreachable there for the same reason.
        Rule.DefEqFVar => "DefEqSyntactic, which decides structurally equal terms first",
        _ => null,
    };

    /// <summary>Rules a run did not reach and that some input could have: the ones a coverage gap is about.</summary>
    public static IEnumerable<Rule> UnexercisedAndReachable() => Unexercised().Where(r => SubsumedBy(r) is null);
}
