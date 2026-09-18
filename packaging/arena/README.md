# Lean Kernel Arena

<https://arena.lean-lang.org> benchmarks Lean proof checkers. `tenet.yaml` is the checker definition
to contribute to <https://github.com/leanprover/lean-kernel-arena> under `checkers/`.

Kept here so it is version controlled alongside the thing it describes, and so the exit-code mapping
is reviewed when the CLI's exit codes change.

## Status against the published test suite

Downloaded from <https://arena.lean-lang.org/lean-arena-tests.tar.gz>, which excludes the large
real-world corpora:

| | result |
| --- | --- |
| `bad/`, must reject | **70 / 70** |
| `good/`, must accept | **119 / 119** |

It was 69 and 117 the first time it was run. Test `bad/tutorial/014_selfProof` found a real soundness
bug: a theorem proved by itself was accepted, and `∀ (p : Prop), p` proved that way is a proof of
`False`. The two on the accept side were a reader that required index numbers to be dense and in
order, which the format does not.

## Declaring threads, which is a fair-play matter

The arena's `threads` field defaults to **1**, and its parallel runner reserves that many CPU slots so
checkers do not oversubscribe each other. Tenet runs one worker per core by default. Submitted without
declaring it, Tenet would take twelve cores while the harness budgeted one, stealing CPU from whatever
ran alongside and corrupting the published timings of checkers that did nothing wrong.

`threads: 4`, with a matching `--jobs 4` in the run line. Four is measured, not guessed: on all of `Init`
wall time is 30.1s at one worker, 13.3s at four and 11.0s at twelve, while summed kernel time across
workers rises from 26s to 65s. Past four the extra CPU buys almost nothing, so taking it would be both
unfair and pointless. `con-leche` declares 4 and `eink0rn` 8, so this is the established convention.

## What has been verified, and what has not

Verified:

| | |
| --- | --- |
| published suite, `--jobs 4` | **70 / 70** reject, **119 / 119** accept |
| against `schemas/checker.json` | validates |
| build in an environment with no .NET | CI, every push |
| binary needs nothing installed | run under `env -i` |
| `mathlib` (`.olean`) | 767,307 declarations, 0 failures |
| `cslib` (`.olean`) | 431,294 declarations, 0 failures |
| `con-leche` (`.olean`) | 232,881 declarations, 0 failures |
| `Init` (`.olean`) | 64,814 declarations, 0 failures |

Not verified, and so not claimed:

- **`cedar`.** Never built here.
- **Timing and memory on the arena's hardware.** Every number above is one laptop.
~~The large corpora through the NDJSON reader.~~ **Closed.** All of Mathlib was exported with
`lean4export` and put through the reader the arena actually feeds:

| | |
| --- | --- |
| export | 4.8 GB, 91,571,400 lines |
| result | **611,878 declarations, 0 failed** |
| time | 652s at `--jobs 4`, one laptop |
| peak resident | **5.7 GB** |

The memory is the number that was worth finding out, because a 17 GB laptop says nothing about an 8 GB
runner. 5.7 GB is lower than this checker's own `.olean` path uses and sits inside the range already on
the board, where the official kernel is 7.6 GB and lean4lean 9.3 GB.

## Two things to settle before submitting

**The arena's environment has no .NET.** Its nix flake provides elan, rustc, node, ocaml, zig, ghc
and pypy, and nothing for .NET, so the build line uses `nix develop path:.` to bring Tenet's own, the
way `lean4cobol` does. That flake now exists at the repo root and exists only for this.

It uses `dotnetCorePackages.sdk_10_0`, not the .NET 8 SDK. Targeting `net8.0` looked easier, since
Tenet multi-targets and nixpkgs has an 8 SDK, but `global.json` pins the SDK to 10.0.1xx with
`rollForward: latestFeature`, so an older SDK does not build slowly, it refuses to start.

`--self-contained true -r linux-x64` is load-bearing, and finding out why is the reason to read
`lka.py` rather than guess. It runs `build` through the nix shell and `run` with a plain copy of the
environment, so a framework-dependent publish would have died on the very first test with "You must
install .NET" and looked like a broken checker rather than a missing flag.

The `arena` job in CI downloads the arena's own published test suite on every push, runs all 189
files through the binary with `env -i`, applies the same exit-code mapping, and fails on anything
short of a perfect score. Running with an empty environment is what proves the binary needs nothing
installed. There is no nix on the development machine this was written on, so CI is the only thing
that has ever executed the flake.

**Declining.** Done. The arena reserves exit 2 for "cannot handle this proof", distinct from
rejecting it. Tenet refuses `Lean.reduceBool` and `Lean.reduceNat` rather than trusting compiled
code, and used to report that as a failed declaration, which reads as a claim the proof is invalid.
It is not; it is a claim that this checker will not vouch for it.

`tenet check` now exits **4** and prints `DECLINED` when every failure is of that kind, and the run
line maps 4 to the arena's 2. The negative corpus records which outcome each case expects, and the
test fails if a rejection comes back as a decline or the other way round, so the distinction cannot
quietly collapse in either direction.
