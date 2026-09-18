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

## Two things to settle before submitting

**The arena's environment has no .NET.** Its nix flake provides elan, rustc, node, ocaml, zig, ghc
and pypy, and nothing for .NET, so the build line uses `nix develop path:.` to bring Tenet's own, the
way `lean4cobol` does. That flake now exists at the repo root and exists only for this.

It uses `dotnetCorePackages.sdk_10_0`, not the .NET 8 SDK. Targeting `net8.0` looked easier, since
Tenet multi-targets and nixpkgs has an 8 SDK, but `global.json` pins the SDK to 10.0.1xx with
`rollForward: latestFeature`, so an older SDK does not build slowly, it refuses to start.

The `arena` job in CI runs the build and run lines from `tenet.yaml` verbatim on every push, over the
negative corpus and a valid export, checking the arena's own convention that 0 is accept and 1 is
reject and anything else is neither. There is no nix on the development machine this was written on,
so CI is the only thing that has ever executed the flake.

**Declining.** The arena reserves exit 2 for "cannot handle this proof", distinct from rejecting it.
Tenet refuses `Lean.reduceBool` and `Lean.reduceNat` rather than trusting compiled code, and reports
that as a failed declaration, which reads as a claim the proof is invalid. It is not; it is a claim
that this checker will not vouch for it. No test in the published suite exercises it, so it does not
affect the score, but the honest mapping is a decline and Tenet has no way to say so yet.
