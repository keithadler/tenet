# Design notes

## What the kernel is

Lean 4's trusted core is a small type checker for a dependent type theory: the Calculus
of Inductive Constructions with a predicative universe hierarchy above an impredicative,
proof-irrelevant `Prop`, definitional eta for functions and structures, quotient types,
and inductive families with a K-like rule for subsingleton eliminators. Everything
else in Lean (tactics, elaboration, the compiler) produces terms that this core checks.

Tenet is that core, in C#, with the same algorithmic decisions where they affect what is
accepted:

| Concern | Tenet |
| --- | --- |
| Terms | `Expr`: locally nameless, de Bruijn indices for bound variables, globally unique ids for free variables. Structural equality ignores binder names and binder annotations, as Lean's does. Each node caches its hash, loose-variable range, and whether it mentions free variables or universe parameters. |
| Universes | `Level` with the reference normalization: flatten `max`, sort, drop subsumed offsets. `IsEquiv` compares normal forms; `IsGeq` is the same sound-but-incomplete test Lean uses for universe constraints on constructor fields. |
| Environment | Append-only map from names to `ConstantInfo`. A child environment stages the auxiliary types used to eliminate nested inductives. |
| Inference | `TypeChecker.Infer` (assumes well-typed input) and `Check` (validates), with separate caches; free variables are introduced through a `LocalContext` whose declarations never change. |
| Reduction | `WhnfCore` (beta, zeta, projections, iota, quotient rules) and `Whnf` (adds delta and literal arithmetic), both cached. |
| Definitional equality | Lazy delta reduction guided by reducibility hints and definitional heights, with proof irrelevance, eta for functions and structures, unit-like types, `Nat` literal offsets, and `String` literal expansion, in the same order as the reference. Success pairs are cached per checker as in the reference; failure pairs are cached too (see below). |
| Inductives | Positivity, universe constraints, parameter uniformity, then recursor generation. Nested occurrences are replaced by auxiliary mutual types, checked, and translated back. |

## Why the order matters

Definitional equality is a semi-decision procedure: sound, incomplete, and not
transitive. Two kernels that make the same set of reductions can still disagree on
whether a particular pair of terms is convertible if they try things in a different
order or cache differently. Tenet follows the reference kernel's order so that anything
Lean accepts, Tenet accepts, and its checks are strictly a subset of Lean's reductions
so that the converse also holds. This is also why some code reads as a transliteration
of an algorithm rather than an idiomatic .NET design: the algorithm is the spec.

## The failure cache

The reference kernel caches successful definitional-equality checks for the whole
declaration but failed ones only inside lazy delta reduction. On some Mathlib
declarations the same failing comparison is then repeated hundreds of times from
different call sites (`PresheafOfModules.freeObj._proof_2` did 8 million unfoldings
where Lean's own kernel needed 30 thousand, because a comparison such as
`fvar =?= DFunLike.coe …` that needs functoriality is false and gets asked again on
every unfolding step). Tenet therefore also caches failures at the top-level
`IsDefEq`, which makes the check up to 40 times faster on such declarations.

A cached failure can only make the check stricter: every `true` is still justified by
an actual derivation, so soundness is untouched, but because definitional equality is
not transitive, a comparison that failed in one context can succeed later after more
unfolding, and the cache would then reject a declaration the reference accepts. To
keep verdicts identical to the reference, `Environment.Add` and `Validate` run a
rejected declaration a second time with the failure cache off (`TypeChecker.FaithfulScope`)
and report only that result. Accepted declarations are never re-run; a rejection costs
about twice the time. `TENET_NO_FAILURE_CACHE=1` disables the fast mode entirely; the
differential tests (docs/testing.md) are run both ways.

The re-check is not theoretical: of the 765,497 declarations in Mathlib and its
dependencies, 1,039 are rejected by the fast mode and accepted by the faithful one
(`--stats` reports the count as "faithful retries"); `AlgebraicGeometry.isIso_pushoutSection_of_iSup_eq`
is one of them.

## Trust

To trust a Tenet run you have to trust:

- the .NET runtime and compiler;
- `Tenet.Kernel` (about 3,500 lines) and the export reader;
- that the export faithfully reflects the declarations Lean checked, which lean4export
  guarantees only for the declarations and expressions themselves (it reads them out of
  Lean's environment; it does not re-elaborate anything).

You do not have to trust Lean's kernel, its compiler, or any tactic.

Native reduction (`Lean.reduceBool`, `Lean.reduceNat`) is refused: it means running the
compiled form of a Lean function, and an external checker has no way to know that
compilation was correct.

## Non-goals

- Elaboration, tactics, or a Lean frontend of any kind.
- Bit-for-bit reproduction of Lean's error messages.
