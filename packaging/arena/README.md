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

## Heap count, measured and then withdrawn

`threads: 4` tells the harness how many CPU slots to reserve and tells the .NET collector nothing.
Server GC sizes itself from the machine's core count, so on the arena's eight-core runner it builds
eight heaps, each holding its own gen0 headroom, while only four workers ever run. Setting
`DOTNET_GCHeapCount=4` on the run line fixes that, and on the corpora it looks free. Measured on the
`Lean` export, 164,133 declarations, `--jobs 4` throughout, three runs each:

| | instructions | peak resident |
| --- | --- | --- |
| eight heaps | 989.3 G | 3.03 GB |
| four heaps | 988.3 G | **2.09 GB** |

31% of the memory for no instructions. It was submitted, and then withdrawn, because the corpora are
not the whole board.

**The `perf/` tests are single-declaration.** `--jobs 4` runs one worker on them, so their peak
resident is live data rather than collector headroom and there is nothing for a smaller heap count to
give back. Cutting it only makes each collection cover more of a large live set. Comparing the arena's
own CI run of the change against the last published round, on their hardware:

| | instructions | peak resident |
| --- | --- | --- |
| `perf/magma-string-n4` | +16.8% | +9.4% |
| `perf/magma-list-pair-n21` | +14.5% | -18.8% |
| `perf/magma-string-pair-n9` | +12.8% | -2.3% |
| `perf/app-lam` | +11.0% | 0.0% |
| `perf/magma-list-deep-n36` | +10.6% | +2.0% |
| `perf/grind-ring-5` | -7.9% | -2.3% |

Twenty-five scored tests paying instructions so seven corpora can save memory, where the arena's PR CI
runs the twenty-five and not the seven, is not a trade to ask a maintainer to take on faith.

Four other shapes were measured and are worse. A hard heap cap, the equivalent of eink0rn's `-M13g`,
reaches 1.65 GB on the export and then **aborts with exit 134** once the live set passes it, and a cap
safe for Mathlib does nothing for con-leche. `GCConserveMemory=9` buys 0.3 GB and returns the
instruction saving. Nursery sizing is inside the noise. DATAS is a wash on the corpora and does not
recover the `perf/` cost. One worker reaches 1.28 GB for 12% more instructions and four times the wall
clock.

**Where this belongs is inside the checker.** The collector has to be sized before the input is read,
which is why the run line was asked to guess, but the checker can see the input's size at startup and
relaunch itself, which is machinery `--low-memory` already has. That is the version worth submitting,
with both sides measured.

## What has been verified, and what has not

Verified:

| | |
| --- | --- |
| published suite, `--jobs 4` | **71 / 71** reject, **122 / 122** accept (2026-09-21; it was 70 and 119) |
| the same, with `DOTNET_GCHeapCount=4` under `env -i` | unchanged, **71 / 71** and **122 / 122** |
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

**The SDK comes from the arena's own flake.** The first version of this submission had Tenet shelling
out to its own nix shell with `nix develop path:.`, the way `lean4cobol` does. The maintainer asked
for the dependency to go in the arena's flake instead, alongside elan and rustc and the rest, and he
is right: it puts Tenet's requirement in the file that lists everyone else's rather than hiding it
inside one checker's build line. The PR adds `dotnet-sdk_10` there and the build line is a plain
`dotnet publish`.

It has to be the .NET 10 SDK, not 8. Targeting `net8.0` looked easier, since Tenet multi-targets and
nixpkgs has an 8 SDK, but `global.json` pins the SDK to 10.0.1xx with `rollForward: latestFeature`,
so an older SDK does not build slowly, it refuses to start.

`--self-contained true -r linux-x64` is kept, and the first version of this file was wrong about why.
It claimed the flag was load-bearing because `run` executes with a plain copy of the environment. It
does, but `lka.py` itself runs inside the dev shell, so that copy already has the runtime in it and a
framework-dependent publish would have worked. The flag stays because a binary carrying its own
runtime does not depend on how it is invoked, which is a smaller claim than the one made before.

The `arena` job in CI downloads the arena's own published test suite on every push, runs all 189
files through the binary with `env -i`, applies the same exit-code mapping, and fails on anything
short of a perfect score. Running with an empty environment is what proves the binary needs nothing
installed.

**Declining.** Done. The arena reserves exit 2 for "cannot handle this proof", distinct from
rejecting it. Tenet refuses `Lean.reduceBool` and `Lean.reduceNat` rather than trusting compiled
code, and used to report that as a failed declaration, which reads as a claim the proof is invalid.
It is not; it is a claim that this checker will not vouch for it.

`tenet check` now exits **4** and prints `DECLINED` when every failure is of that kind, and the run
line maps 4 to the arena's 2. The negative corpus records which outcome each case expects, and the
test fails if a rejection comes back as a decline or the other way round, so the distinction cannot
quietly collapse in either direction.
