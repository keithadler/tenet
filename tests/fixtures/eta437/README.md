# The Lean 4.37 definitional-equality rules

Lean 4.37.0-rc1 changed three things in its kernel (lean4#15103, #15371, #15373), and Tenet applies them only
to exports written by Lean 4.37 or later (`LeanVersion.KernelHasEta437`). These two exports are the evidence
that the switch does something in each direction. Both were written by Lean itself; nothing in them is edited.

| File | Written by | Under its own version | With the other version's rules |
| --- | --- | --- | --- |
| `eta437.ndjson` | Lean 4.37.0-rc1 from `Eta437.lean` | all 14 accepted | `Eta437.andMajor` and `Eta437.partialCtor` rejected |
| `acc436.ndjson` | Lean 4.36.0 from `Acc436.lean` | all 12 accepted | `Acc436.accReduces` rejected |

`Eta437Tests` rewrites the version in the first line and asserts both columns. The reference kernel gives the
same four answers: `tools/leancheck` built on Lean 4.36.0 rejects exactly those two declarations of
`eta437.ndjson` and accepts `acc436.ndjson`, and built on 4.37.0-rc1 it accepts `eta437.ndjson` and rejects
exactly `Acc436.accReduces` (run on 2026-11-19).

`Acc436.accReduces` is the direction that is easy to miss. It is `rfl` through `Acc.rec` applied to a proof
that is a constant, not a constructor application, and 4.36 unfolds the proof to find the constructor. Lean
4.37 no longer reduces a proof standing as a major premise, so the recursor is stuck and the theorem is
rejected. A checker that only added the new eta rules would go on accepting it.

`tests/fixtures/invalid/partial-constructor-eta.ndjson` is `eta437.ndjson` plus one theorem,
`Eta437.boxEta : ∀ f : Bool → Box, f = Box.mk := fun f => Eq.refl f`, which claims that every function into a
structure is its constructor. `stitch.py` builds it from the type of `Eta437.boxType`'s hypothesis and the
value of `Eta437.boxValue`, so every expression in it was written by Lean. It is rejected under both rule sets.

To regenerate, with `leanexport` from the matching toolchain:

```bash
lean -o Eta437.olean Eta437.lean      # Lean 4.37.0-rc1
LEAN_PATH=. leanexport Eta437 -- Eta437.andMajor Eta437.partialCtor Eta437.boxType Eta437.boxValue > eta437.ndjson
lean -o Acc436.olean Acc436.lean      # Lean 4.36.0
LEAN_PATH=. leanexport Acc436 -- Acc436.accReduces > acc436.ndjson
python3 stitch.py eta437.ndjson ../invalid/partial-constructor-eta.ndjson
```
