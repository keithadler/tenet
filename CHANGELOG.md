# Changelog

All notable changes to Tenet. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/keithadler/tenet/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/keithadler/tenet/releases/tag/v0.1.0
