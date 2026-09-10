# Changelog

All notable changes to Tenet. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- `tenet crosscheck <export.ndjson> <olean|dir>` compares what the `.olean` reader decodes
  against what Lean's own exporter wrote for the same declarations, and classifies each
  difference. Comparing verdicts with Lean's kernel cannot find a reader bug, because a reader
  that drops a hypothesis yields a weaker theorem both kernels accept; this covers that path.
  Over all 648 modules of `Init` and a Mathlib slice: 98,463 constants compared, 0 substantive
  differences.

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
  asking whether the statement itself required the axiom.
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
