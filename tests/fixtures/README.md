# Test fixtures

Both fixtures export the theorem `Nat.add_succ` with its transitive dependencies
(the `Nat` inductive type, constructors, recursor, `Eq`, `HAdd`, and so on).

- `Nat.add_succ.ndjson`: produced by the current
  [lean4export](https://github.com/leanprover/lean4export) (format 3.1.0, Lean 4.34.0-rc2).
- `Nat.add_succ.v3.0.ndjson`: the example shipped with lean4export (format 3.0.0,
  Lean 4.27.0-rc1). It spells the inductive block's fields `inductiveVals`,
  `constructorVals`, and `recursorVals`; the reader accepts both spellings.

The exported content is derived from the Lean 4 standard library and lean4export,
both Apache License 2.0, Copyright Lean FRO and contributors.

`olean/Coe.olean` and `olean/Coe.olean.private` are the compiled `Init.Coe` module from the
Lean 4.34.0-rc2 toolchain (Apache License 2.0, Copyright Lean FRO and contributors), used by the
`.olean` reader tests and by the corruption fuzzer in `OleanFuzzTests`.

`nested/Lean.Syntax.ndjson` is `Lean.Syntax` with its dependencies (273 declarations), exported by
[leanout](https://github.com/keithadler/leanout) from Lean 4.36.0 (`leanout Init -- Lean.Syntax`). `Lean.Syntax`
is a nested inductive (it holds an `Array Syntax`), which the kernel checks through auxiliary types in a separate
environment; it is here so the trace tests cover that path.

Larger exports (`Init.Prelude`, `Init.Core`, all of `Init`) are generated, never
committed. Set `TENET_EXPORTS` to a directory containing them to run the
integration tests; see `docs/testing.md`.
