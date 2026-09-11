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

## Classic attacks

`AttackTests` states the standard ways to derive `False` in a dependent type theory as
declarations the kernel must reject: a non-positive inductive (Curry's paradox), a
constructor argument in a universe too large for its type (Girard's paradox), a constructor
that does not return its own type, an ill-typed index, large elimination out of a
proposition with several constructors (deciding propositions), definitional equality
between proofs of different propositions, `Sort u : Sort u`, and literal arithmetic whose
result would not fit (refused, never computed). Each test also checks the rejection
reason, so a rejection for the wrong reason fails.

## Robustness of the `.olean` reader

A memory-mapped reader that trusts stored pointers or sizes would read outside the mapping
on a damaged file and kill the process, or loop on a pointer cycle. Every raw read in
`OleanModule` goes through one bounds-checked accessor, and every stored pointer must lead
to an earlier object (Lean's compactor writes an object after everything it points to, and
later parts only point into earlier ones), so any walk over the object graph terminates.
`OleanFuzzTests` corrupts the `Init.Coe` fixture in random ways (byte flips, bit flips,
overwritten words, truncation, zeroed blocks) and requires every attempt to end in an
`OleanFormatException` or a clean decode; 400 iterations per test run, more with
`TENET_FUZZ_ITERATIONS`. The first run found one leak, a `BinderInfo` check that threw the
wrong exception type. `tenet check` reports a malformed module as a file error (exit 2).

## Differential testing against Lean's kernel

The strongest check on Tenet is disagreement hunting: the same export, possibly damaged,
judged by Lean's kernel and by Tenet, declaration by declaration.

`tools/leancheck` is a small Lean program that reads an export with lean4export's own
parser and replays it through `Lean.Kernel.Environment.addDeclCore`, printing `OK name` or
`FAIL name: reason` per declaration, re-deriving inductive blocks and comparing them with
the export exactly as Tenet does, and installing a failed declaration unchecked so later
ones can still be judged, again as Tenet does.

With `LEANCHECK_TIMES=1` in the environment, leancheck also prints `TIME name microseconds`
to stderr for every declaration, the wall-clock time Lean's kernel spent in `addDeclCore`.
This is the ground truth for Tenet's performance work: the same export checked by both,
declaration by declaration.

### Targeted campaigns

The mutation menu is split. Most kinds damage the term and both kernels must reject; a few
rewrite it into something *equal*, so both must still accept, and a rejection from either side
is a completeness gap. Both real kernel bugs found so far lived on that side, so those kinds
are worth more per run. `--list-kinds` prints the menu and marks which are which;
`--kinds a,b,c` restricts a campaign to a chosen few, which makes a disagreement point at one
cause instead of eight.

The targeted kinds aim at one kernel feature at a time: universe level normalization
(`level-max-commute`, `level-imax-commute`, `level-max-idem`, `level-succ-drop`), literal
arithmetic and overflow (`natlit-boundary`, at 0, 2^31, 2^63, 2^64 and 2^128), nested inductive
metadata (`ind-numnested`, `ind-isrec`, `ind-isreflexive`), and recursor and constructor arity
(`rec-numminors`, `rec-numindices`, `ctor-numparams`).

Classifying a mutation as equality-preserving is a claim that has to be right. `max u v` to
`max u u` and `succ u` to `u` both look like level rearrangements and are not equal; they are in
the damage group for that reason. Getting this wrong turns an expected rejection into a false
bug report.

### A corpus built to be mutated

`tools/edgecases` is a small Lean library dense in the rules a general export exercises only
rarely: structure eta, proof irrelevance, K-like reduction, quotient reduction, nested and
mutual inductives, literal arithmetic at the word boundaries, and universe polymorphism. Both
kernels accept all 58,236 declarations of its export, which for several of those rules is the
only direct comparison against the reference the project has.

`lean4export` writes the whole transitive closure, so the corpus is a sliver at the end of a
6.5-million-line file and random mutation almost never lands on it. `--tail N` restricts
mutation to the last N lines, where the newest declarations sit.

### What the targeted campaigns found

Nothing, so far, and the shape of the nothing is worth recording.

| Campaign | Variants | Agreed rejections | Disagreements |
| --- | --- | --- | --- |
| `level-max-commute` on `Init.Prelude` (equality-preserving) | 40 | 0, as required: both kernels accepted every variant | 0 |
| Ten damage kinds on `Init.Prelude` | 50 | 6,230 | 0 |
| Ten damage kinds on `Init.Core` | 30 | 4,233 | 0 |
| All kinds against the edge-case corpus, `--tail 300000` | 12 | 85 | 0 |

The equality-preserving run is the more informative of the three. Zero rejections on either
side is the correct answer, and it confirms both kernels agree that `max u v` and `max v u`
denote the same universe. The damage runs confirm the two kernels reject the same things for
the same reasons across level normalization, literal boundaries, inductive metadata and
recursor arity.

This method has a ceiling worth stating. Mutating an export is good at finding places where
two implementations of the same specification drift apart, which is how both real kernel bugs
here were caught. It is unlikely to find a deep soundness bug in Lean, because such a bug needs
a term someone constructed deliberately against the type theory, not one produced by damaging
a valid term at random. Fuzzing finds implementation disagreements; it does not find design
flaws, and nobody should read a clean campaign as evidence that none exist.

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

Since Tenet caches failed definitional-equality checks and re-checks a rejected
declaration with the cache off (docs/design.md, "The failure cache"), run a campaign
both ways: once as above, and once with `TENET_NO_FAILURE_CACHE=1` in the environment
so the faithful algorithm alone is compared against Lean.
Both campaigns (40 variants of Init.Prelude, seed 7) gave 12,145 agreed rejections and no
disagreements.

The CI campaign (seed 36) then found a second real difference: `RecursorInfo.GetMajorInduct`
walked the recursor's type accepting only pis, while the reference's `binding_body` walks
lambdas too, so after a mutation turned the major premise's binder into a lambda Tenet
rejected `Substring.Raw.noConfusion` and Lean did not. Fixed by walking both binders.

### Lookups on declarations installed unchecked

After a failure both tools install the failed declaration unchecked, so from then on a
constant can name something that does not exist. Verdicts on those variants depend on how a
lookup answers for an unknown name: Lean's kernel uses `env().get`, which throws "unknown
constant", in `infer_constant`, `infer_proj`, `reduce_proj_core`, `try_eta_struct_core`,
`is_def_eq_unit_like`, `is_non_rec_structure` and `get_first_cnstr`, and `env().find`, which
answers "no", in `is_delta`, `is_constructor_app` and the recursor lookup of
`inductive_reduce_rec`. Tenet uses `Get` and `Find` at the same places. Seed 59 found the
cost of getting one wrong: with `Eq.refl`'s type renamed to a bare `refl`, a `Find` in the
structure test let K-like reduction be skipped and the reduction succeed by another route,
and twenty `noConfusion` declarations were accepted that Lean rejected.

### Triage: disagreements that are not Tenet bugs

Differential testing on mutated inputs can produce disagreements that are artifacts of
the reference rather than defects in Tenet. Known classes:

- **Universe normalization incompleteness in Lean.** Lean's `is_equivalent` on levels
  normalizes each side once and does not re-flatten after an `imax` collapses into a
  `max`, so `imax s (max r 1)` and `max s (max r 1)` (the same universe) normalize to
  `max s (max 1 r)` and `max 1 (max r s)` and are judged unequal. Tenet's algorithm matches
  Lean's here (see `LevelReferenceAlgorithmTests`). The disagreement arises because Lean
  rewrites levels through simplifying constructors only when a pointer-identity shortcut
  fails, which happens after the sharing pass Lean applies to theorem proofs; so Lean can
  end up comparing `imax s (max r 1)` against `max s (max r 1)` where Tenet compares the
  unchanged level against itself. Lean's elaborator never emits unsimplified levels, so
  this cannot occur on a real export. Seen as `PULift.up.inj` in an Init.Core variant with a
  `max-imax-swap` mutation. Run the oracle with `pp.universes` (it does so by default) to
  recognize the pattern: two `Eq.{...}` levels that are equal as universes.
  The same mechanism also produces the STRICT direction. In a `Mathlib.Data.Real.Basic`
  variant, one `max (u_2+1) (u_1+1)` in the shared level table became
  `imax (u_2+1) (u_1+1)`, and 46 declarations (`Sum.range_eq`, `StateT.instLawfulMonad`,
  `RelEmbedding.wellFounded`, ...) were rejected by Tenet and accepted by Lean. Both
  kernels normalize that `imax` to the unsorted `max (u_2+1) (u_1+1)`, which is not
  structurally equal to the sorted `max (u_1+1) (u_2+1)` on the other side; Tenet
  therefore rejects, and so would Lean's C++ on pointer-identical objects. Lean accepted
  because instantiating the universe parameters of an imported constant rebuilt the level
  through `mk_imax`, which turns it into a `max` and lets the sorted normal forms agree;
  the rebuild happens only when pointer identity has been broken by sharing. The same
  `max`-to-`imax` mutation on the prelude export produces identical verdicts from both
  kernels, so whether Lean rewrites depends on object provenance, not semantics. Again
  impossible on a real export: the elaborator never stores `imax _ (_+1)`.
- **Recovery policy after a failed block.** Both tools install a failed declaration
  unchecked and continue; the oracle mirrors Tenet's choices (including enabling quotient
  reduction after a broken quotient block). If the tools ever diverge here, later
  rejections will differ without either kernel being wrong.
- **Lean crashes.** Lean's kernel segfaults or aborts on some ill-formed inputs installed
  unchecked. The harness reports the run as incomplete and keeps the variant.
- **Neither kernel finishes.** A mutation can make definitional unfolding run away: on one
  Mathlib-slice variant, both kernels ground on the same mutated
  `Std.Time.PlainTime.format._sparseCasesOn_1` for more than ten minutes. Tenet's unfolding
  limit ends it with a deterministic timeout (100 million by default; `TENET_MAX_UNFOLDS`
  lowers it for campaigns); Lean's kernel has no such limit, so the harness kills a run
  after `--timeout` seconds and counts it as incomplete. When only one kernel finishes, check
  it is the one with the limit before reading anything into the difference.
