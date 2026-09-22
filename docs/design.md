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

### Why the fast checkers are fast: they never substitute

Before attempting anything, read `sokonanoda`, which does Mathlib in 2.1m where Lean's own kernel takes 32.9m.
Its description says it outright: *"conversion checking uses closures"*, inspired by
[smalltt](https://github.com/AndrasKovacs/smalltt). The source confirms it. `Closure { env, ctx, body }`,
`eval(depth, env, expr) -> Value`, `apply_closure(depth, clo, value, _)`, a `Value` type with rigid neutral
heads and unfold heads, eliminations packed into a `u64`, and `bumpalo::Bump` arenas throughout. The only
`subst` functions in the whole repository are for universe **levels**. Nothing ever substitutes into a term.

That is normalization by evaluation, and it is a different evaluator, not a faster one. Beta reduction is
applying a closure to a value in an environment. The work this kernel does, 21M `Replace` calls, 387M node
visits and 252M expression nodes built, does not exist there at all, because rebuilding a term around a
substituted variable is the thing NbE is designed never to do.

So the 79% duplicate rate measured below is a symptom rather than a disease. Substitution keeps reconstructing
terms it has already constructed; interning would stop paying for them twice, while NbE never builds them.

**The decision, made deliberately: Tenet is not competing on speed.** Faithfulness is the point of it, and
the two pull opposite ways. Everything below is kept as a record of what was measured and what was ruled out,
so the question does not get reopened from scratch, but no work is planned against it.

**The tension behind that decision is real and is not a performance question.** Tenet's first claim is that it decides
exactly what Lean's kernel decides, and it matches Lean's algorithm deliberately, down to the order lazy delta
unfolds things. Definitional equality in Lean is incomplete on purpose, so *which* pairs get decided depends on
the reduction strategy, not only on the theory. Swapping in NbE would put that claim at risk in a way no
amount of testing fully retires: agreeing on 189 arena tests and on Mathlib is not agreeing on every input.
A kernel can be a faithful mirror of Lean's algorithm or it can be fast, and those pull in opposite directions.
That is a decision about what this project is for, not an optimisation to schedule.

### Interning, not tried, and the measurement that said it was worth trying

Of the 186.7M application nodes built while checking `Init`, **147.4M are structurally equal to one already
built**: 78.9%, leaving 39.3M distinct. Measured by hashing each newly built application and counting how
often the hash had been seen; at 39.3M distinct values in a 32-bit space the collision error is under 1%, far
too small to change the conclusion.

So hash-consing would build roughly a fifth as many applications. The allocation saving is the smaller half of
the prize. The larger half is that interning makes structural equality into pointer equality, and this kernel
does 12.2M definitional-equality comparisons and 27.7M `whnfCore` lookups whose cost is dominated by comparing
and hashing terms structurally.

It has not been attempted, and the measurement does not say it pays. What it rules out is the reason not to
try: the duplication is real and large rather than a few percent. The cost against it is one lookup in a shared
table per node built, 186.7M of them, across four workers that would contend for it. Sharding or per-thread
tables trade that contention for lost sharing. Lean's own C++ kernel does not hash-cons, which may be why it
and this checker sit in the same performance neighbourhood while the Rust implementations on the arena are an
order of magnitude faster.

**What was done about the memo.** Lean's C++ kernel memoizes on the expression node itself, turning a hash
insert into a field write. That does not port here: workers share expression nodes across threads, so a mutable memo
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
70 and 119 of 119.

**Measured on a quiet machine afterwards, it is worth about 1% of wall time**: eight interleaved pairs,
medians 13.08s to 12.93s, faster in six pairs of eight. Real, and far smaller than a 42% allocation cut
suggests, for the same reason the GC numbers gave earlier: with the server collector and four workers,
collection overlaps with work on other threads, so removing 0.9s of pause does not remove 0.9s of wall time.
Collection was never on the critical path.

The change is kept for the allocation, not the speed. Less memory pressure is worth having on a constrained
runner, and this project is measured on memory as well as time. But nobody should expect it to make checking
faster, and the bottleneck it was chased for is untouched: the work is in the 387M node visits themselves, not
in the garbage they leave behind.

## Where the memory goes, measured

The arena scores memory as well as instructions, and memory is where Tenet is furthest from the
reference: on `con-leche` it was 3.7 GB against the official kernel's 872 MB, a factor of 4.4, while
instructions were only 31% over. Time had been measured to death (above) and memory had not been
measured at all, so that is where the headroom was.

Most of it is not live data. Measured on the `Lean` export, 164,133 declarations and 11.98M
expressions, `--jobs 4` throughout, on a twelve-core machine:

| | wall | CPU | peak resident |
| --- | --- | --- | --- |
| baseline (server GC, heaps unset) | 26.3s | 128.5s | **3.67 GB** |
| `DOTNET_GCHeapCount=4` | 29.3s | 125.9s | **2.06 GB** |
| `DOTNET_GCHeapCount=2` | 35.8s | 130.7s | 1.71 GB |
| `DOTNET_GCHeapCount=4` + `GCConserveMemory=9` | 31.4s | 130.8s | 1.74 GB |
| `DOTNET_PROCESSOR_COUNT=4` | 30.2s | 127.8s | 2.18 GB |
| workstation GC | 47.1s | 133.1s | 1.33 GB |

**Server GC sizes itself from the machine, not from `--jobs`.** Twelve cores means twelve heaps, each
carrying its own gen0 headroom, even though only four workers ever run. Telling it four cuts peak
resident by 44% and costs nothing in instructions: 125.9s of CPU against 128.5s, well inside the
run-to-run spread this workload has. Wall time is 9% worse because collection stops overlapping
checking across as many threads, which is the right trade on a board that scores instructions.

This is not the heap-count claim that was thrown out earlier in this file. That one was about
*speed*, came from three samples, and dissolved. This is about *memory*, where a 44% difference is
far outside the noise and reproduces on every run.

**The floor is the environment, and it is about 1.3 GB.** The workstation row measures roughly the
live set, since a single-heap non-concurrent collector keeps little headroom. Streaming does not
help: the environment holds every declaration, proof terms included, because a later declaration may
refer to any earlier one. The `ExportFile` tables the reader fills are a red herring, at 11.98M
expression slots and 970k name slots they are under 110 MB of references to objects the environment
already holds.

**What would go below the floor, and why it has not been done.** A theorem's value is needed to check
that theorem and, in practice, never again: a theorem's type is a `Prop`, and proof irrelevance
decides proofs equal without looking at them. Dropping values after checking would take a large bite
out of 1.3 GB on any corpus that is mostly proofs, which is all of them. But Lean's kernel does treat
theorems as delta-unfoldable, and the one claim this project rests on is that it decides what Lean
decides. Trading that for memory is a question about what Tenet is, in the same family as the NbE
decision above, and it is not being settled by assuming proof irrelevance always gets there first.

## Why Mathlib costs more than the other corpora

The Lean Kernel Arena scores instructions retired, not wall time. On its corpora Tenet sits within 8% of
Lean's own kernel on `init`, `std` and `cedar`, and slightly **ahead** on `cslib` at 0.96x. Mathlib is the
outlier at **1.38x**, and it is worth knowing why before anyone tries to fix it.

Two hypotheses, measured on all of Mathlib, four jobs, quiet machine:

| | CPU | wall | peak RSS |
| --- | --- | --- | --- |
| baseline | 1815.7s | 574.7s | 8.88 GB |
| `TENET_NO_FAILURE_CACHE=1` | 1760.2s | 564.1s | 8.48 GB |
| `--no-compare` | **1505.5s** | 475.1s | 7.55 GB |

**The failure cache is not it.** Disabling it entirely, along with the 924 faithful retries it causes, moves
CPU by 3%. The kernel then does noticeably more work (whnfCore +19%, unfolds +24%) for slightly less CPU,
which says the cache's own bookkeeping roughly cancels what it saves at this scale.

**Deriving and comparing recursors is it, or half of it.** Skipping that work is worth **17% of instructions**
and 15% of memory. Removing it would put Tenet at about 1.15x official, in line with every other corpus, and
the cost lands on Mathlib specifically because Mathlib has far more inductive types than anything else on that
board.

**It stays.** Lean's `Environment.replay` installs the recursors it is handed. Tenet re-derives each one from
the inductive's own types and constructors and compares field by field with what the exporter wrote, which is
the check that catches an exporter or a reader that quietly dropped something, and the one path the kernel
comparison cannot reach. Deleting a real check to climb a column is the one thing a proof checker must not do.
The honest framing is that 17% of Tenet's Mathlib cost buys something the reference does not attempt.
