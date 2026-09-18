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
about twice the time, except a rejection by the unfolding limit (`DeterministicTimeoutException`),
which is final: the faithful mode could only repeat the work. `TENET_NO_FAILURE_CACHE=1` disables the fast mode entirely; the
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

## Reusing reductions between declarations, tried and rejected

A `TypeChecker` is built per declaration, so its reduction caches start empty each time. On all of `Init`,
`whnfCore` runs 27.7 million times and hits its cache 8% of the time, which looks like an obvious thing to fix:
share the caches across declarations and stop renormalizing the same types thousands of times.

There is even an argument that Tenet may do this and Lean may not. Whnf of a **closed** term depends only on the
constants it reaches; those are all present before the declaration using them is checked; this environment only
grows and refuses to redefine a name. So the answer cannot change once computed. Lean checks against an
environment that is still being built one declaration at a time, and has no such guarantee.

It was implemented and measured, and it is worth **nothing measurable**. The first measurement said 3%, from
five runs of one build against five runs of the other. That method is worthless on this machine: a later
interleaved A/B of an unrelated change, alternating the two binaries within each pair to cancel thermal drift,
found the *unchanged* binary winning four pairs of six and posting the fastest single run. Run-to-run spread on
this workload is around 10%, which swamps anything either change did.

Instrumenting the shared cache says why there was nothing to find:

| | |
| --- | --- |
| hits | 1,332,645 |
| misses | 15,689,576 |
| not eligible | 7,327,972 |

A 7.8% hit rate, which is the same 8% the per-declaration cache already gets. The reuse is not there to be had,
and the reason is structural rather than fixable. Type checking opens binders by generating fresh free
variables, so 30% of all reductions are on terms carrying variables local to one declaration. Those are unique
objects by construction and could never match across declarations even if sharing them were sound. What is left
is closed terms, and those simply do not recur often enough.

It was reverted. Five subtle conditions (closed terms only, safe checkers only, not under `eagerReduce`, not in
a faithful retry, and not while counting rules, since a reused result fires none) guarding the hottest path in
the kernel would have been a poor trade for 3%, and it was not even 3%.

**The lesson is about the measurement, not the cache.** Three separate performance claims were made here and all
three dissolved under a better method: a recursor pre-filter "worth 2%", this "worth 3%", and a GC heap-count
setting "worth 10%" that came from three samples. Anything measured on this workload needs interleaved pairs
and enough of them, because the noise is larger than any of the effects being claimed. `tools/bench` exists for
exactly this and should be used rather than a loop around `--timing`.

Where the time actually goes, for anyone who wants to try again: 7.2 million definition unfoldings on `Init`,
and a parallel speedup that stops paying at about four workers. Wall time goes 30.1s at one job to 13.3s at
four and only 11.0s at twelve, while summed kernel time across workers rises from 26s to 65s. That is memory
and allocation behavior, not duplicated reduction, and it is the more promising thread.

## Where the time goes, measured

Chasing speed by guessing produced three wrong answers in a row (above). Counting produced one answer, and
counting is worth more here because it is deterministic: the numbers below are identical whether or not the
machine is busy, which timing is not.

Checking all of `Init`, 64,814 declarations:

| | |
| --- | --- |
| expression nodes built | **252.3M** (App 187.3M, Lam 24.7M, Pi 21.5M, Const 8.9M, Sort 5.5M) |
| of the applications, built by | `Replace` 142.7M, `MkRevApp` 35.8M, `MkApp` 8.7M |
| `ExprOps.Replace` | **21.0M calls, 387.1M node visits** |
| its per-call memo | 215.7M entries stored, 9.3M hits |
| allocated | 54.5 GB |
| GC pause | 2.6s |

**It is not the garbage collector.** Allocation is 54.5 GB and GC pause 2.6s at one worker, four workers and
twelve, to three significant figures. Parallelism adds no allocation and no collection pressure, so the
scaling wall is not GC and tuning the collector cannot fix it. A gen0 sizing experiment bore that out: 2.5%
faster for 45% more resident memory, which is a bad trade for a checker whose memory is scored.

**It is substitution.** Three quarters of everything built is an application, and three quarters of those come
out of `Replace` rebuilding a term around a substituted variable. `Instantiate` already skips subterms with no
loose variable in range, and `Replace` already returns the original node when both children come back
unchanged, so the cheap structural wins are taken.

**The memo in `Replace` looks wasteful and is not.** It stores 215.7M entries to serve 9.3M hits, a 4% hit
rate, and deleting it is catastrophic: node visits go from 387M to 3,361M and the run from 29.3s to 81.5s.
Lean's terms are shared DAGs, so each of those few hits is skipping an enormous subtree. Anyone looking at the
hit rate and reaching for the delete key, as I did, should run it with `Replace`'s cache disabled first.

**What was done about it.** Lean's C++ kernel memoizes on the expression node itself, turning a hash insert
into a field write. That does not port here: workers share expression nodes across threads, so a mutable memo
field would need an allocation per entry to be written atomically, which costs more than it saves.

The cheaper half of the same idea does port. The memo was a fresh `Dictionary` per call: 21 million
allocations, each grown to about a dozen entries and discarded. It is now rented from a per-thread pool and
cleared for reuse, which keeps the memo and drops the allocation. A stack rather than one slot, because
`Replace` nests: `Instantiate`'s substitution calls `LiftLooseBVars`, which calls `Replace` again on the same
thread, and the inner call must not clear the outer one's table. Tables that grow past 65,536 entries are
dropped instead of pooled, so one pathological term does not park a huge table on every worker.

Measured on all of `Init`, and these are counters rather than timings so they hold on a busy machine:

| | before | after |
| --- | --- | --- |
| allocated | 54.6 GB | **31.5 GB** |
| gen0 collections | 54 | 39 |
| GC pause | 2.40s | **1.51s** |

Same verdict, 64,814 declarations and 0 failures, 127 tests on both frameworks, and the arena suite still 70 of
70 and 119 of 119. Wall-clock effect is not claimed here: every timing taken this day was on a machine busy
exporting Mathlib, and this repository has already published three speed claims that turned out to be thermal
drift. The 0.9 seconds of GC pause that stopped happening is real; what it is worth end to end should be
measured on a quiet machine with `tools/bench`.
