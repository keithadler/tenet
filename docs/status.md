# Status

Updated 2026-09-08.

| Export | Declarations | Result |
| --- | --- | --- |
| `tests/fixtures/Nat.add_succ.ndjson` (format 3.1.0, Lean 4.34.0-rc2) | 7 | checks; recursor for `Nat` derived and equal to Lean's |
| `tests/fixtures/Nat.add_succ.v3.0.ndjson` (format 3.0.0, Lean 4.27.0-rc1) | 20 | checks |
| `Init.Prelude` | see below | see below |
| `Init.Core` | see below | see below |
| `Init` (all) | see below | see below |

Results for the large exports are recorded in this file as they are obtained and kept
current by the `check-prelude` CI job.

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
