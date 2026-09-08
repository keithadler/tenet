# Testing

## Unit tests

```bash
dotnet test
```

`tests/Tenet.Tests` covers universe levels, substitution, the type checker on hand-built
terms, and the two committed fixtures (`tests/fixtures`), including negative tests: a
tampered proof and a tampered recursor rule must both be rejected.

## Large exports

The real test is the Lean core library. Exports are large (all of `Init` is about 350 MB)
and are never committed; generate them:

```bash
git clone https://github.com/leanprover/lean4export
cd lean4export && lake build
mkdir -p ~/tenet-exports
lake env .lake/build/bin/lean4export Init.Prelude > ~/tenet-exports/Init.Prelude.ndjson
lake env .lake/build/bin/lean4export Init.Core    > ~/tenet-exports/Init.Core.ndjson
lake env .lake/build/bin/lean4export Init         > ~/tenet-exports/Init.ndjson
```

Then either run the checker directly:

```bash
dotnet run -c Release --project src/Tenet.Cli -- check ~/tenet-exports/Init.Prelude.ndjson
```

or let the integration tests pick them up:

```bash
TENET_EXPORTS=~/tenet-exports dotnet test
```

The tests for files that are not present are skipped silently.

## Reading a failure

```
FAIL thm Nat.add_succ (0.01s)
    declaration type mismatch for 'Nat.add_succ'
      expected: ...
      inferred: ...
```

Every failure names the declaration, the kernel's reason, and how long it took. With the
default settings the checker keeps going after a failure, installing the exporter's own
view of the failed declaration so that later ones can still be checked; use `--fail-fast`
to stop at the first one. `--only Foo.bar,Foo.baz` checks just those declarations and adds
everything else unchecked, which is the quick way to iterate on one failure.

## Memory on very large exports

The checker keeps every expression of the export in memory (later declarations refer to
earlier ones by table index, and the environment needs every constant's type and value
for unfolding). Measured on `Init` (347 MB export): 3.7 GB peak with the default server garbage
collector, 0.8 GB with the workstation collector, which is about 3.5 times slower on
twelve cores:

```bash
tenet check Mathlib.ndjson --low-memory
```

`--low-memory` relaunches the checker with the workstation collector; the equivalent by
hand is `DOTNET_gcServer=0`.

Intermediate settings keep server GC but cap its appetite:

```bash
DOTNET_GCHeapCount=4 DOTNET_GCConserveMemory=7 tenet check Mathlib.ndjson
```

## Differential testing against Lean's kernel

The strongest check on Tenet is disagreement hunting: the same export, possibly damaged,
judged by Lean's kernel and by Tenet, declaration by declaration.

`tools/leancheck` is a small Lean program that reads an export with lean4export's own
parser and replays it through `Lean.Kernel.Environment.addDeclCore`, printing `OK name` or
`FAIL name: reason` per declaration, re-deriving inductive blocks and comparing them with
the export exactly as Tenet does, and installing a failed declaration unchecked so later
ones can still be judged, again as Tenet does.

`tools/Tenet.DiffTest` produces mutated copies of an export (swapped proofs, off-by-one
de Bruijn indices, permuted universe arguments, swapped recursor rules, wrong constructor
metadata, and semantically neutral edits such as binder annotations and reducibility
heights), runs both checkers on each, and reports two kinds of disagreement:

- SOUNDNESS: Tenet accepts a declaration Lean rejects. Never acceptable.
- STRICT: Tenet rejects a declaration Lean accepts. A bug, but a recoverable one.

```bash
cd tools/leancheck && lake build && cd ../..          # needs elan; `lean` must be on PATH
dotnet build -c Release
tools/Tenet.DiffTest/bin/Release/net10.0/Tenet.DiffTest \
  --export exports/Init.Prelude.ndjson \
  --tenet src/Tenet.Cli/bin/Release/net10.0/tenet \
  --oracle tools/leancheck/.lake/build/bin/leancheck \
  --variants 40 --mutations 12 --seed 1
```

The first campaign (40 variants, 12 mutations each, on Init.Prelude) found one real
difference: Tenet compared the reduced head of an application by reference where the
reference kernel compares structurally, so a cache hit that returned an equal but distinct
object sent Tenet down a path that inferred the type of a stuck projection and rejected
seven declarations Lean accepts. After the fix, 40 variants gave 8,409 agreed rejections
and no disagreements.
