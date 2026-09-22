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

## Declaring heaps, which is the same matter again

`threads: 4` tells the harness how many CPU slots to reserve. It does not tell the .NET garbage
collector anything, and the collector is the part that decides how much memory the process takes.
Server GC sizes itself from the machine's core count: on a twelve-core box it builds twelve heaps,
each holding its own gen0 headroom, whether or not four threads are all that will ever run. So a
checker can declare four threads honestly and still take a big machine's worth of memory, which is
what Tenet was doing.

Measured on the `Lean` export, 164,133 declarations and 11.98M expressions, `--jobs 4` throughout:

| | wall | CPU | peak resident |
| --- | --- | --- | --- |
| unset (twelve heaps) | 26.3s | 128.5s | **3.67 GB** |
| `DOTNET_GCHeapCount=4` | 29.3s | 125.9s | **2.06 GB** |
| `DOTNET_GCHeapCount=2` | 35.8s | 130.7s | 1.71 GB |
| `DOTNET_GCConserveMemory=9` as well | 31.4s | 130.8s | 1.74 GB |
| workstation GC | 47.1s | 133.1s | 1.33 GB |

Four heaps cuts peak resident by **44%** and costs nothing in instructions, which is the metric the
board scores: CPU is 125.9s against 128.5s, inside the run-to-run spread. Wall time is about 9%
worse, because collection no longer overlaps checking across as many threads. On a board that scores
instructions and memory rather than wall clock, that is the right way round.

Two and `GCConserveMemory=9` were tried and are not worth it. Both buy about another 0.3 GB and give
back the instruction saving, and two costs 22% of wall time on top.

The workstation row is there to say where the floor is. 1.33 GB is close to the live data itself:
the environment holds every declaration including proof terms, because later ones may refer to them.
Anything under that number needs a change to what is retained, not to how it is collected. Dropping
a theorem's value once it has been checked would do it, since nothing else can unfold a theorem in
practice, but Lean's kernel does treat theorems as unfoldable and the whole claim of this project is
that it decides what Lean decides. That is a faithfulness question, not a tuning one, and it is not
being answered by guessing.

The setting is on the `run` line beside `--jobs 4` rather than baked into the build, so the two
numbers that have to agree are on adjacent lines and a reviewer can see that they do.

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
