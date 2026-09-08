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
