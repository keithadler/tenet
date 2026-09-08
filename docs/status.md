# Status

Updated 2026-09-08.

| Export | Declarations | Result |
| --- | --- | --- |
| `tests/fixtures/Nat.add_succ.ndjson` (format 3.1.0, Lean 4.34.0-rc2) | 7 | checks; recursor for `Nat` derived and equal to Lean's |
| `tests/fixtures/Nat.add_succ.v3.0.ndjson` (format 3.0.0, Lean 4.27.0-rc1) | 20 | checks |
| `Init.Prelude` (Lean 4.34.0-rc2) | 1,824 | 0 failures, 0.2 s |
| `Init.Core` (Lean 4.34.0-rc2) | 3,468 | 0 failures, 0.4 s |
| `Init`, the whole core library (Lean 4.34.0-rc2) | 58,135 (59,591 constants) | 0 failures, 7 s wall clock including parsing (12 jobs); 21 s of checking with 1 job |
| `Mathlib.Data.Real.Basic` and everything it imports (Mathlib master, 2026-09-08) | 179,215 (186,458 constants) | 0 failures, 9.8 s with 12 jobs, 4.2 GB peak |
| all of `Init` from the toolchain's `.olean` files (`tenet check Init.olean --all`) | 64,814 units in 649 modules (includes `partial`/`unsafe` definitions the export omits) | 0 failures, 8 s, 2.5 GB peak |
| **all of Mathlib and its dependencies from `.olean` files** (`tenet check Mathlib.olean --all`, Mathlib master 2026-09-08) | **765,497 units in 10,726 modules** | **0 failures, 6.1 min, 9 GB peak** |
| Mathlib, first 5.9 GB of the export (Mathlib master, 2026-09-08; see note) | 657,351 (673,865 constants) | 0 failures, 16 min with `--low-memory` and 8 jobs, 8.4 GB peak |

Note on the export-based Mathlib row: the exporter, not Tenet, ran out of memory on the
17 GB laptop after writing 5.9 GB; the file ends mid-line. The `.olean` path has no such
limit: modules are memory-mapped and imported constants are decoded on demand, so the whole
library fits. Tenet checked every complete declaration
(reported as INCOMPLETE, exit 3) and rejected none. A complete run needs a machine with
more memory for lean4export.

## Performance

The first Mathlib runs had single declarations taking 30 to 88 seconds. Profiling
`PresheafOfModules.freeObj._proof_2` against Lean's own kernel (`set_option diagnostics
true` and `trace.profiler`) showed Tenet doing 8 million definition unfoldings where
Lean did 30 thousand: the same failing definitional-equality comparison was being repeated
from hundreds of call sites, because the reference caches failures only inside lazy delta
reduction. Tenet now caches them for the whole declaration and re-checks a rejection with
the cache off so verdicts are unchanged (docs/design.md, "The failure cache"). That
declaration went from 9.3 s to 0.5 s (Lean: 0.23 s), and the whole of Mathlib from 6.5 min
to 6.1 min (the slow declarations were a small share of the total); 1,039 of the 765,497 declarations needed the faithful re-check.

The slowest declarations now take 5 to 13 seconds in the parallel Mathlib run, and the
failure cache makes no difference to them. Measured against Lean's own kernel on the same
exports (`LEANCHECK_TIMES=1`, see docs/testing.md), one declaration at a time and one
thread each:

| Declaration | Lean's kernel | Tenet |
| --- | --- | --- |
| `CategoryTheory.instIsIsoIndCoimageImageComparison` | 15.0 s | 11.4 s |
| `CategoryTheory.instAbelianInd` | 1.7 s | 1.1 s |
| `MayerVietorisSquare.biprodAddEquiv_symm_biprodIsoProd_hom_toBiprod_apply` | 7.0 s | 4.4 s |
| `MayerVietorisSquare.sequenceIso._proof_2` | 5.4 s | 3.8 s |
| whole export up to `Mathlib.CategoryTheory.Abelian.Indization`, 220,649 declarations | 116.5 s of kernel time | 88 s wall with 1 job (parse bound), 28 s with 12 |
| whole export up to `...SheafCohomology.MayerVietoris`, 300,181 declarations | 226.0 s of kernel time | 42 s wall with 12 jobs |

So these declarations are inherently expensive and Tenet's kernel is now somewhat faster than
the reference on them single-threaded. In the 12-job runs the same declarations take two to
three times longer than alone (memory bandwidth and the shared garbage collector), which is
where the remaining wall-clock time on Mathlib goes.

Measured on a 12-core Apple M-series laptop with 17 GB, .NET 10, server GC. The reader
parses about 145 MB/s and runs concurrently with checking, so wall time is close to the
larger of the two.

## Re-checking a published formalization

OpenAI's [NavierStokesAndEuler](https://github.com/openai/NavierStokesAndEuler) (commit
`8937a8f`, Lean 4.34.0-rc2, built against Mathlib with `lake build`) formalizes finite-time
blowup for the three-dimensional Navier-Stokes and Euler equations.

| | |
| --- | --- |
| `tenet check <project dir>` | 91,178 declarations in 2,486 modules, 0 failures, 212 s, 13,068 modules mapped |
| `Euler.euler_breakdown_R3` | 89,915 constants, axioms `propext`, `Classical.choice`, `Quot.sound` |
| `Euler.exists_compact_smooth_euler_singularity` | 94,404 constants, same three axioms |
| `NavierStokes.Comparator.navier_stokes_breakdown_R3` | 93,446 constants, same three axioms |
| `NavierStokes.Comparator.navier_stokes_breakdown_periodic` | 89,881 constants, same three axioms |

The axiom lists agree with what Lean itself printed during the build. Mathlib, which those
proofs rest on, is checked separately (the row above); this run checked the project's own
modules against it.

What the run establishes is narrow: a kernel written from the type theory rather than
translated from Lean's code follows every step of those proofs and accepts them, and no
`sorryAx` or extra axiom appears. Whether the formal statements match the informal problem
is a separate question that no kernel can answer.

## Lean versions

The tests and the tables above pin Lean 4.34.0-rc2, but the `.olean` reader and the kernel are
checked against several toolchains in CI (the `olean-compat` matrix): each job installs a
toolchain and checks its whole `Init` library in place.

| Toolchain | Declarations in `Init` | Result |
| --- | --- | --- |
| 4.34.0-rc2 | 64,814 | 0 failures |
| 4.33.1 | 64,656 | 0 failures |
| 4.28.0 | 56,236 | 0 failures |
| 4.20.0 | 38,631 | 5 not checkable, see below |

Reading Lean 4.20.0 found two things worth recording, and one real gap in the kernel.

- **The string literal constant is version dependent.** A string literal reduces to an
  application of one hardcoded constant, and that constant changed with `String`'s
  representation: `String.mk` while `String` was a structure over `List Char`,
  `String.ofList` since the UTF-8 representation. Tenet had 4.34's name hardcoded like the
  reference kernel does, so seven `rfl` proofs about string literals (`String.length_empty`
  and friends) were rejected. It now reads the name from the environment.
- **Old code generator helpers cannot be checked and are skipped.** Up to about 4.20 the old
  code generator wrote `_cstage`, `_spec_` and `_elambda` helpers into the module, and those
  reference constants such as `_neutral` that it added straight to the kernel environment
  without storing them. Lean's own `Environment.replay` never meets them because it skips
  every unsafe constant; Tenet checks unsafe constants, so it recognizes these by name the
  way Lean's `looksLikeOldCodegenName` does and reports how many it skipped (25,485 in `Init`).
- **Five declarations cannot be checked from the files at all.** `Array.eraseIdx.induct` and
  four like it mention names Lean realizes on demand (`.induct`, `.splitter`) and never
  wrote to any `.olean`. Tenet reports them as failures and says no loaded module stores the
  constant. The CI matrix therefore covers 4.28.0 and later.

## Parallel checking

`tenet check` uses all cores by default (`--jobs 1` for the sequential mode). In parallel
mode every constant is first installed unchecked, then each declaration is checked against
that environment on its own thread, with two extra rules that recover exactly the
sequential semantics: a declaration may only refer to constants that precede it in the
export, and duplicate names are reported. Inductive and quotient blocks are re-derived in
a child environment that hides the exporter's versions of their constants, then compared.
The unit tests check that parallel and sequential mode agree and that a forward reference
is rejected in both.

The speedup comes almost entirely from the .NET server garbage collector; with the
workstation collector the checker is allocation-bound and twelve threads gain nothing.

## Evidence the checks are real

A checker that accepts everything would also produce the table above, so:

- `tests/Tenet.Tests/FixtureTests.cs` tampers with a theorem's statement and with a
  recursor's computation rules and requires both to be rejected.
- `LargeExportTests.InitPreludeRejectsSwappedProofs` gives every theorem in the prelude
  the proof of the previous theorem and requires at least 95% of them to be rejected,
  with no collateral failures among untouched declarations. It passes.
- `tenet check --stats` prints kernel work counters. On `Init.Core`: 193,330 type
  inferences, 104,689 definitional-equality checks, 39,952 head reductions, 2,520
  definition unfoldings, 2,149 iota reductions, 337 `Nat` literal evaluations.
- The recursors and constructor metadata Tenet derives from types and constructors alone
  are compared field by field with Lean's for all 615 inductive blocks in `Init`,
  including the nested inductive `Lean.Syntax`.

## Agreement with Lean's kernel

`tools/leancheck` replays an export through Lean's own kernel and `tools/Tenet.DiffTest`
mutates exports and compares the two verdict sets (see `docs/testing.md`). Campaigns so far:

| Export | Variants x mutations | Agreed rejections | Disagreements |
| --- | --- | --- | --- |
| Init.Prelude (seed 1) | 40 x 12 | 8,409 | 0 after the WhnfCore fix (7 STRICT before it) |
| Init.Prelude (seed 2026) | 100 x 10 | 27,048 | 0 |
| Init.Core (seed 7) | 40 x 12 | 30,156 | 0 real; the flagged items were a harness parsing artifact and a recovery-policy difference after a broken quotient block, both fixed in the tools |
| Init.Core (seed 7, rerun with the fixed tools and two more mutation kinds) | 40 x 12 | 17,653 | 0 |
| Init.Core (seed 11) | 100 x 8 | 19,860 | 1 flagged, triaged as Lean's universe-normalization incompleteness after its sharing pass (`PULift.up.inj`; the two universes are equal); see `docs/testing.md` |

Two Lean-side observations from the same runs, reported for completeness: Lean's kernel
segfaults (exit 139) on one mutated Init.Core variant and aborted mid-line on another, both
with ill-formed constants installed unchecked; Tenet reports errors on the same files.

Results for the large exports are kept current by the `check-prelude` CI job, which also
runs a 15-variant differential test on every push.

## Implemented

- Names, levels (normalization, equivalence, `IsGeq`), expressions, substitution.
- Type inference and checking for all expression forms, including projections and literals.
- `whnf` with beta, zeta, delta, iota, projection, quotient, K-like reduction, structure eta
  for major premises, `Nat` literal arithmetic (`add sub mul div mod gcd pow beq ble land lor
  xor shiftLeft shiftRight`), and `String` literal expansion to `String.ofList`.
- Definitional equality with lazy delta reduction, proof irrelevance, eta, structure eta,
  unit-like types, and the reference's caches.
- Axioms, definitions (safe, partial, unsafe), theorems, opaques, mutual unsafe blocks.
- Inductive types: mutual, indexed, reflexive, nested; recursor generation; comparison of
  every derived field with the export.
- Quotients.
- Export reader for format 3.0 and 3.1.
- `.olean` reader (format versions 2 and 3, GMP and native big numbers, the module system's
  `.olean.private` part merged) and in-place checking with lazily decoded imports.

## Limits

- Native reduction (`Lean.reduceBool` / `Lean.reduceNat`) is rejected by design: an
  external checker cannot trust the compiler.
- Definition unfolding per declaration is bounded (`TypeChecker.MaxUnfolds`, default 100
  million) so a non-terminating unsafe definition fails with a deterministic timeout
  instead of hanging; Lean uses a heartbeat limit for the same purpose.
- Memory: the whole export is held in memory. See `docs/testing.md` for the settings that
  trade speed for footprint on very large exports.
