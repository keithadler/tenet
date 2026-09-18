# Divergences from Lean's kernel

Tenet aims to decide exactly what Lean's kernel decides. Anywhere it does not, and the difference is not listed
here, it is a bug and should be reported.

The practice, and the name of this file, is borrowed from
[lean4lean](https://github.com/digama0/lean4lean/blob/master/divergences.md).

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
Lean keeps computing the operation and accepts declarations that the damaged file no longer states, while Tenet
unfolds and rejects them. This shows up as Tenet being stricter than Lean and is not a defect. `Tenet.DiffTest`
reads `unvalidatedPrimitives` from the check report and reports those rejections separately rather than counting
them as disagreements. On prelude seed 61 that is 85 rejections across 15 variants, every one of them attributable
to `NatAdd`, `NatMul`, `NatPow`, `NatBeq` or `NatBle` having been damaged.

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

## Known incompleteness, not a deliberate choice

Level normalization: Tenet decides fewer universe equalities than con-leche does. On a mutated `Init.Core`, where
the level in question is `imax (imax s (max r 1)) u` against `imax (max (max 1 r) s) u`, con-leche accepts and
Tenet rejects. It is in the safe direction, it cannot arise on a real export since the elaborator never stores an
unsimplified `imax _ (max _ 1)`, and it is recorded in `docs/testing.md` rather than here because it is a gap to
close and not a decision.
