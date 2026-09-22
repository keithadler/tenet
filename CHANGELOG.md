# Changelog

All notable changes to Tenet. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/).

## [0.11.1] - 2026-09-21

### Fixed
- **Checking a project directory could reject sound declarations.** Every module is mapped into one
  environment and each constant was resolved by name alone, first module wins. Two modules may declare the
  same name when neither imports the other, which is ordinary in any project with more than one executable:
  [verso](https://github.com/leanprover/verso) has eight top-level `Config` structures. The second
  declaration was lost, references to it resolved to the first, and the kernel then compared a term against a
  type from an unrelated program and reported a type mismatch that was not there. Verso produced 33 such
  rejections against declarations Lean had just compiled.

  A name now means what the module under check can see: its own declaration first, otherwise whichever
  declaring module is in its import closure, and nothing if the module imports none of them. Only names
  declared by more than one module are tracked, so an unambiguous project is unaffected, measured identical on
  Lean's core library at 64,675 declarations in 654 modules before and after.

  Checking an export is unaffected: an export is one flat environment with no two declarations sharing a name,
  and it goes through a different reader entirely. Arena results do not change.

## [0.11.0] - 2026-09-20

Tenet joined the [Lean Kernel Arena](https://arena.lean-lang.org), which benchmarks proof checkers for Lean.
It scores 70 of 70 on the must-reject corpus and 127 of 127 on the must-accept one, tied with the best there,
and is the only entry on .NET.

### Added
- **The arena submission**, in `packaging/arena/`, kept in this repo so the exit-code mapping gets reviewed
  when the CLI's exit codes change. A CI job downloads the arena's own published test suite on every push,
  runs all 189 files through a self-contained binary with `env -i`, and fails on anything short of a perfect
  score, so a regression goes red here rather than on somebody else's dashboard. `threads: 4` is declared and
  matched with `--jobs 4`: the runner reserves that many CPU slots, and a parallel checker that does not say so
  takes CPU from whatever runs beside it and skews other people's published numbers.
- `tenet check` can **decline** a file instead of rejecting it: exit 4, printing `DECLINED`, when every failure
  is a refusal to vouch rather than a finding that something is wrong. Refusing to believe the output of
  compiled code (`Lean.reduceBool`, `Lean.reduceNat`) is the case that matters, and reporting it as a rejection
  claimed the proof was invalid, which Tenet was never in a position to say. The negative corpus records which
  outcome each case expects and the test fails if either collapses into the other.
- `OleanModule.DeprecationOf` reads `@[deprecated]`: the replacement name, the note and the version, which is
  what a reader needs before acting on a lemma Mathlib has moved on from.
- `OleanModule.KeysInExtension` answers which declarations an environment extension has an entry for, without
  decoding the payload, which is how to ask whether a declaration is protected, noncomputable, a class, or
  carries any other name-keyed attribute. An extension whose entries are not name-keyed reports none rather
  than guessing.

### Fixed
- **The native AOT publish had been failing since it was introduced**, and the release workflow falls back to a
  self-contained build when it does, silently. Two releases shipped 32 MB binaries while the README promised
  4 MB ones. `Trim="true"` on the DATAS runtime option makes it a feature switch, which ILC rejects outright;
  the option is now set without it for AOT builds. Measured after: 4.85 MB and 10 ms to start.
- The substitution memo in `ExprOps.Replace` was a fresh `Dictionary` on each of 21 million calls. It is rented
  from a per-thread pool now: **42% less allocation** on `Init`, 54.6 GB to 31.5 GB, and 37% less collector
  pause. Worth about 1% of wall time, measured on a quiet machine with interleaved pairs, and kept for the
  allocation rather than the speed.
- The GitHub Action and the sample point at the published release. Both have to trail the repo's version until
  a release actually exists, and bumping them inside the release commit made CI download a release that commit
  was about to create.

### Documented
- **Where the time goes**, by counting rather than timing: 252M expression nodes built per `Init` check, 187M of
  them applications, 143M of those from `Replace` rebuilding terms around substituted variables. Not the
  garbage collector, whose allocation and pause are identical at one worker, four and twelve.
- **Why the fast checkers are fast.** `sokonanoda` does Mathlib in a fourteenth of the instructions, and its
  conversion checking uses closures rather than substitution. That is a different evaluator, not a tuned one,
  and adopting it would put at risk the claim this project rests on, since Lean's definitional equality is
  incomplete on purpose and which pairs get decided depends on the reduction strategy. **Speed is deliberately
  not a goal**; the measurements are kept so the question is not reopened from scratch.
- **Why Mathlib costs 1.38x the reference in instructions** where every other corpus is within 8%: 17% of it is
  deriving every recursor and comparing it field by field with what the exporter wrote, which Lean's replay does
  not attempt. It stays.
- **What this says about Lean**: every bug this project has found has been in Tenet, and across all of Mathlib,
  140,000 damaged declarations, 166,048 constants compared against Lean's exporter and six toolchains, no bug
  has been found in Lean's kernel. With its bounds stated, because an independent implementation catches
  defects in the reference's code and not in its design.
- **What kind each divergence is.** Three of six are threat model rather than theory: Lean ships its own prelude
  so it can safely decide things from a name that Tenet cannot, and in each of those Tenet ends up closer to the
  type theory than Lean by doing work Lean can correctly skip.
- An audit of all of Mathlib: 490,619 declarations, 100% resting on nothing beyond `propext`, `Classical.choice`
  and `Quot.sound`, and 39 on `lcProof`, which is the compiler's stub for erasing proofs from `unsafe`
  definitions. Mathlib's CI already guards this with `lean4checker`, so this confirms independently rather than
  discovering.

## [0.10.0] - 2026-09-18

The Lean Kernel Arena found a soundness bug in the first five minutes, and it is in every release
before this one. **If you are running 0.9.0 or earlier, upgrade.**

### Fixed
- **A theorem proved by itself was accepted.** `theorem selfProof : ∀ (p : Prop), p := selfProof`
  instantiated at `False` is a proof of `False`, and Tenet reported it `OK`, one declaration checked,
  no axioms. Both the parallel and the streaming paths add every declaration to the environment up
  front so workers can check in any order, and the ordering guard that makes that safe rejected only
  constants declared *later*, never a declaration naming itself. Self-reference is legitimate for an
  inductive block, a mutual definition block and the quotient block, and is refused everywhere else.
  Found by test `bad/tutorial/014_selfProof` of the [Lean Kernel Arena](https://arena.lean-lang.org).
  Not reachable by mutation, not in the negative corpus, and invisible to agreement with Lean or
  con-leche on Mathlib, because no real export contains a declaration that names itself.
- The reader required index numbers to be dense and in ascending order. The format requires neither;
  every real exporter happens to do both, which is how a reader that insisted on it passed all of
  Mathlib. Gaps are now holes. Referring to a hole is still an error, and so is defining an index
  twice. A test asserting the old behavior described what the reader did rather than what the format
  says, and is replaced by three that assert what is actually required.
- The GitHub Action installed 0.7.0 by default, two releases of soundness fixes behind.
- `check --rules` printed nothing on an export; it was wired only into the `.olean` path.
- `Rule.InferBVar` was counted on a switch arm the loose-bound-variable guard makes unreachable.
- The nightly proved-checker step piped both checkers through `tee`, so neither exit code reached the
  step and a rejection would have reported green.

### Added
- **A negative corpus**, `tests/fixtures/invalid`, of nine well-formed exports that must be rejected:
  `Type : Type`, a non-positive inductive, a `Prop` eliminating into `Sort u`, a swapped proof, a
  squatted `_nested`, a claim laundered through `Lean.reduceBool`, and all three soundness bugs ever
  found in this checker. Checking Mathlib with zero failures is a claim about agreement, not about
  soundness: a kernel whose check returns `true` reports zero failures too, faster. Cases targeting an
  optional defense are also asserted to be **accepted in full when it is switched off**, so the corpus
  cannot drift into files rejected for being malformed.
- **Tenet checks con-leche's own soundness proof.** That checker carries a machine-checked theorem
  that it never accepts a file declaring `False`, and until now the only thing that had checked that
  proof was Lean's kernel, which is what con-leche exists to double-check. 232,881 declarations across
  2,791 modules, 0 failures, and `no_False_declaration` independently reported as resting on
  `propext`, `Classical.choice` and `Quot.sound` alone. It does not remove Lean, since this is a Lean
  proof checked by a kernel calibrated against Lean. It removes the shared implementation.
- `TrustSurfaceTests` reads the names the kernel hardcodes out of its own source and fails unless each
  is accounted for, mapped to the case that attacks it. Writing it found `eagerReduce` unattacked.
- `Trust.Names`, and `tenet audit` reporting which of those names a project defines itself.
- `check EXPORT --names-out FILE`, so coverage across slices can be counted as a union.
- Rule coverage from the nightly Mathlib run: 36 of 39 reachable rules, 1.28 billion firings. All of
  Mathlib reaches no rule that `Init` misses.
- `net8.0` alongside `net10.0`, so a project that has not moved off .NET 8 can use the libraries.
- Badges, and a line saying which one to read.

### Changed
- Coverage is reported as 36 of 39 reachable rules, not 36 of 40. `DefEqFVar` cannot be reached by any
  input, because the syntactic check decides every pair that would satisfy it, exactly as in Lean's
  `is_def_eq_core`. Subsumed rules are listed separately from cold ones.
- The nightly rotates through seven Mathlib slices instead of re-proving one forever.

## [0.9.0] - 2026-09-18

Two ways a file could have talked this kernel into accepting a proof of `False`, both closed.
Neither was reachable by mutation testing; both came from reading what another checker had
already written down about itself.

### Fixed
- A numeric literal was given the type `Nat` by assertion, without checking that the `Nat` in the
  environment was the inductive the literal denotes. An export declaring `def Nat : Prop := False`
  and then `def boom : False := 3` was accepted, with an empty axiom list, and `tenet audit` called
  it unconditional. A literal is now refused unless the environment's `Nat` and `String` are what
  the literal means. This is the most serious thing this project has found in itself.
- An accelerated primitive was trusted from its name alone, which is safe for Lean because Lean
  ships its prelude and is not safe for a checker whose entire job is reading somebody else's file.
  An export declaring `Nat.add := fun a b => a` made Tenet accept `2 + 2 = 4`, false of the
  declaration in front of it, and reject `2 + 2 = 2`, true of it. Each of the sixteen shortcuts is
  now checked before it is taken: eight against their defining equations over free variables, the
  rest at sampled values. A constant that fails is unfolded rather than rejected, so an unusual but
  honest prelude is checked slowly instead of refused.
- The `_nested` namespace, which eliminating a nested inductive derives auxiliary types into, is
  reserved against unrelated declarations rather than only checked on constructor types.
- `Nat.ble`'s equations held only on the newest Lean, because its zero clauses match on both
  arguments. Three toolchain compatibility jobs hung before this was caught.
- The differential harness attributed the primitive divergence in one direction only. Both occur:
  usually Tenet is stricter, but on a damaged `Nat.pow` it is the laxer one, which is the alarming
  direction and the same cause.
- A stack-depth test was asserting against a legal JIT optimization rather than against the guard,
  and failed about one run in thirteen.

### Added
- `net8.0` alongside `net10.0`. The packages carry both, so a project that has not moved off .NET 8
  can reference the libraries. Both builds pass the full suite and check all of `Init` identically.
  .NET 10 is the faster of the two and the one the standalone binaries are built from.
- Agreement with `leanchecker`, which carries a machine-checked consistency proof, over five Mathlib
  slices and 312,904 distinct declarations, both accepting in full with counts matching exactly. A
  nightly job keeps putting a slice to it.
- `docs/specification.md`: the forty rules this kernel implements, each with its typing judgment,
  the method that implements it, and the function in Lean's C++ kernel it corresponds to. A test
  fails if a rule has no row or a row has no rule, so the correspondence can be checked rather than
  believed.
- `docs/divergences.md`: every place Tenet decides something differently from Lean, why, and how to
  switch it off. Anything not listed there is a bug.
- Hostile tests organized by what the checker takes on trust. Each asserts the attack is refused and
  that it succeeds with the defense turned off, so a test cannot pass by testing nothing.
- Complete level equality by case analysis on which parameters can be zero, off by default. It can
  only accept more, never less, and the gap it closes cannot arise on a real export.
- `samples/Tenet.Explorer`: a hundred lines that read Lean from C# with no Lean installed, built
  against the published packages rather than by project reference, so CI checks that what is on
  nuget.org is still usable.

## [0.8.0] - 2026-09-17

Published to nuget.org, and made harder to crash than to answer wrongly.

### Added
- Packages on nuget.org, published by Trusted Publishing: the job asks GitHub for a short-lived
  OIDC token and nuget.org validates it against a policy naming this repository and workflow. There
  is no stored key to leak or rotate.
- `tenet names`: find out what the declarations in a project are called, which every other command
  needs as its input and none of them could answer.
- Declarations resolve by name against a whole project rather than one `.olean` file.
- A catalog of the kernel's forty rules, with a counter at every rule site, so a green run reports
  which rules it exercised instead of only that it passed.
- `difftest --oracle2` asks con-leche who is alone when Tenet and Lean disagree.
- A nightly three-way differential run.
- `readercheck`: damage an export in each of the ways a reader defect would, and assert `crosscheck`
  notices. It previously could not see the defects it exists to catch.

### Fixed
- A term deeper than the stack is rejected instead of aborting the process. A .NET stack overflow
  cannot be caught, so without the guard one pathological declaration takes down every other
  declaration being checked alongside it. The first pass guarded five call sites; an audit found
  eight more.
- The differential harness scored a checker that crashed as a checker that agreed, because a missing
  report read as an empty failure list.
- The rule catalog was missing four rules, so its coverage read as 33 of 36 when it was 36 of 40.
- The edge-case corpus was written against what the checks were meant to do rather than what they
  measurably did.
- `readercheck` in CI ran the oracle's toolchain against the exporter's output, producing 58 spurious
  differences. Its own baseline guard is what caught it.

## [0.7.0] - 2026-09-15

Checked against a checker with a consistency proof, and made to answer to programs as well as
people.

### Fixed
- The GC's adaptive heap sizing (DATAS) is pinned off. It costs this workload about 30% at
  twelve threads, and .NET 11 turns it on by default where .NET 10 left it off, so a runtime
  upgrade alone would have changed throughput by a third. Both runtimes now perform the same.
- `AnalysisLevel` is pinned rather than `latest`: with warnings as errors, a newly released SDK
  could add style rules and break the build with no code change, which the .NET 11 RC did.

### Added
- `tenet check --sarif FILE` writes findings in SARIF, so GitHub code scanning renders a
  rejection on the pull request diff rather than in a log. `--fail-on-axiom` contributes
  findings too, which is the more useful case: a `sorry` that reached the branch shows up
  where someone will see it.
- `tools/bench`, a committed benchmark and a comparison script that exits non-zero on a
  regression. Performance was previously only visible to whoever timed it by hand.
- `tenet check --timing` prints where the time went: mapping, decoding, kernel, and worker
  utilization. On `Init` it shows the same work costing 24.2 s of kernel time on one job and
  56.1 s summed across twelve, which is the parallel contention measured rather than estimated.
- Shell completions for bash and zsh in `completions/`, and a Homebrew formula.
- `tenet <command> --help` answers about that command alone. The full usage block lists nine
  commands and had stopped being something anyone reads to find one flag.
- A determinism test: the same check run sequentially and in parallel, twice, must give the
  same answer. Workers share decode caches, a resolver and an environment, so a race would
  show up as a verdict that depends on thread timing, and nothing else would catch it because
  every large run reports zero failures whichever way a race fell.
- `tenet check --fail-on-axiom NAME` exits non-zero if anything checked rests on that axiom.
  Checking says the proofs are valid; this says they are valid without leaning on something the
  project has decided not to lean on, which is the gate a formalization wants in CI.
- Reports record the SHA-256 of every artifact checked. A verdict is only reproducible if you
  can tell whether the inputs were the same files.
- Standalone native binaries on each release: no .NET installed, 4 MB, and about 7 ms to start
  instead of 27. Reports are now written by a hand-rolled emitter rather than a reflecting
  serializer, which is what made an ahead-of-time build possible and also fixes the field order.
- `.olean` format 1 (Lean up to about 4.12) is read, and the pre-module-system `ModuleData`
  layout no longer overruns the mapping. CI now covers six toolchains from 4.12 to 4.34.
- `--json` on `axioms`, `audit`, `compare`, `crosscheck`, `statement` and `why`. Every command that answers a
  question worth acting on can now answer it to a program; parsing prose was not an interface.
- `--names-out` on `crosscheck`, writing every constant compared, so coverage across several
  export slices can be unioned rather than summed: two Mathlib exports share most of a closure,
  and adding their counts overstates coverage badly.
- A nightly run against Mathlib master, since the `.olean` reader depends on Lean's compiled
  object layout, which is not a stable interface, and per-push CI only checks `Init`.
- A verdict regression test over the edge-case corpus. The large runs all report zero failures
  and would keep reporting it whichever answer were wrong, so nothing previously noticed if a
  refactor quietly changed how structure eta, proof irrelevance, K-like reduction, quotient
  reduction or literal arithmetic were decided.

## [0.6.0] - 2026-09-11

Two questions that a kernel alone cannot answer: is my reader decoding what Lean actually
stored, and are two projects stating the same theorem.

### Added
- `tenet compare A.olean nameA B.olean nameB` asks whether two separately built projects state
  the same theorem, by definitional equality rather than by eye. A kernel never asks whether a
  statement is the intended one; the case a machine can settle is when somebody has written the
  statement independently, and then the question is whether the two agree. Names that carry
  different content in the two projects are reported rather than silently resolved, since a
  shared name meaning two things is how two statements look alike and differ.
- `tenet crosscheck <export.ndjson> <olean|dir>` compares what the `.olean` reader decodes
  against what Lean's own exporter wrote for the same declarations, and classifies each
  difference. Comparing verdicts with Lean's kernel cannot find a reader bug, because a reader
  that drops a hypothesis yields a weaker theorem both kernels accept; this covers that path.
  Over all 648 modules of `Init` and a Mathlib slice: 98,463 constants compared, 0 substantive
  differences.
- The differential harness takes `--kinds` and `--list-kinds`, and carries eleven mutation
  kinds aimed at one kernel feature at a time: universe level normalization, literal
  arithmetic at 0 / 2^31 / 2^63 / 2^64 / 2^128, nested inductive metadata, and recursor and
  constructor arity. Kinds whose result is definitionally equal to the original are marked,
  because those are the ones that find completeness gaps rather than obvious damage.
- `tools/edgecases`, a Lean corpus dense in structure eta, proof irrelevance, K-like reduction,
  quotient reduction, nested and mutual inductives, literal boundaries and universe
  polymorphism, for use as a mutation target. `--tail N` focuses mutation on it.

## [0.5.0] - 2026-09-10

Four questions a kernel can answer about a finished build, beyond "does it check": which
axioms a theorem rests on, which lemma brought each one in, what a statement is built from
and who wrote those definitions, and how much of a whole project stands unconditionally.
Demonstrated on OpenAI's NavierStokesAndEuler and on the FLT project.

### Added
- `tenet why FILE.olean NAME` shows a shortest chain from a declaration to each assumption it
  rests on, naming the module at every step. An axiom list says what a theorem depends on;
  the chain says which lemma brought the dependency in.
  [gonzalgo](https://github.com/vince-gonzalez/gonzalgo) did this first and goes further,
  measuring how far an axiom reaches and flagging theorems that may not need it.
- `tenet statement FILE.olean NAME...` lists the constants a theorem's statement is built
  from, split into those the project under test defines and those coming from established
  libraries. A kernel cannot tell whether a statement means what its prose claims, and the
  usual way that goes wrong is a definition written for the occasion; this says where to
  look. It does not decide whether the statement is right.
- `tenet audit <project dir>` separates the declarations a project has proved outright from
  those resting on an assumption, counts every axiom beyond `propext`, `Classical.choice` and
  `Quot.sound` (not just `sorryAx`, since a named axiom is just as conditional and less
  obvious), and names the declarations that introduce each hole rather than the larger set
  that merely inherits one.

### Fixed
- Four lookups used `Find` where Lean's kernel uses `get`: the structure test behind eta for
  structures and K-like reduction (`is_non_rec_structure`), the first-constructor lookup
  (`get_first_cnstr`), projection reduction (`reduce_proj_core`) and the eta-struct head
  (`try_eta_struct_core`). Lean rejects an unknown name there with "unknown constant"; Tenet
  answered "not a structure" and went on, and could accept a declaration Lean rejects. Only
  reachable when a declaration was installed unchecked, which the differential harness does
  after a failure: seed 59, variant 013 renamed `Eq.refl`'s type to a bare `refl`, and twenty
  `noConfusion` declarations were accepted that Lean rejected.

## [0.4.0] - 2026-09-08

Checked against several Lean toolchains, and used to re-check a published formalization:
OpenAI's NavierStokesAndEuler. The whole import closure re-checked in one pass, 850,211
declarations in 13,068 modules with 0 failures, and its four headline theorems depend on
`propext`, `Classical.choice` and `Quot.sound` only.

### Added
- CI checks the `.olean` reader and the kernel against several Lean toolchains, not only the
  pinned one: each matrix job installs a toolchain and checks its whole `Init` library in place.

### Fixed
- The constant a string literal reduces to is read from the environment (`String.ofList`, or
  `String.mk` for a Lean built before the UTF-8 `String`) instead of being hardcoded, so
  `rfl` proofs about string literals check on older toolchains too. Found by checking Lean 4.20.

### Changed
- Helpers of Lean's old code generator (`_cstage`, `_spec_`, `_elambda`, gone since about Lean
  4.20) are skipped and counted rather than reported as failures: they reference constants the
  generator never stored, so no kernel can check them from module data. Lean's own replay never
  meets them because it skips every unsafe constant.
- An `unknown constant` failure says so when no loaded module stores the constant.
- `tenet show FILE.olean NAME...` prints declarations from a compiled module and its imports.
- `tenet axioms FILE NAME...` prints the axioms a declaration depends on, transitively, the way
  Lean's `#print axioms` does, so a proof can be shown to rest on nothing but `propext`,
  `Classical.choice` and `Quot.sound`, with no `sorryAx`. Works on exports and `.olean` files.

## [0.3.1] - 2026-09-08

Hardening and adoption: a corruption-proof `.olean` reader, the classic attacks as tests, one
command to check a built Lake project, and a GitHub Action.

### Changed
- The `.olean` reader bounds-checks every raw read and requires stored pointers to lead to
  earlier objects, so a corrupted file can only produce an `OleanFormatException`; `tenet check`
  reports it as a file error (exit 2) instead of crashing. A corruption fuzzer (`OleanFuzzTests`,
  on the checked-in `Init.Coe` fixture) exercises this.
- `tenet check FILE.olean --report out.json` writes the outcome as JSON, as the export mode does.
- A GitHub Action (`uses: keithadler/tenet@main`) that installs the released tool and checks a built
  Lake project, with a job summary; CI runs it on `tools/leancheck`.
- `tenet check <project dir>` checks every module a Lake project has built (its `.lake/build/lib/lean`),
  so a project's CI can run one command.
- A declaration that hits the unfolding limit (`DeterministicTimeoutException`) is reported
  once and not re-checked in the faithful mode, which would only repeat the work.
- `AttackTests`: the classic derivations of `False` (non-positive inductives, Girard's
  paradox, large elimination from Prop, and so on) as declarations the kernel must reject.
- `Tenet.DiffTest --timeout SECONDS` (default 1800) kills a checker run that does not finish,
  since Lean's kernel has no unfolding limit and a mutation can send it into a very long reduction.

## [0.3.0] - 2026-09-08

Failed definitional-equality checks are cached, with a faithful re-check on rejection. The
slowest Mathlib declarations are now faster in Tenet than in Lean's own kernel, measured
declaration by declaration on the same exports.

### Changed
- Failed definitional-equality checks are cached for the whole declaration, not only
  inside lazy delta reduction. The slowest Mathlib declarations were repeating the same
  failing comparison hundreds of times; `PresheafOfModules.freeObj._proof_2` went from
  9.3 s to under 0.5 s (Lean's own kernel: 0.23 s), all of Mathlib from 6.5 to 6.1 minutes. A rejected
  declaration is re-checked with the cache off so verdicts stay identical to the
  reference kernel's; `TENET_NO_FAILURE_CACHE=1` turns the cache off. See docs/design.md.
- A rejected declaration no longer leaves partially added constants (a mutual block
  whose second member fails) in the environment.
- `tenet check --stats` also prints the most often unfolded definitions, like Lean's
  `[kernel] unfolded declarations` diagnostics, and how many declarations needed the
  faithful re-check; `--only NAME` works for `.olean` files; `--verbose` names each
  declaration before checking it; `TENET_MAX_UNFOLDS` overrides the unfolding limit.

### Fixed
- `Name.Parse` (and so `--only`) reads all-digit components as numeric, so private names such as
  `_private.Mathlib.Foo.0.bar` round-trip.
- `RecursorInfo.GetMajorInduct` walks lambdas as well as pis, as the reference does; found
  by the CI differential campaign (Tenet rejected `Substring.Raw.noConfusion` on a mutated
  export that Lean accepts).
- Error messages print expressions with a bound (`ExprPrinter.MaxLength`). Terms are shared
  graphs, and printing one as a tree could take memory exponential in its size; a rejection
  in Mathlib's `AlgebraicGeometry.isIso_pushoutSection_of_iSup_eq` ran the process out of
  memory while formatting the message.

## [0.2.0] - 2026-09-08

All of Mathlib and its dependencies (765,497 declarations, 10,726 modules) checked in place
from `.olean` files in 6.5 minutes with zero failures.

### Added
- `Tenet.Olean`: reads Lean's compiled `.olean` files directly (memory-mapped, constants
  decoded on demand, the module system's private part merged). `tenet check Foo.olean`
  checks a module in place; `--all` checks the import closure; `tenet info Foo.olean`.
- `partial` and `unsafe` definitions are checked, as mutual blocks under their own safety.
- `Environment.SetResolver` for constants that live outside the environment.
- `tenet show FILE NAME...` prints declarations from an export.
- `tenet check --low-memory` relaunches with the workstation garbage collector (about a
  third of the memory, 3 to 4 times slower).
- `TypeChecker.MaxUnfolds`: a deterministic timeout on definition unfolding per
  declaration, so a non-terminating unsafe definition fails instead of hanging.
- Tests for K-like reduction, structure eta, proof irrelevance, literal arithmetic,
  unsafe/partial rules, mutual blocks, opaque values, reader robustness, and deep terms.

### Changed
- Checking streams: the export is parsed on one thread while declarations are checked on
  dedicated large-stack worker threads. All of `Init` takes about 7 s wall clock.
- The reader parses table lines with `Utf8JsonReader` (about 145 MB/s).
- Expression metadata is packed into one word; constants' universe arrays are interned.

### Fixed
- `WhnfCore` compared the reduced head of an application by reference instead of
  structurally; with a cache hit this could reject declarations Lean accepts. Found by
  differential testing against Lean's kernel (`tools/leancheck`, `tools/Tenet.DiffTest`).
- Negative `bvar` and `proj` indices are kernel errors rather than crashes.
- A truncated or malformed export is checked up to the problem and reported as INCOMPLETE
  (exit status 3) instead of aborting with a parse error.

## [0.1.0] - 2026-09-08

First release.

- Kernel: names, universe levels with normalization, locally nameless expressions,
  environments, type inference and checking, weak head normalization, definitional
  equality with lazy delta reduction, proof irrelevance, eta for functions and structures,
  K-like reduction, `Nat` and `String` literals, inductive types (mutual, indexed,
  reflexive, nested) with recursor derivation, quotients.
- Export reader for lean4export format 3.0 and 3.1.
- `tenet check` with parallel checking (`--jobs`), `tenet info`.
- Checks all of `Init` (58,135 declarations) and Mathlib up to `Mathlib.Data.Real.Basic`
  (179,215 declarations) with zero failures.

[0.2.0]: https://github.com/keithadler/tenet/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/keithadler/tenet/releases/tag/v0.1.0
