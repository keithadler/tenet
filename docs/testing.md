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
dotnet run -c Release -f net10.0 --project src/Tenet.Cli -- check ~/tenet-exports/Init.Prelude.ndjson
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

## The reader against Lean's own exporter

Comparing verdicts with Lean's kernel cannot find a bug in the `.olean` reader. A reader that
drops a hypothesis yields a weaker theorem, and both kernels accept it, honestly and for the
same reason. `tenet crosscheck` covers that path by comparing every constant the reader decodes
against the same constant as `lean4export` wrote it.

Crosscheck therefore carries the whole weight of the claim that the reader is faithful, and
until `tools/readercheck/readercheck.sh` existed it had only ever been given undamaged input,
where it answers "no differences" whether or not it is capable of seeing one. Readercheck
damages the export one mutation kind at a time and asks whether crosscheck says so. Any kind it
misses is a class of reader defect that would go unreported.

The first run found two, both in `ExprOps.FirstDifference`:

- It began with `a.Equals(b)`, which ignores binder names and binder infos the way the kernel
  does. A pair differing only in binder metadata was answered "(equal)" before the comparison
  that names the difference could run, so the "binder names or implicitness only" line that
  crosscheck prints could never be reached by a binder-only difference. It now uses
  `Expr.EqStrict`, which compares that metadata, because this is a reader being checked against
  a reference and not a proof against a type.
- A `let`'s `nonDep` flag was ignored outright, with a comment saying so. `Expr.Equals`
  distinguishes it, so `FirstDifference` was answering "(equal)" about terms the kernel's own
  equality calls different.

Fixing both surfaced 619 binder differences and 18 `nonDep` differences on an undamaged
`Init.Prelude`, all of them previously invisible. The `nonDep` ones are not a reader defect:
`lean4export` normalizes the flag to `false` on purpose, so that two expressions differing only
in that hint do not take two indices in its table (`Export.lean`, `removeMData`). Tenet's reader
preserves what the `.olean` holds, which is the stricter and more faithful behavior, so
crosscheck now reports these in their own line rather than counting them as identical.

Readercheck exercises 28 of the 29 mutation kinds; `drop-safety` cannot apply, because
`lean4export` omits unsafe declarations entirely and no export contains one. A mutation is only
visible if it lands in a declaration the target `.olean` actually holds, so give it the whole
module tree (`$LEAN_SYSROOT/lib/lean/Init`) rather than a single module when the export covers
an import closure.


Re-measuring `Init` with the strict comparison shows how blind the old one was, and that the
answer that matters did not move:

| `Init`, 652 modules | Compared | Cosmetic | nonDep | Realized elsewhere | Substantive |
| --- | --- | --- | --- | --- | --- |
| before the fix | 59,720 | 41 | not visible | 16 | **0** |
| after the fix | 59,477 | 18,499 | 1,589 | 1 | **0** |

Eighteen thousand binder differences were there the whole time and could not be reported. None
of them can change a verdict, which is why they are cosmetic, but a check that cannot see them
also could not have seen a reader that got them wrong. The substantive column is still zero,
now under a comparison strong enough for that to mean something. (The two rows are different
Lean versions, hence the small difference in constants compared.)

The Mathlib slices, re-measured with the strict comparison against Mathlib on Lean 4.32.1:

| Export | Compared | Cosmetic | nonDep | Substantive |
| --- | --- | --- | --- | --- |
| `Analysis.SpecialFunctions.Trigonometric.Basic` | 151,691 | 46,846 | 3,341 | **0** |
| `CategoryTheory.Limits.Shapes.Products` | 28,712 | 8,945 | 525 | **0** |
| `LinearAlgebra.Matrix.Determinant.Basic` | 100,516 | 32,679 | 2,061 | **0** |
| `NumberTheory.Padics.PadicNumbers` | 131,620 | 40,755 | 2,856 | **0** |
| `Topology.MetricSpace.Polish` | 128,004 | 39,633 | 2,748 | **0** |
| **distinct constants, unioned** | **166,048** | | | **0** |

The old measurement of the same five slices put the cosmetic column at 64, 30, 59, 57 and 57.
That is what the comparison could see when it short-circuited on an equality that ignores binder
metadata: only a binder difference inside a declaration that already differed for some other
reason. The substantive column is the one that carries the claim, and it was zero then and is
zero now, under a comparison strong enough for zero to mean something.

The rows are a different Mathlib and a different Lean from the earlier table, which is why the
compared counts moved; the union is over the constants these five runs actually compared.

Count the union, never the sum. Those five runs add to 540,543, which overstates coverage by a
factor of 3.3, because any two Mathlib exports share most of their closure. `--names-out` writes
every constant compared so the union can be taken; 166,048 is roughly a fifth of what a full
Mathlib check covers, and the rest is still untested ground.

"Cosmetic" means a difference of binder name or implicitness, which the kernel ignores.
"nonDep" means a `let`'s optimization hint, which `lean4export` normalizes to false on purpose.
"Realized elsewhere" means one private auxiliary generated in a different module: the same
declaration under a different prefix. None of the three can change what is accepted.

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

### Against con-leche, a checker with a consistency proof

[con-leche](https://github.com/leanprover/con-leche) is an external Lean checker proven in Lean
not to accept a proof of `False`. That is a stronger guarantee than anything here: Tenet is
tested, con-leche is proved. Running the two on identical export files is therefore worth more
to Tenet than to con-leche, and it is the best assurance evidence this project has.

| Export | declarations | Tenet | con-leche |
| --- | --- | --- | --- |
| `Analysis.SpecialFunctions.Trigonometric.Basic` | 289,758 | 0 failed | accepted |
| `NumberTheory.Padics.PadicNumbers` | 269,848 | 0 failed | accepted |
| `Topology.MetricSpace.Polish` | 266,295 | 0 failed | accepted |
| `LinearAlgebra.Matrix.Determinant.Basic` | 239,057 | 0 failed | accepted |
| `CategoryTheory.Limits.Shapes.Products` | 166,980 | 0 failed | accepted |
| **distinct declarations, unioned** | **312,904** | | |

Count the union. Those five add to 1,271,680, which overstates coverage fourfold, because any two Mathlib
exports share most of their closure.

`tenet check EXPORT --names-out FILE` writes every constant the run ended with, which is what makes the union
countable. It was only on `crosscheck` before, which needs an `.olean` tree alongside the export, and the claim
being made here is about `check`. On `.olean` input the flag is refused rather than accepted, since that path
checks module by module and keeps no single environment to list.

The nightly rotates through seven slices by day of year rather than re-proving one of them forever, and uploads
the name list as an artifact, so the union grows on its own instead of when somebody remembers to add a slice.
A module Mathlib master has renamed costs one night: the job falls back to the first slice and says so.

### Checking the proof itself

Running Tenet and con-leche on the same export and having both accept transfers con-leche's proof to what
Tenet accepted. That transfer rests on con-leche's proof being valid, and until now the only thing that had
checked that proof was Lean's own kernel: the same kernel con-leche exists to double-check.

Tenet checks it too. con-leche requires no packages, so its library builds standalone.

```
tenet check ConLeche/MainTheorem.olean --all --quiet
OK: 232,881 checked in 2,791 modules, 0 failed, 50.2s

tenet axioms ConLeche/MainTheorem.olean ConLeche.no_False_declaration
ConLeche.no_False_declaration depends on 27,357 constants and these axioms:
  propext
  Classical.choice
  Quot.sound
```

That matches what con-leche reports about itself with `#print axioms`, arrived at by a kernel that shares no
code with the one that produced the file.

**What it removes.** Not Lean. This is still a Lean proof, checked by a kernel written to decide what Lean's
kernel decides and calibrated against it. What it removes is the *shared implementation*: the failure mode
where one bug in the C++ kernel makes both the checker under test and the proof of that checker wrong
together, in the same direction, invisibly.

**What it does not establish** is that the theorem says what you want. `tenet statement` gives the honest
version: the statement is built from 28 constants, 14 of which con-leche defines itself, and a wrong
definition would hide in those. No kernel helps there. It names them so they can be read.

The `proof-of-soundness` nightly job runs all of this and fails if the proof is not accepted in full, if any
axiom beyond the three appears, or if all three are not named, so a reworded or empty report cannot pass as a
clean one.

### What that licenses, and what it does not

con-leche's `no_False_declaration` is a machine-checked theorem that a file declaring a theorem of type `False` is
never accepted in its verified mode. Both checkers accepted each of these files in full, neither rejecting
anything, so every declaration in them was accepted by both.

What follows is that **Tenet accepted nothing in these corpora that would have made a proved checker reject**.
That is a different kind of statement from two tested implementations agreeing, because one side of it rests on a
proof rather than on having been run a lot.

Four things it does not say, each worth stating before someone reads more into the number than is there.

It is about these corpora, not about the kernel. A declaration nobody ran is not covered, and a proof would cover
it. This is evidence, not a theorem about Tenet.

con-leche's theorem is narrower than "sound". It rules out a file declaring a theorem of type `False`, which is
the canonical unsoundness and the one worth ruling out, but it is a specific shape rather than every way a checker
could be wrong.

Both read the same export file. Their parsers are independent, which is worth something, but a fault in
`lean4export`'s rendering of the `.olean` would be invisible to both. That path is covered separately, by
`crosscheck` against Tenet's own `.olean` reader, and is measured in the reader section above.

And the counts agreeing exactly on every slice is a good sign rather than a check. It indicates both processed the
same declarations, but neither tool emits the set, so it was not verified as a set. The claim rests on both
rejecting nothing, which does not need the sets to be compared.

The two take visibly different internal routes to the same verdicts. con-leche's log reports 62
projection functions of non-direct structure-likes rewritten to recursor form, and two
declarations hoisted ahead of the pinned `Nat` operations they ground. Tenet does neither and
agrees anyway, which is the point of an independent check.

On the same files with twelve jobs, Tenet ran `Init` in 5.5 s against 14.5 s, and the Mathlib
slice in 19.7 s against 63.1 s. con-leche's README states plainly that it is deliberately slow
because the annotation work is what makes its proof tractable, so this measures the price of
the proof rather than a defect.

### con-leche as a third opinion, and what it found

Two checkers that differ tell you they differ. A third tells you which one is alone, so
`difftest --oracle2` puts a Tenet-versus-Lean disagreement to con-leche as well.

Reading its answer takes care. con-leche reports one verdict for a whole file and stops at the
first problem it finds, and a variant carries a dozen mutations, so "con-leche rejected the
variant" is usually about some other declaration entirely. The harness only counts it as having
settled a disagreement when it accepts the whole file, which means it accepted the disputed
declaration too, or when its message names that declaration. Otherwise it says the question was
left open. The first version of this did not make that distinction and reported con-leche as
siding with Lean on a disagreement it had said nothing about.

Asked properly, it answered. Reducing the `PULift.up.inj` disagreement (see the triage section
below) to the single mutation that causes it, one level in the shared table turning `max u v`
into `imax u v`, gives three different verdicts on the same file:

| Checker | Rejects |
| --- | --- |
| Lean's kernel | 3: `PULift.noConfusion`, `PULift.up.inj`, `PULift.up.injEq` |
| Tenet | 1: `PULift.noConfusion` |
| con-leche | 0, accepts all 3,482 declarations |

All three are sound here and they differ in completeness, con-leche being the most complete.
The level in dispute for `PULift.noConfusion` is `imax (imax s (max r 1)) u` against
`imax (max (max 1 r) s) u`. The inner `max r 1` is at least 1 for every assignment, so the inner
`imax s (max r 1)` is `max s (max r 1)`, which is the sorted `max (max 1 r) s`, and the two outer
levels are therefore the same universe. con-leche sees this. Neither Lean nor Tenet does, and
Tenet sees two of the three cases Lean does not.

So Tenet carried a level-normalization completeness gap of its own, narrower than Lean's and in
the safe direction: it rejected something valid rather than accepting something invalid. It cannot
arise on a real export, because Lean's elaborator never stores an unsimplified `imax _ (max _ 1)`.

`Level.CompleteEquality` closes it, by case analysis on which parameters can be zero rather than by
normalizing once, and `LevelCompletenessTests` decides exactly this pair with it on and does not
with it off. It is off by default, since deciding more than Lean is a divergence; see
[divergences.md](divergences.md). The gap is recorded here because it is the first thing the third
opinion found, and because a gap that only a proved checker can see is exactly what two testers
agreeing with each other will miss.

One difference in what each will accept. con-leche takes only Lean's three standard axioms and
stops on anything else:

```
con-leche: not implemented yet: non-standard axiom (AxTest.knownResult)
tenet:     OK: 57900 checked, 0 failed
```

That means con-leche cannot currently check the FLT project, which carries `knownin1980s`,
`Mazur_statement` and `Odlyzko_statement`. Tenet checks such projects and reports which
declarations rest on which assumption, which is the case `audit` and `why` exist for.

### Comparing statements across projects

`tenet compare` answers the one faithfulness question a machine can settle: when two groups
formalize the same claim independently, do their statements agree? Both are brought into one
environment and compared by definitional equality, so a difference of phrasing is seen through
and a difference of meaning is not.

The corpus carries the test cases: `sum_direct` and `sum_via_alias` state one claim through
different definitions and compare equal, `sum_near_miss` differs by one number and does not,
and `addzero_n` and `addzero_m` differ only in a binder name and are identical.

Where a name carries different content in the two projects, that is reported rather than
resolved. Two statements built from a name that means two things will look alike and are not.

### A worked case: is OpenAI's Navier-Stokes statement the one it claims to adapt?

OpenAI's `formalization.yaml` names DeepMind's
[Formal Conjectures](https://github.com/google-deepmind/formal-conjectures) file as the
independent statement of the Clay problem, and its Comparator README thanks those authors for
the formalization it adapted. Both sides therefore agree on what the reference is, and the
question is whether the adaptation changed anything. A theorem name and a statement's surface
text can be identical while a structure underneath has been weakened, and the proof would still
check.

Two confounds had to go first. The projects pin different Lean toolchains, 4.33.1 against
4.34.0-rc2, so a comparison across them is really a comparison across two Mathlib versions: the
first attempt reported 216 shared names carrying different content, which tells you nothing
about the adaptation. The Formal Conjectures file was therefore rebuilt inside the OpenAI
project against its exact Mathlib, with only the FC-specific attributes stripped and its two
scoped notations inlined verbatim. Notation abbreviates; it changes no term.

| Declaration | Result |
| --- | --- |
| `divergence` | definitionally equal |
| `IsOnePeriodic` | identical |
| `InitialVelocityCondition` | all fields definitionally equal |
| `ForceCondition` | all fields identical |
| `NavierStokesExistenceAndSmoothness` | all fields definitionally equal |
| `InitialVelocityConditionDecay` | 3 of 4 fields equal; the 4th is `toInitialVelocityCondition` |
| `ForceConditionDecay` | same shape; the differing field is `toForceCondition` |
| `NavierStokesExistenceAndSmoothnessRn` | 8 of 9 fields equal; the 9th is `toNavierStokesExistenceAndSmoothness` |
| `NavierStokesExistenceAndSmoothnessPeriodic` | 8 of 9 equal; the 9th is the parent |
| `InitialVelocityConditionPeriodic` | 3 of 4 equal; the 4th is the parent |
| `ForceConditionPeriodic` | 4 of 5 equal; the 5th is the parent |

Every field that *can* differ in content is equal. The single field that differs in each
derived structure is always the inheritance reference to the parent, and that one can never
match: two structures declared separately are distinct types in Lean no matter how identically
they are written. The parents themselves compare equal on their own fields, so the content
agrees the whole way down, including the row that carries the actual equations.

What this does not establish is that either statement is a faithful rendering of the Clay
problem. It establishes that the adaptation preserved the statement it started from. Whether
that statement is right remains a question for people who read it.

## Exports written to attack the checker

The mutation harness takes a valid export and breaks it. That finds places where two implementations of one
specification drift apart, which is what it is for, and it found both of the real Lean kernel bugs this project
has caught. It cannot find a file written on purpose to exploit what the checker assumes, because a damaged valid
file is not one.

Both soundness bugs found in Tenet itself were files of the second kind, and neither was reachable by mutation:

- a file declaring `def Nat : Prop := False` and then `def boom : False := 3`, which was accepted with an empty
  axiom list, so `tenet audit` called a proof of `False` unconditional;
- a file declaring `Nat.add` at the right type with a body that returns its first argument, which made the checker
  accept `2 + 2 = 4` and reject `2 + 2 = 2`.

Both came from reading lean4lean's divergences file rather than from running anything. `tests/HostileTests.cs`
holds the cases, organized around the attack surface rather than around the two bugs: every name the kernel
hardcodes is something it takes on trust, and each deserves a file that abuses it.

```sh
grep -ohE 'Name\.Of\("[^)]*"\)' src/Tenet.Kernel/*.cs | sort -u
```

Binder names in that list are cosmetic. The rest are assumptions: the type a literal denotes, the sixteen `Nat`
operations the kernel computes itself, the constants a string literal expands through, the quotient block, and
`Lean.reduceBool`.

`tests/TrustSurfaceTests.cs` runs that grep from a test and requires every name to be accounted for, as a binder,
as a name the kernel invents and never looks up, or as an assumption mapped to the case that attacks it. The
mapping names a method, and the method has to exist, so renaming a case cannot leave a name looking defended by
something that is gone.

This matters more than the individual cases. A name added to the kernel tomorrow is a new assumption with no
attack written for it, and nothing about a green suite would say so: the failure is not a test that breaks but a
test nobody wrote. Adding one now fails the build until somebody classifies it. Both halves were verified by
adding a name to the kernel and watching the test name it, and by classifying a name the kernel does not have.

Writing it found `eagerReduce` unattacked. It is the odd one out, since it asserts nothing about a term and
instead turns on a mode that reduces harder. That cannot launder a falsehood, because reduction does not change
what a term means, but it is attacker-controlled effort, and it now has a case.

Working the list found three gaps, since closed, and four places already defended. The defended ones are in the
catalog too, so that a later simplification cannot quietly remove them:

| Name | How it is defended |
| --- | --- |
| `Quot`, `Quot.mk`, `Quot.lift`, `Quot.ind` | the kernel builds the quotient constants with the types it requires rather than reading a file's, and will not reduce until it has |
| `Eq` | `Quot.CheckEqType` checks its kind, universe count, constructor count, and both exact types before the quotient block is admitted |
| `Bool`, `Bool.true`, `Bool.false` | a comparison's shortcut is licensed by equations stated against those very constants, so a body that answers differently fails them and is unfolded instead |
| `optParam`, `autoParam`, `outParam`, `semiOutParam` | stripped by name without validation, but the stripped parameter type is compared against the declared one when constructors are checked, and a wrapper that is not the identity fails there |
| `Lean.reduceBool`, `Lean.reduceNat` | refused rather than believed, so a claim cannot be laundered through compiled code |

The annotation row is the weakest of these: it is defence in depth rather than by design, and it rests on a check
whose purpose is something else. It is written down so that it is a decision rather than an accident.

Each case asserts two things: that the attack is refused, and that it succeeds with the defense switched off.
Without the second half a case can pass because the attack was built wrong, which is how a catalog of attacks
quietly becomes a catalog of nothing.

## Which rules the evidence actually covers

"All of Mathlib checks with no failures" is the headline claim of this project, and on its own
it does not say what was exercised. A rule no corpus reaches is untested however many
declarations passed through it, and nothing in a green run distinguishes a rule that works from
a rule that is never called. `tenet check ... --rules` counts each of the kernel's 36 typing and
reduction rules by name, so that is a measurement rather than an assumption.

The catalog has to be complete or the fraction it reports is wrong in the flattering direction.
Checking it against Lean's `type_checker.cpp`, step by step through `whnf_core` and
`is_def_eq_core`, found four rules missing from it: the free-variable equality check, the
proof-by-reflection shortcut against `Bool.true`, `lazy_delta_proj_reduction`, and
`try_unfold_proj_app`. The denominator was 36 and should have been 40.

All of `Init`, 64,814 declarations across 649 modules, 52.5 million rule firings:

| Reached | Count |
| --- | --- |
| 36 of 39 reachable rules | |
| `Beta` | 11,400,486 |
| `DefEqSyntactic` | 10,109,100 |
| `InferApp` | 10,089,370 |
| `DefEqOffset` | 4,630,402 |
| `DeltaLazy` | 4,593,357 |
| `Delta` | 3,314,730 |
| `Iota` | 1,604,600 |
| ... | |
| `DefEqEta` | 1,014 |
| `IotaK` | 379 |
| `QuotLift` | 227 |
| `StringLitToCtor` | 29 |
| `DefEqUnitLike` | 5 |
| `QuotInd` | **1** |
| `InferBVar`, `NativeReduce`, `DefEqStringLit`, `DefEqFVar` | **0** |

The tail is the interesting part. `Quot.ind` reduced exactly once in all of `Init`, and
`DefEqUnitLike` five times. Passing 64,658 declarations is strong evidence about `Beta` and
almost none about `QuotInd`: one firing is one test.

Of the four at zero, two are expected. `InferBVar` is the error path for a loose bound variable,
which cannot occur in a well-formed export, and `NativeReduce` is the point where Tenet refuses
`Lean.reduceBool` and `Lean.reduceNat` rather than trusting compiled code.

`DefEqFVar`, one of the four the catalog had been missing, is zero for a structural reason.
Reaching the free-variable check in `is_def_eq_core` means arriving there with the same variable
on both sides, and every path that could do so passes a `quick_is_def_eq` that answers first:
before any reduction, and again after `whnf_core` whenever either side changed. The only route
left is `lazy_delta_reduction` leaving both sides as one variable with no check in between, and
delta unfolds constants, whose values are closed. So it reads as defensive code, present because
the reference has it there. That is an argument, not a proof, and it is recorded as one.

`DefEqStringLit` at zero has its own explanation, below.

The edge-case corpus exists to fill exactly this kind of hole, and the first measurement said it
did not. Its README claimed to be dense in K-like reduction and quotient reduction and to be "the
only direct check of several of them the project has"; checking its 113 declarations reached
neither. Stating a rule in Lean source is not the same as making the kernel use that rule while
checking the result, and nothing short of counting can tell the two apart.

The corpus was then written against the measurement rather than against the intent:

Counted out of 40 throughout, which is how the catalog read at the time. One of the forty, `DefEqFVar`, was
later found to be unreachable by any input, so the honest denominator is 39; see below.

| | before | after | `Init` |
| --- | --- | --- | --- |
| declarations | 113 | 158 | 64,814 |
| rules reached | 25 of 40 | **36 of 40** | 36 of 40 |
| `IotaK` | 0 | 2 | 379 |
| `QuotInd` | 0 | 1 | 1 |
| `DefEqEta` | 0 | 4 | 1,014 |
| `DefEqUnitLike` | 0 | 3 | 5 |
| `DefEqConst` | 0 | 1 | 6,365 |
| `InferLet` | 0 | 13 | 10,645 |
| `StringLitToCtor` | 5 | 11 | 29 |

What each case had to be rewritten into is the interesting part.

- The three theorems filed under K-like reduction were comparing one proof against another proof
  of the same proposition, which proof irrelevance settles before any recursor is touched. They
  exercise `DefEqProofIrrel` and are now named for it. Reaching K-like reduction takes a motive
  landing in `Type`, so that irrelevance cannot apply and the kernel has to turn the variable
  `h : a = a` into `Eq.refl` to reduce.
- `DefEqConst` needs a bare constant that lazy delta cannot unfold and that carries no arguments,
  since a definition is unfolded first and an application goes through congruence. An inductive
  type at two spellings of one level does it.
- `QuotInd` looked unreachable, because `Quot.ind` always produces a proof and proof irrelevance
  should settle any comparison of proofs. It does not, because `IsDefEqCore` runs both sides
  through `whnfCore` before consulting irrelevance, and the recursor reduces there. Bisecting all
  64,658 declarations of `Init` for the single firing found `Quot.indBeta`, the library's own
  statement of that reduction, and the corpus now carries the same shape.

The corpus's remaining gaps are now exactly `Init`'s: `InferBVar`, `NativeReduce`,
`DefEqStringLit` and `DefEqFVar`. The first two are meant to be unreachable, and `DefEqFVar`
appears to be, for the reason given above. `DefEqStringLit` is the
one real hole, and it is zero on `Init` for Lean 4.12, 4.24 and 4.34 alike. Reading the reference
settles why, and it is not a defect here.

Lean's `try_string_lit_expansion_core` keys on `*g_string_mk`, which is initialized to
`{"String", "ofList"}`, and `is_def_eq_core` calls it after lazy delta reduction and after
application congruence and eta, immediately before the unit-like rule. Tenet keys on
`Environment.StringLiteralConstructor` and calls it in exactly that position, so the two
implementations agree. What changed is Lean itself: `String.ofList` used to be the structure's
constructor and is now an ordinary definition, with `String.mk` a definition too and
`String.ofByteArray` the constructor. A definition is delta-reducible, so on Lean 4.34 lazy delta
unfolds it first and neither kernel can reach the rule. On 4.12 and 4.24 `String.mk` is the real
constructor and the rule is live, but no declaration in `Init` ever compares a literal against it.

There is a second reason, found later and stronger than the first. `String` is a structure, so eta
for structures reaches a literal against its constructor form before `try_string_lit_expansion`
does: it expands the literal into `String.mk` of its own field, reducing that projection expands
the literal to its character list, and the two sides meet. Lean's kernel places its own copy of
the rule in the same position, after eta, so the shadowing applies there too. That does not depend
on which Lean version is in front of you, and it does not depend on `String.ofList` having become
a definition. It holds wherever `String` is a structure, which is every real environment.

So the rule is unreachable rather than merely unexercised, in both kernels, and it is kept because
the reference keeps it. `SafetyTests` asserts that: the comparison succeeds, `DefEqStringLit` does
not fire, and `DefEqEtaStruct` and `StringLitToCtor` do. An earlier version of that test claimed to
cover the rule, and only did so because it had built `String` as an opaque constant rather than as
a structure, which no export contains.

### A corpus built to be mutated

`tools/edgecases` is a small Lean library meant to be dense in the rules a general export
exercises only rarely: structure eta, proof irrelevance, K-like reduction, quotient reduction,
nested and mutual inductives, literal arithmetic at the word boundaries, and universe
polymorphism. Both kernels accept all 58,236 declarations of its export.

Measuring which rules its own declarations actually reach (`tenet check tools/edgecases
--rules`) says the intent is not met: checking its 113 declarations fires 25 of the 36 rules,
and reaches neither K-like reduction nor `Quot.ind`, two of the rules it was written for. See
the rule coverage section below.

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

  The unfold budget does not bound every way a run can end. Prelude seed 97 variant 002 grows
  a single term deep enough to exhaust a 512 MB stack before it spends anything like 100
  million unfolds. A .NET stack overflow cannot be caught, so that used to abort the process,
  taking down the 1,807 declarations being checked beside it and leaving no report at all.
  The kernel now probes the remaining stack as it recurses and raises
  `RecursionLimitException`, so the one declaration is rejected with "expression too deep"
  and the rest of the variant is checked normally. Every recursion that walks a term or a
  universe level is guarded: `WhnfCore`, `IsDefEqCore`, `InferTypeCore`, the two structural
  traversals in `ExprOps`, structural equality on expressions, and `Normalize`, `PushMaxArgs`,
  `IsGeqCore`, `IsNormLt`, `Instantiate`, `Equals` and the two zero tests on levels. The two
  printers truncate with an ellipsis instead of throwing, because they are what writes the
  message for some other error and must not replace it. Recursion over names and over an
  inductive declaration's structure is not guarded: neither is grown by reduction, both are
  bounded by what the export already holds.

  Each guard has a test that builds something deeper than a 1 MB stack and asserts the
  rejection. Removing any one of them aborts its own test, which is how the tests are known to
  be measuring the guard and not something else. On that variant Tenet now finishes in
  about 112 seconds with 32 rejections; Lean's kernel still does not finish it, allocating
  past 3 GB and climbing. Neither is wrong to diverge on ill-typed input, and the honest
  report is that one side answered and the other did not.

  What the harness must not do is read the wreckage as a verdict. A run that leaves no report
  counts as incomplete rather than as a clean sheet of zero failures, and a campaign where
  every variant is inconclusive exits 4, because zero disagreements across zero comparisons
  is not agreement.
