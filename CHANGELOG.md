# Changelog

All notable changes to Tenet. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed
- The `.olean` reader bounds-checks every raw read and requires stored pointers to lead to
  earlier objects, so a corrupted file can only produce an `OleanFormatException`; `tenet check`
  reports it as a file error (exit 2) instead of crashing. A corruption fuzzer (`OleanFuzzTests`,
  on the checked-in `Init.Coe` fixture) exercises this.

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
