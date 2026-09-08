# Status

Updated 2026-09-08.

| Export | Declarations | Result |
| --- | --- | --- |
| `tests/fixtures/Nat.add_succ.ndjson` (format 3.1.0, Lean 4.34.0-rc2) | 7 | checks; recursor for `Nat` derived and equal to Lean's |
| `tests/fixtures/Nat.add_succ.v3.0.ndjson` (format 3.0.0, Lean 4.27.0-rc1) | 20 | checks |
| `Init.Prelude` (Lean 4.34.0-rc2) | 1,824 | 0 failures, 0.2 s |
| `Init.Core` (Lean 4.34.0-rc2) | 3,468 | 0 failures, 0.4 s |
| `Init`, the whole core library (Lean 4.34.0-rc2) | 58,135 (59,591 constants) | 0 failures, 36.9 s |

Times are check time only (parsing the 347 MB `Init` export adds about 6 s) on an Apple
M-series laptop, single-threaded, .NET 10.

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

Results for the large exports are kept current by the `check-prelude` CI job.

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

## Not implemented

- Native reduction (`Lean.reduceBool` / `Lean.reduceNat`): rejected by design.
- Parallel checking of independent declarations.
- A `.olean` reader.
