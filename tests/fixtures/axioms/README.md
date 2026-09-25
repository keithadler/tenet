# Axiom-collection fixtures

What `tenet axioms`, `tenet why` and `tenet audit` all rest on is one walk: from a declaration to the
assumptions underneath it. Every way of being wrong in that walk that matters is a way of reporting
too few. Over-reporting is an annoyance. Under-reporting says a proof resting on `sorry` is complete,
which is the one claim these commands exist to make.

Two under-reports were live in that walk and no test caught either, because a missed edge and a clean
proof look identical from outside. An axiom reachable only through a constructor's field type was
invisible, so a structure whose field rested on `sorry` came back with no axioms at all. And reaching
an axiom ended the walk, so an axiom stated in terms of another one hid the second.

`Axioms.lean` is one declaration per shape. `compare.sh` builds it with the pinned toolchain, reads
what Lean's `#print axioms` says about each name, asks Tenet the same question about the compiled
module, and fails on any disagreement. The expectations written into `AxiomCollectionTests` are
transcriptions of a run of this; the script is what keeps the transcription honest.

## The one disagreement

`Minimal.lean` and `MinimalImporter.lean` are the reproduction from
[leanprover/lean4#15226](https://github.com/leanprover/lean4/issues/15226), unchanged. Lean answers
`#print axioms S9` one way from the module that defines `S9` and another way from a module that
imports it. Lean's maintainers call it an inconsistency rather than a wrong answer, since whether an
inductive rests on its constructors' axioms is a convention; the cause is that `collectAxioms` caches one axiom set per constant, and the sentinel it inserts to break
the inductive/constructor cycle cannot be told apart from a finished result, so the inductive is
cached with whatever had been collected when the constructor's walk reached it.

Tenet keeps one accumulator for the whole walk and caches nothing per constant, so the module a
declaration is read from cannot change its answer. `compare.sh` asserts that Tenet says
`Classical.choice` in both modules and that Lean still says it in only one. If Lean fixes the issue,
the script says so and asks for the section to be folded into the main loop.

The reproduction is kept in its own module rather than folded into `Axioms.lean` because the bug
turns on the order a module's constants are walked in during export. `Axioms.lean` has the same
shape and Lean gives the same answer from both modules. Keeping the issue's own file unchanged
is what makes it reproduce.

## Running it

```
dotnet publish src/Tenet.Cli -c Release -f net10.0 -o axiom-bin
./tests/fixtures/axioms/compare.sh "$PWD/axiom-bin/tenet"
```

It needs `lean` on the path; `lean-toolchain` pins the version the expectations were recorded with, so
elan will fetch it. CI runs it in the `check-prelude` job, which already has a toolchain.
