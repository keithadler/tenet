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
and pypy, and nothing for .NET, so the build line above uses `nix develop path:.` to bring Tenet's
own, which is what `lean4cobol` does. **That flake does not exist yet.** Targeting `net8.0` rather
than `net10.0` is deliberate: nixpkgs carries a .NET 8 SDK, and Tenet multi-targets, so the older
one is available without waiting on packaging.

**Declining.** The arena reserves exit 2 for "cannot handle this proof", distinct from rejecting it.
Tenet refuses `Lean.reduceBool` and `Lean.reduceNat` rather than trusting compiled code, and reports
that as a failed declaration, which reads as a claim the proof is invalid. It is not; it is a claim
that this checker will not vouch for it. No test in the published suite exercises it, so it does not
affect the score, but the honest mapping is a decline and Tenet has no way to say so yet.
