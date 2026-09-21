# Divergences from Lean's kernel

Tenet aims to decide exactly what Lean's kernel decides. Anywhere it does not, and the difference is not listed
here, it is a bug and should be reported.

The practice, and the name of this file, is borrowed from
[lean4lean](https://github.com/digama0/lean4lean/blob/master/divergences.md).

## What kind of divergence each one is

Asked to sort these into "theory" and "implementation", the honest answer is that almost none of them are
about the type theory, and the interesting split is elsewhere.

| | Divergence | Kind |
| --- | --- | --- |
| 1 | An accelerated primitive is checked before it is trusted | **Threat model** |
| 2 | A literal's type is checked against the environment | **Threat model** |
| 3 | The `_nested` namespace is reserved | **Threat model** |
| 4 | Failure caching | **Optimization, observationally neutral** |
| 5 | Bounds the reference does not have | **Resource** |
| 6 | Complete level equality, available and off | **Algorithm against theory** |
| 7 | `#print axioms` under-reports across a module boundary | **Upstream defect** |

**Threat model (1, 2, 3).** Not disagreements about what is true. Lean ships its own prelude and does not
support replacing it, so deciding from the name that `Nat.add` is addition, or that a numeral is a `Nat`, is
sound *for Lean*. Tenet reads a file somebody else produced, which is its entire purpose, so the same shortcuts
are holes. In each case Tenet ends up **closer to the type theory than Lean is**, by doing work Lean can
correctly skip. None of these can arise on an honest export.

**Optimization (4).** Caching failed definitional-equality comparisons can only make a checker stricter, since
definitional equality is not transitive. A declaration rejected in that mode is re-checked with the cache off
and the reference algorithm's verdict is the one reported, so nothing is observable from outside.

**Resource (5).** The theory has no notion of running out of stack or of a hundred million unfoldings. An
external checker needs one, because a kernel that never returns is as useless as one that answers wrongly.

**Upstream defect (7).** The only entry where Lean is wrong rather than differently right, and the only one
outside the kernel proper: it is about what `tenet axioms` reports, not about what the kernel accepts. Tenet
matches `#print axioms` everywhere except one case, and in that case `#print axioms` contradicts itself.

**Algorithm against theory (6).** The only entry that is genuinely about the theory. Lean's `is_equivalent`
normalizes each side once and compares, so `imax u v` against a semantically equal `max` form is not settled
and Lean answers no. Deciding by case analysis on which parameters can be zero settles every such pair, which
is what the *semantics* of `imax` says, and it can only accept more, never less. It is off by default anyway,
because deciding what Lean decides is this project's first claim and the gap cannot arise on a real export.

## An accelerated primitive is checked before it is trusted

**Where:** `Primitives.Check`, consulted by `TypeChecker.ReduceNat`.

**What Lean does:** computes `Nat.add 2 2` by adding two machine integers rather than unfolding the declaration,
and decides to do that from the name alone. Nothing checks that the constant called `Nat.add` is addition.

**What Tenet does:** takes the shortcut only for a constant whose behavior it has checked. Eight primitives are
checked against their defining equations over free variables (`add x 0 ≡ x`, `add x (succ y) ≡ succ (add x y)`, and
so on); the rest are recursions with no clause form and are exercised at sampled values instead.
`Primitives.EquationsKnown` says which primitive got which. A constant that fails is unfolded rather than rejected,
so an unusual but honest prelude is checked slowly instead of refused.

**Why:** Lean ships its prelude and does not support replacing it, which is what makes dispatching on the name safe
there. Tenet reads a file somebody else produced; that is its entire purpose. An export declaring
`Nat.add := fun a b => a` made Tenet accept `2 + 2 = 4`, which is false of the declaration in front of it, and
reject `2 + 2 = 2`, which is true of it.

**Consequence for differential testing:** on a mutated export that damages a constant named after a primitive,
Lean keeps computing the operation from the name while Tenet unfolds the body the file actually contains. The two
then part company in whichever direction the damaged body happens to fall, and both directions occur.

Usually Tenet is the stricter one: Lean accepts arithmetic the damaged file no longer states, and Tenet rejects
it. On prelude seed 61 that is 85 rejections across 15 variants.

But not always. On seed 128 the damaged `Nat.pow` makes a `decide` proof come out true, so Tenet accepts
`Char.ofNat._proof_1` and Lean, computing the real value, rejects it. That surfaces as Tenet being *laxer* than
Lean, which is the alarming direction, and it is the same cause: Tenet is the one reading the file.

`Tenet.DiffTest` reads `unvalidatedPrimitives` from the check report and attributes both directions, naming the
primitives responsible and every declaration involved rather than quietly dropping them. Attributing an apparent
soundness disagreement is worth more scrutiny than attributing a strictness one, which is why the declarations are
printed and the counts kept separate. The first version of this attributed only the strictness direction, and the
other one failed CI three commits later.

**Turning it off:** `TENET_NO_PRIMITIVE_CHECK=1` restores Lean's behavior of dispatching on the name.

## A literal's type is checked against the environment

**Where:** `TypeChecker.InferLit`, via `Primitive.NatLiteralType` and `Primitive.StringLiteralType`.

**What Lean does:** gives a numeric literal the type `Nat` and a string literal the type `String` by assertion.
lean4lean's divergences file records that the kernel "was not checking that the literal type actually exists", and
that this is tolerable for the same reason as the primitives: Lean ships its prelude.

**What Tenet does:** refuses a literal unless the environment's `Nat` is the two-constructor inductive the literal
denotes, and its `String` is a structure reachable from a list of characters.

**Why:** without it, a numeric literal is a term of whatever the file happens to call `Nat`. An export declaring
`def Nat : Prop := False` and then `def boom : False := 3` was accepted, with an empty axiom list. `tenet audit`
called it unconditional. That is a proof of `False` from a file with no `sorry` and no axioms, and it is the most
serious thing this project has found in itself.

## The `_nested` namespace is reserved

**Where:** `Environment.AddCore(Declaration, ...)`.

**What Lean does:** rejects a declaration using the reserved `_nested` prefix, reserving the whole namespace
against unrelated user declarations. lean4lean checks only constructor types and notes the difference.

**What Tenet does:** the same as Lean, rejecting any declaration whose own name sits in that namespace, in
addition to the existing check on the types.

**Why:** eliminating a nested inductive derives auxiliary types under that prefix. A file that occupies one of
those names first is betting on how the elimination resolves the name it finds, and that is a question worth not
having. The kernel's own auxiliaries are installed through `AddCore(ConstantInfo)`, which does not go through this
check, so an honest file loses nothing.

## Failure caching

**Where:** `TypeChecker.CacheFailures`.

**What Lean does:** caches successful definitional-equality comparisons only. Definitional equality is not
transitive, so reusing a failure can make the check stricter than the reference.

**What Tenet does:** caches failures as well, which on some Mathlib declarations avoids repeating one failing
comparison hundreds of times. A cached failure can only make the checker reject more, never accept more, so a
declaration rejected in this mode is re-checked with the cache off (`Environment.RunFastThenFaithful`) and the
verdict reported is the reference algorithm's.

**Turning it off:** `TENET_NO_FAILURE_CACHE=1`.

## Bounds the reference does not have

**Where:** `TypeChecker.MaxUnfolds`, `TypeChecker.NatMaxSizeBytes`, `StackGuard`.

**What Lean does:** bounds kernel work through the elaborator's heartbeats rather than in the kernel, and computes
a `Nat` literal of any size its memory allows.

**What Tenet does:** stops after 100 million definition unfoldings per declaration, refuses a literal computation
above 128 MB, and rejects a term deeper than the stack rather than letting the process abort. Each raises a
`KernelException` and is reported as a rejection, so a declaration stopped by one of them is not silently accepted.

**Why:** an external checker is given files it did not produce, and a kernel that never returns is as useless as
one that answers wrongly. A .NET stack overflow in particular cannot be caught, so without the guard one
pathological declaration takes down every other declaration being checked alongside it.

**Turning them off:** `TENET_MAX_UNFOLDS` sets the unfolding bound; `--stack-mb` sets the stack.

## Complete level equality, available and off

**Where:** `Level.CompleteEquality`, consulted by `Level.IsEquiv`.

**What Lean does:** normalizes each side once and compares. `imax u v` is `0` when `v` is and `max u v`
otherwise, so a pair whose meaning turns on that is not settled, and Lean answers no.

**What Tenet does by default:** the same, because deciding what Lean decides is this project's first claim.

**What it can do instead:** decide by case analysis on which parameters can be zero. Splitting each into `0` and
`succ p` resolves every `imax`, and what is left is `max` and `succ` over parameters, which the ordinary
comparison settles symbolically for all values. Every branch must agree before it answers yes, so it can only
accept more, never less. `TENET_COMPLETE_LEVELS=1` turns it on.

**Why it exists:** this was the one place two other checkers were measurably ahead. con-leche showed it rather
than claimed it: on a mutated `Init.Core` it accepts `PULift.noConfusion`, where the level in dispute is
`imax (imax s (max r 1)) u` against `imax (max (max 1 r) s) u`. `max r 1` is at least 1 for every assignment, so
the inner `imax` is a `max` and the two are one universe. lean4lean makes the same move and documents it the same
way.

**Why it is off:** turning it on makes Tenet accept declarations Lean rejects, which the differential harness
would report, correctly, as a disagreement. The gap it closes cannot arise on a real export, since the elaborator
never stores an unsimplified `imax _ (max _ 1)`. Costing nothing measurable on `Init` either way, it is worth
having and not worth defaulting to.

## `#print axioms` under-reports across a module boundary

**Where:** `Replay.AxiomsOf` and `Replay.AxiomEdges`, behind `tenet axioms`, `tenet why` and `tenet audit`.

Lean answers `#print axioms S9` one way from the module that defines `S9` and another way from a module that
imports it. For the structure in [leanprover/lean4#15226](https://github.com/leanprover/lean4/issues/15226),
whose field type rests on `Classical.choice`, the defining module correctly reports `[Classical.choice]` and an
importing module reports no axioms at all. Tenet reports `Classical.choice` from either.

This is not a difference of opinion. Lean disagrees with itself, and the wrong answer is the one that says a
declaration rests on nothing.

`Lean.CollectAxioms.collect` caches one axiom set per constant so that an imported declaration is walked once.
An inductive and its constructors refer to each other, so a sentinel goes into the cache before the recursion
starts. Nothing distinguishes that sentinel from a finished result. When a constructor's walk reaches its own
inductive while the inductive's entry is still being computed, the constructor reads the sentinel, and the
inductive is then cached with only what had been collected by that point. Which of the two is walked first
depends on the order the module's constants come out in, which is why the bug fires for some modules and not
others, and why the reproduction in `tests/fixtures/axioms/` is kept exactly as it was reported.

Tenet keeps one accumulator and one `seen` set for the whole walk and caches nothing per constant. There is no
per-constant result to be poisoned, so there is no sentinel to mistake for an answer, and the module a
declaration is read from cannot change what it rests on. This costs a repeat of the walk for each query, which
is the right trade for a command that is asked about one declaration at a time and whose whole job is to be
right about holes.

**Tenet has no standing to be smug about this.** The same walk had two under-reports of its own until the
fixture that compares it against Lean case by case was written: an axiom reachable only through a
constructor's field type was invisible, so a structure whose field rested on `sorry` came back clean, and
reaching an axiom ended the walk, so an axiom stated in terms of another hid the second. Both were found by
reading #15226 and asking whether Tenet had the same class of bug. It had a worse one, in every module rather
than only across an import.

**If Lean fixes it**, `tests/fixtures/axioms/compare.sh` fails and says so. The right response is to delete
this entry and fold the reproduction into the main comparison, not to keep a divergence that no longer exists.
