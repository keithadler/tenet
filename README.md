# Tenet

**An independent implementation of the Lean 4 kernel on .NET, and a checker for Lean exports.**

[![CI](https://github.com/keithadler/tenet/actions/workflows/ci.yml/badge.svg)](https://github.com/keithadler/tenet/actions/workflows/ci.yml)
[![Nightly](https://github.com/keithadler/tenet/actions/workflows/nightly.yml/badge.svg)](https://github.com/keithadler/tenet/actions/workflows/nightly.yml)
[![NuGet](https://img.shields.io/nuget/v/tenet.svg?label=tenet)](https://www.nuget.org/packages/tenet)
[![License](https://img.shields.io/badge/license-MIT%20OR%20Apache--2.0-blue.svg)](LICENSE-MIT)

The nightly badge is the one worth looking at. CI runs the unit tests, the negative corpus and a
small differential campaign on every push; the nightly is where Mathlib is re-checked from master,
where a Mathlib slice is put to a checker carrying a machine-checked consistency proof, and where
that checker's own soundness proof is checked back. A green CI badge says the code builds. A green
nightly badge says yesterday's Mathlib still checks.

Tenet re-implements the trusted core of the [Lean 4](https://lean-lang.org) theorem
prover in C#: expressions, universe levels, environments, type inference, definitional
equality, inductive types (mutual and nested), quotients, and native `Nat` and `String`
literals. Given an export produced by
[lean4export](https://github.com/leanprover/lean4export), it re-checks every
declaration from scratch and reports the first thing it cannot accept.

It is written from the type theory and the reference kernel's behavior, not translated
from it, and it shares no code with Lean. That is the point: a proof that survives two
independent kernels is a proof you can trust a little more. Be precise about the
independence, though. The code is independent; the algorithm is not, because matching the
reference kernel's behavior was the goal and its structure was followed deliberately. So a
defect in Lean's kernel *design*, as opposed to its code, is one Tenet would likely
reproduce rather than catch. Tenet is not the first
independent kernel and does not claim to be; it is the one on .NET. See
[Other checkers](#other-checkers).

```
$ tenet check .lake/build/lib/lean/Mathlib.olean --all
Mathlib: Lean 4.35.0-rc1; 10758 modules mapped in 1.5s, checking all of them
OK: 766950 checked in 10758 modules, 0 failed, 10758 modules mapped, 398.0s, 12 jobs
```

## Status

Version 0.11. Every rule of the reference kernel has a counterpart here. Tenet checks all
of Mathlib and its dependencies, 765,497 declarations in 10,726 modules, directly from the
compiled `.olean` files in about six minutes on a laptop, with zero failures, deriving
the recursors itself and comparing them field for field against Lean's own.

Read alone that is a claim about agreement, not about soundness. A kernel whose check
returns `true` reports zero failures on all of Mathlib too, in less time, so the number
only means something next to what gets rejected. Tenet rejects all nine exports in
[`tests/fixtures/invalid`](tests/fixtures/invalid), a committed corpus of well-formed
files written to be wrong in a particular way: `Type : Type`, a non-positive inductive, a
`Prop` eliminating into `Sort u`, a theorem proved by itself, a swapped proof, a squatted
reserved namespace, a claim laundered through compiled code, and all three soundness bugs
ever found in this checker, kept as permanent regression cases. Each is rejected at the one declaration the manifest names,
with everything before it accepted, and the ones targeting an optional defense are
**accepted in full when that defense is switched off**, which is what stops the corpus
quietly becoming a set of files rejected for being broken. Its verdicts have also been compared
with Lean's kernel on about 130,000 deliberately damaged declarations, and every theorem
in the prelude given the previous theorem's proof must be rejected, at least 95% of them,
with nothing else breaking. See [docs/status.md](docs/status.md) for what has been run and
how the checks were shown not to be vacuous.

## Re-checking a published formalization

OpenAI's [NavierStokesAndEuler](https://github.com/openai/NavierStokesAndEuler)
formalizes finite-time blowup for the three-dimensional Navier-Stokes and Euler equations
in Lean 4. Tenet re-checked it, on commit `8937a8f`:

```
$ tenet check nse --all --quiet
OK: 850211 checked in 13068 modules, 0 failed, 13068 modules mapped, 679.5s, 12 jobs

$ tenet axioms nse/.lake/build/lib/lean/Euler/Solution.olean Euler.euler_breakdown_R3
Euler.euler_breakdown_R3 depends on 89915 constants and these axioms:
  propext
  Classical.choice
  Quot.sound
```

`--all` is the claim worth making. It re-checks the entire import closure in one pass:
Lean's core library, Batteries, Aesop, Qq, the exact Mathlib the project pins, and then
Euler and Navier-Stokes on top. 850,211 declarations, nothing trusted in the middle, no
failures. Each of the four headline theorems rests on nothing but the three standard
axioms, with no `sorryAx`; the same holds for
`NavierStokes.Comparator.navier_stokes_breakdown_R3` and `navier_stokes_breakdown_periodic`.

Checking the project's own modules alone (`tenet check nse`, without `--all`) is 91,178
declarations in 2,486 modules and takes 212 seconds, but it trusts the imported constants
rather than re-deriving them, so it is the weaker statement. Note that Mathlib is pinned per
project: a separate run over some other Mathlib checkout does not cover the one these proofs
actually rest on.

#### A third: a new result, checked on request

[long-mathematics/rank-two-poisson-counterexample](https://github.com/long-mathematics/rank-two-poisson-counterexample)
is Christopher D. Long's explicit counterexample to the rank-two Poisson conjecture
([arXiv:2608.23777](https://arxiv.org/abs/2608.23777)), with a Lean formalization of the paper's
core claims. Checked on commit `6ef94a5`, Lean 4.32.1:

```
$ tenet check rank-two-poisson-counterexample --all
OK: 751482 checked in 10425 modules, 0 failed, 436.7s, 12 jobs

$ tenet audit rank-two-poisson-counterexample
  710 declarations defined by this project in 19 modules
  unconditional: 710 (100.0%)
  resting on an assumption: 0
```

Every one of its 710 declarations stands on nothing beyond `propext`, `Classical.choice` and
`Quot.sound`. No `sorry`, no axiom of its own. The named results check out individually too:
`main_complex` reaches 16,341 constants, `exact_fiber_complex` 14,620,
`explicit_counterexample_complex` 14,247, and each reaches only the three standard axioms.

Worth saying what that does and does not establish, because the repository itself is careful
about exactly this. Its coverage ledger separates PROVED from PARTIAL and MISSING line by line
against the paper, and states plainly that core claims are formalized while several supporting
ones are not. So: the Lean that exists is complete and unconditional, and it does not cover
every claim in the manuscript. Both halves are the author's own framing, and the audit agrees
with it.

### The other answer: a formalization in progress

A green build does not mean a finished proof. `sorry` is a real term of any type, so a
project full of holes compiles perfectly. Kevin Buzzard's
[Fermat's Last Theorem](https://github.com/ImperialCollegeLondon/FLT) is the honest example:
it says so in its own source, and expects to for years. `tenet audit` says the same thing
mechanically, on commit `81d8bee`:

```
$ tenet audit flt
flt: 9821 declarations defined by this project in 262 modules
  unconditional (nothing beyond propext, Classical.choice, Quot.sound): 9630 (98.1%)
  resting on an assumption: 191 (1.9%)

  assumptions carried, and how many declarations rest on each:
    knownin1980s                106 declarations   (a named axiom this project introduces)
    sorryAx                      90 declarations   (an unfinished proof)
    Mazur_statement               1 declarations   (a named axiom this project introduces)
    Odlyzko_statement             1 declarations   (a named axiom this project introduces)
```

Note which one is largest. `knownin1980s` is a deliberate, documented axiom Buzzard uses for
results he is confident can be proved on paper from pre-1990 mathematics. It behaves exactly
like `sorry` and carries more of the project than `sorry` does. An audit that grepped for
`sorry` would have reported a rosier number and missed the bigger assumption; the first
version of this command did precisely that, and reported 99.1%.

The headline theorem is in the 1.9%, as the project says it is:

```
$ tenet axioms flt/.lake/build/lib/lean/FermatsLastTheorem.olean PNat.pow_add_pow_ne_pow
PNat.pow_add_pow_ne_pow depends on 68902 constants and these axioms:
  knownin1980s
  propext
  sorryAx   <-- an incomplete proof
  Classical.choice
  Quot.sound
```

An axiom list names the assumption but not the lemma that brought it in. `tenet why` walks
the chain, and on FLT it traces straight through the project's own reduction structure:

```
$ tenet why flt/.lake/build/lib/lean/FermatsLastTheorem.olean PNat.pow_add_pow_ne_pow

PNat.pow_add_pow_ne_pow
  rests on knownin1980s by this chain:
       PNat.pow_add_pow_ne_pow   [FermatsLastTheorem]
    -> flt   [FLT.Proof]
    -> FLT.Bosses.B1_proof   [FLT.Proof]
    -> FLT.Bosses.B2_proof   [FLT.Proof]
    -> FLT.Bosses.B3_proof   [FLT.Proof]
    -> FLT.Bosses.B4_implies_B3   [FLT.Proof]
    -> FreyPackage.mazur   [FLT.FreyCurve.Mazur]
    -> knownin1980s   [FLT.Assumptions.KnownIn1980s]
  rests on sorryAx by this chain:
       ... -> FLT.Bosses.B3_proof -> FLT.Bosses.B4_proof -> sorryAx
```

Eight and a half seconds, and it names the two places the work actually stands: Mazur's
theorem, cited as known before 1990 rather than formalized, and the fourth reduction step,
still unproved. The path is a shortest one, so it names one route to each assumption rather
than the whole subgraph.

That is the contrast worth drawing. Both projects compile. One rests on the three standard
axioms and nothing else; the other carries four named assumptions and says so plainly. A
kernel can tell you which, in seconds, without reading a word of prose.

## Reproduce it

You need [elan](https://github.com/leanprover/elan) for the Lean toolchain, the .NET 10 SDK,
about 25 GB of disk, and roughly 40 minutes for the build. Nothing here is specific to my
machine.

```bash
# 1. Get the formalization and build it. Lean's own kernel checks it as it goes.
git clone https://github.com/openai/NavierStokesAndEuler
cd NavierStokesAndEuler
git checkout 8937a8f          # the commit checked below; omit for the latest
lake exe cache get            # downloads Mathlib's prebuilt .olean files
lake build                    # about 37 minutes on a 12-core laptop

# 2. Install Tenet and re-check the same build with a different kernel.
dotnet tool install -g tenet
tenet check . --all           # the whole closure: Mathlib, then Euler and Navier-Stokes

# 3. Ask what the headline theorems actually rest on.
tenet axioms .lake/build/lib/lean/Euler/Solution.olean   Euler.euler_breakdown_R3 Euler.exists_compact_smooth_euler_singularity
tenet axioms .lake/build/lib/lean/NavierStokes/ComparatorSolution.olean   NavierStokes.Comparator.navier_stokes_breakdown_R3   NavierStokes.Comparator.navier_stokes_breakdown_periodic
```

Expected output from step 2, give or take timing. It needs about 10 GB of memory:

```
OK: 850211 checked in 13068 modules, 0 failed, 13068 modules mapped, 679.5s, 12 jobs
```

and from step 3, for each of the four theorems, `propext`, `Classical.choice` and
`Quot.sound` and nothing else.

Drop `--all` for a faster run over the project's own modules only (91,178 declarations,
about 3.5 minutes), which trusts the imported constants instead of re-deriving them. And
`tenet show <module.olean> <name>` prints the exact statement of any theorem, which is the
thing worth reading before believing any of this.

If you get a different answer from the one above, I want to know: open an issue. A
disagreement is far more likely to be a bug in Tenet than a problem with the proof, and
that is exactly why a second checker is worth running.

Be precise about what that is worth. It says one more kernel, written from the type theory
rather than translated from Lean's code, follows every step of those proofs and agrees.
It says nothing about whether the theorem statements are the ones the
[Clay problem](https://www.claymath.org/millennium/navier-stokes-equation/) asks for;
reading a formal statement against an informal one is work for people, and it is where the
scrutiny of any formalization belongs. Tenet did not prove anything here, and finding a
disagreement would most likely have meant a bug in Tenet, which is how its last four were
found.

Tenet is not affiliated with the Lean FRO or Microsoft. "Lean" is the name of their
prover; this project only reads its export format.

## Tested against

| | Versions | How |
| --- | --- | --- |
| Lean | 4.35.0-rc1, 4.34.0, 4.33.1, 4.28.0, 4.24.0, 4.16.0, 4.12.0 | each toolchain's whole `Init` library checked in place, every push |
| .NET | 10 (ships), 11 RC1 (tracked) | built and tested on both; 10 is the active LTS |
| Platforms | Linux, macOS, Windows | build and test on every push |
| Mathlib | master, on Lean 4.35.0-rc1 | 766,950 declarations, 0 failures, checked nightly |

.NET 11 is tracked rather than targeted. It is a release candidate on the short-term support
track, and the one difference that mattered was a default rather than the runtime: it turns on
the GC's adaptive heap sizing, which costs this workload about 30% at twelve threads. That
setting is now pinned off, and with it pinned the two runtimes perform the same.

## Install

A release attaches a standalone binary for each platform: no .NET installation, about 4 MB, and
roughly 7 ms to start. Download it from the
[releases page](https://github.com/keithadler/tenet/releases), or:

```bash
dotnet tool install -g tenet          # needs the .NET SDK
brew install --build-from-source ./Formula/tenet.rb   # macOS, from a checkout
```

If `tenet` then answers **"You must install .NET to run this application"**, .NET is installed
somewhere the tool cannot find. A global .NET tool is a small native shim, and PATH only tells
your shell where to find that shim; it does not tell the shim where the runtime lives. The shim
looks at `DOTNET_ROOT` and at `/usr/local/share/dotnet`, and the official `dotnet-install.sh`
installs to `$HOME/.dotnet` instead, so this is the common case rather than an exotic one:

```bash
cat >> ~/.zprofile <<'EOF'
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
EOF
```

The standalone binary has no such problem: it carries its own runtime and needs nothing on the
machine.

Shell completions for bash and zsh are in `completions/`.

## Using it as a library

The kernel, the `.olean` reader and the export reader are published as ordinary packages, so a
project can read Lean's compiled output without Lean installed:

```bash
dotnet add package Tenet.Olean
```

```csharp
using var m = new OleanModule("Mathlib/Analysis/Complex/Basic.olean");
foreach (ConstantInfo c in m.DecodeAll())
    Console.WriteLine($"{c.KindName} {c.Name} : {c.Type}");
```

`samples/Tenet.Explorer` is a worked example: a hundred lines that turn a declaration into a page
showing what it says, what it rests on, and the chain to each assumption. It takes the packages
from nuget.org rather than by project reference, and CI builds it that way, so it doubles as a
standing check that what was published is still usable.


Building requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). What gets built runs
on .NET 8 or later: every project targets `net8.0` and `net10.0`, so the packages carry both and
you can reference them from a project that has not moved off .NET 8. Because there are two
targets, `dotnet run` and `dotnet publish` need `-f` to say which one.

```bash
git clone https://github.com/keithadler/tenet
cd tenet
dotnet build -c Release
dotnet run -c Release -f net10.0 --project src/Tenet.Cli -- check path/to/export.ndjson
```

The two builds decide the same thing, and CI runs the test suite and the CLI on both. .NET 10 is
the faster of them: checking all of `Init` takes 12.5s there against 16.5s on .NET 8 on the same
machine, so prefer it when you have the choice. The published standalone binaries are .NET 10.

Or as a global tool: `dotnet tool install -g tenet` (see **Install** above if it cannot find
.NET afterwards).

## Check a Lean project in place

Tenet reads Lean's compiled `.olean` files directly, so no export step is needed:

```bash
tenet check .lake/build/lib/lean/MyProject/Main.olean          # this module; imports are trusted
tenet check .lake/build/lib/lean/MyProject.olean --all         # the module and everything it imports
tenet check ~/.elan/toolchains/*/lib/lean/Init.olean --all     # the whole core library, about 8 seconds
```

Every module in the import closure is memory-mapped and its constants are decoded only when
the kernel looks them up, so memory stays proportional to the module being checked rather than
to everything it imports. Library roots are found from the Lake build tree around the target,
`LEAN_PATH`, and the elan toolchain matching the module's Lean version; add more with `--lib`.
`partial` and `unsafe` definitions are checked too, as the mutual blocks Lean added them as.

## Produce an export to check

```bash
git clone https://github.com/leanprover/lean4export
cd lean4export && lake build
# from inside a Lean project (or lean4export itself for the core library):
lake env .lake/build/bin/lean4export Init.Prelude > Init.Prelude.ndjson
lake env .lake/build/bin/lean4export MyProject.Main -- MyProject.mainTheorem > main.ndjson
```

Tenet reads export format 3.x (NDJSON), both the current 3.1 layout and the 3.0 layout.

## Commands

| | |
| --- | --- |
| `tenet check FILE` | check every declaration of an export, a `.olean` module, or a built Lake project directory; `--jobs N` (default: all cores), `--only a,b`, `--fail-fast`, `--low-memory`, `--report out.json`, `--stats`, `--slow SECONDS`, `--verbose`, `--quiet` |
| `tenet info FILE` | metadata and counts |
| `tenet why TARGET NAME` | the chain from a declaration to each assumption it rests on, module by module |
| `tenet crosscheck EXPORT OLEAN` | what the `.olean` reader decodes, against Lean's own exporter |
| `tenet compare A nameA B nameB` | are two separately built projects stating the same theorem? |
| `--sarif FILE` | on `check`: findings in SARIF, which GitHub code scanning renders on the diff |
| `--timing` | on `check`: where the time went, and worker utilization |
| `--fail-on-axiom NAME` | on `check`: exit non-zero if anything rests on that axiom, e.g. `sorryAx` |
| `--json` | on `axioms`, `audit`, `compare`, `crosscheck`, `statement` and `why`: machine-readable output instead of prose |
| `tenet audit DIR` | which of a project's declarations are complete and which rest on `sorry` |
| `tenet statement TARGET NAME...` | which constants a theorem's statement is built from, and which of them the project defines itself |
| `tenet axioms TARGET NAME...` | print the axioms a declaration depends on, transitively, as Lean's `#print axioms` does |
| `tenet names TARGET [PATTERN]...` | list the declarations a target defines, filtered by pattern; `--all` includes what it imports |
| `tenet show TARGET NAME...` | print declarations in full: type, value, hints, constructor and recursor data |

A `TARGET` is an export file, a single `Module.olean`, or a project directory. Given a directory,
every module under it is searched, so a declaration is found by its name and you do not have to know
which file it lives in: `tenet why mathlib/.lake/build/lib/lean Finset.sum_comm` answers in about
three seconds across 8,275 modules.

Checking streams: one thread parses while the others check, so all of `Init` takes about
seven seconds wall clock including parsing, and a declaration may only refer to constants
that precede it in the export, exactly as when Lean checked it.

Tenet caches failed definitional-equality checks, which the reference kernel does not, and
re-checks a rejected declaration without the cache so verdicts match Lean's exactly; set
`TENET_NO_FAILURE_CACHE=1` to run the reference algorithm alone (docs/design.md).

## Use it in a Lean project's CI

After `lake build`, one step re-checks every module the project compiled with an independent kernel:

```yaml
- uses: keithadler/tenet@main
  with:
    project: .          # a Lake project directory, a .olean file, or an export
    # all: "true"       # also check every imported module
    # args: --jobs 2
```

The action installs the released `tenet` tool, runs `tenet check`, and writes a summary with any
failing declarations to the job page. The same command works locally: `tenet check .` in a built project.

## Use the kernel as a library

`Tenet.Kernel` has no dependency on the export format. You build expressions, add
declarations to an `Environment`, and it throws a `KernelException` with a readable
message when something is wrong.

```csharp
using Tenet.Kernel;

var env = new Environment();
Name u = Name.Of("u");
// def id.{u} {α : Sort u} (a : α) : α := fun {α} a => a
Expr type  = Expr.Pi(Name.Of("α"), Expr.Sort(Level.Param(u)), Expr.Pi(Name.Of("a"), Expr.BVar(0), Expr.BVar(1)), BinderInfo.Implicit);
Expr value = Expr.Lam(Name.Of("α"), Expr.Sort(Level.Param(u)), Expr.Lam(Name.Of("a"), Expr.BVar(0), Expr.BVar(0)), BinderInfo.Implicit);
env.Add(new DefinitionDecl(Name.Of("id"), [u], type, value, ReducibilityHints.Abbrev, DefinitionSafety.Safe));

var tc = new TypeChecker(env);
Expr t = tc.Infer(Expr.Const(Name.Of("id"), [Level.One]));   // ∀ {α : Type}, α → α
```

`Tenet.Export` reads `.ndjson` exports into the same data structures and drives the
checker; `Tenet.Olean` memory-maps compiled modules and decodes constants on demand;
`Tenet.Cli` is the `tenet` command.

## Other checkers

Independent checking of Lean proofs is an established practice with several existing tools.
Tenet is another entry, not a first.

| | What it is | Catches a bug in Lean's kernel? |
| --- | --- | --- |
| [lean4checker](https://github.com/leanprover/lean4checker) | Official. Replays a module's environment through **Lean's own kernel** | No: it shares the kernel it is checking |
| [con-leche](https://github.com/leanprover/con-leche) | An external checker written in Lean, **proven in Lean not to accept a proof of `False`** | Yes, and it is the strongest guarantee of any of these |
| [con-ron](https://github.com/leanprover/con-ron) | A Rust port of con-leche, proven equivalent to it with [Aeneas](https://aeneasverif.github.io) | A different compiler and runtime, but proven identical in behavior, so not design diversity |
| [lean4lean](https://github.com/digama0/lean4lean) | A Lean 4 kernel written in Lean 4, aimed at being verified against the type theory | Yes, and it is the most rigorous of these |
| [nanoda](https://github.com/ammkrn/nanoda_lib) | An independent kernel in Rust; the checker [Comparator](https://github.com/leanprover/comparator) drives it | Yes |
| [trepplein](https://github.com/gebner/trepplein) | An independent kernel in Scala | Yes |
| Tenet | An independent kernel in C# on .NET | Yes |
| [gonzalgo](https://github.com/vince-gonzalez/gonzalgo) | Axiom provenance for Lean 4 and Metamath: which step introduced an axiom, how far it reaches, and which theorems are candidates for not needing it | Not its job; it explains rather than re-checks |

What Tenet adds is a second *implementation* on a different runtime, checked against Lean's
own kernel declaration by declaration on damaged inputs, plus two things aimed at using it
routinely: it reads compiled `.olean` files directly, so a project can be checked in place
without producing an export first, and it runs the whole of Mathlib in about six minutes.

`tenet why` overlaps with gonzalgo and arrived after it. Gonzalgo is the more complete answer
to that particular question: it covers Metamath as well as Lean, and it measures an axiom's
reach and flags theorems that look like candidates for not needing it, neither of which Tenet
does. Its author is careful about the limit, and so should this page be: flagging a candidate
is not a proof that the axiom was unnecessary. That is settled only by producing the
axiom-free proof, and on `set.mm` nine such have been merged.

Diversity is the point of all of these. Independent implementations only help if they are
genuinely independent, so the sensible thing is to run more than one.

## What "checking" means here

For each declaration in the export, in order:

- **axioms, definitions, theorems, opaques**: the type is a well-formed sort, the value's
  inferred type is definitionally equal to the stated type, theorems are propositions,
  universe parameters are declared and distinct, safe code does not use unsafe code.
- **inductive types**: Tenet is given only the types and constructors, as Lean's
  elaborator gives them to Lean's kernel. It checks positivity, universe constraints, and
  parameter uniformity, derives the recursors and their computation rules itself, and
  then compares every field of what it derived against what the exporter wrote. Nested
  inductives are translated to mutual ones and back.
- **quotients**: `Eq` must have exactly the expected shape; the four quotient constants are
  generated and compared against the export.

What it does not do: run compiled code. `Lean.reduceBool` and `Lean.reduceNat` are
rejected with a clear message, because trusting them means trusting the compiler.

### What you still have to trust

A checker moves trust, it does not remove it. Accepting a Tenet run means trusting:

- the .NET runtime and the C# compiler;
- `Tenet.Kernel`, about 3,500 lines, and whichever front end you used: the export reader,
  or the `.olean` reader, which decodes Lean's compiled object graph directly;
- that the input reflects what Lean actually checked. For an export, that is lean4export;
  for `.olean` files, that the files came from the build you think they did;
- that the theorem statements say what you believe they say. No kernel can help here.

### Why the checks are not vacuous

A checker that accepted everything would produce the same clean output, so:

- **It rejects tampered proofs.** Every theorem in the prelude given the previous theorem's
  proof: at least 95% must be rejected, with no collateral damage. Recursor rules and
  theorem statements are tampered with in the fixtures too.
- **Everything it accepts is put to [con-leche](https://github.com/leanprover/con-leche)**, whose
  `no_False_declaration` is a machine-checked theorem that it never accepts a file declaring a
  theorem of type `False`. Across five Mathlib slices, 312,904 distinct declarations, both
  checkers accepted every file in full. So Tenet accepted nothing in those corpora that would
  have made a proved checker reject, which is a different kind of evidence from two tested
  implementations agreeing. It is a statement about those corpora and not about the kernel;
  [docs/testing.md](docs/testing.md) sets out exactly what it does and does not license.
- **Its verdicts are compared with Lean's own kernel**, declaration by declaration, on about
  140,000 deliberately damaged declarations. That comparison has found two real kernel bugs,
  both in Tenet: a head comparison that used reference equality where the reference compares
  structurally, and a recursor walk that accepted only one binder shape.
- **The `.olean` reader is checked against Lean's own exporter**, constant by constant, over all
  `Init` and five Mathlib areas: 166,048 distinct constants compared, zero substantive differences. That is the one path the
  kernel comparison cannot reach, since a reader that drops a hypothesis yields a weaker theorem
  both kernels would accept.
- **Other methods found three more, also all in Tenet**: checking older toolchains found a
  hardcoded string-literal constant that is actually version dependent, fuzzing the `.olean`
  reader found a corruption path that threw the wrong exception, and profiling found an
  unbounded printer that could exhaust memory while formatting an error.
- **It derives rather than trusts.** Recursors and constructor metadata are re-derived from
  the types and constructors alone and compared field by field with what Lean wrote.
- **It is attacked on purpose, not only damaged at random.** Mutation takes a valid export and
  breaks it, which can only explore files that are nearly honest. The two worst bugs found in
  Tenet were not of that kind and neither was reachable by mutation: a numeric literal was typed
  `Nat` by assertion, so a file declaring `def Nat : Prop := False` and then `def boom : False := 3`
  was accepted with an empty axiom list, and the sixteen `Nat` operations were computed from the
  name alone, so a file declaring `Nat.add := fun a b => a` made the checker accept `2 + 2 = 4`
  and reject `2 + 2 = 2`. Both were found by reading what another checker checks, both were fixed
  in 0.9.0, and both have regression tests that reproduce them with the defense switched off.
  Every name the kernel hardcodes is now read out of its own source by a test and has to be
  accounted for, so a new assumption cannot be added without an attack being written for it.

### The question a kernel cannot answer

Checking says a proof is valid. It says nothing about whether the theorem means what you think, and
that is the question a mathematician actually has. `tenet statement` gets as close to it as a tool
can: it reports which constants a statement is built from, and which of those the project defined
itself, because a wrong definition hides there and nowhere else.

The contrast is the point:

| Theorem | constants in the statement | defined by the project |
| --- | --- | --- |
| `Nat.exists_infinite_primes` (Euclid) | 6 | **0** |
| `Polynomial.Monic.comp` | 9 | **0** |
| `Nat.Prime.factorization_pow` | 12 | **0** |
| `Finset.sum_range_succ` | 14 | **0** |
| `ConLeche.no_False_declaration` | 28 | **14** |

A Mathlib theorem's statement is built entirely from Mathlib and `Init`, so there is nothing bespoke
to audit: it means what the community's definitions mean, and those have been read by many people.
A result about a program, like con-leche's proof that it never accepts a file declaring `False`, is
half its author's own vocabulary. That is not a criticism, it is unavoidable for a theorem about a
specific artifact. But it locates the trust: fourteen definitions, named, and a reader who wants to
believe the theorem has to read those fourteen.

No kernel can do this for you. Tenet can tell you where to look.

### What this says about Lean

**Every bug this project has found has been in Tenet.** Two from the differential comparison with
Lean's kernel, three more from older toolchains, `.olean` fuzzing and profiling, three soundness
bugs from hostile files, and a reader that demanded more of the export format than the format
demands. Across all of Mathlib and its dependencies, about 140,000 deliberately damaged declarations
compared verdict by verdict against Lean's own kernel, 166,048 constants compared between the
`.olean` reader and Lean's exporter, six toolchains from 4.12 to 4.35, and a corpus written
specifically to attack a checker, **no bug has been found in Lean's kernel.**

That is worth stating plainly because it is a statement about Lean rather than about Tenet, and it
is the kind of thing a project cannot credibly say about itself. It is also bounded, and the bounds
matter more than the headline. An independent *implementation* can only catch a defect in the
reference's code; a defect in its *design* is one Tenet would likely reproduce, since matching the
reference kernel's decisions was the goal and its structure was followed deliberately. Tenet is
tested, not verified, so this is evidence and not proof. And the places Tenet decides differently on
purpose are listed in [docs/divergences.md](docs/divergences.md), where three of the six entries
exist precisely because Lean can safely assume something Tenet cannot.

Tenet aims to decide exactly what Lean's kernel decides. [docs/specification.md](docs/specification.md) is the
correspondence, rule by rule: the judgment each one implements, where it lives here, and where it lives in Lean,
so that the agreement can be checked rather than believed. The places Tenet deliberately decides differently are
in [docs/divergences.md](docs/divergences.md); anywhere else, a difference is a bug.

See [docs/status.md](docs/status.md) for the runs and [docs/testing.md](docs/testing.md) for
the method.

## Layout

```
src/Tenet.Kernel     the kernel (no dependencies)
src/Tenet.Export     export reader and check driver
src/Tenet.Olean      .olean reader (memory-mapped, lazy) and in-place checker
src/Tenet.Cli        the tenet command
tests/Tenet.Tests    xunit tests; large-export tests run when TENET_EXPORTS is set
tests/fixtures       small committed exports
docs/                design notes, status, testing, divergences, specification
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). The one rule that matters most: the kernel must
never accept something Lean rejects. Being stricter than Lean is a bug too, but a
recoverable one.

## License

Dual-licensed under either the [MIT License](LICENSE-MIT) or the
[Apache License, Version 2.0](LICENSE-APACHE), at your option. This mirrors the licenses
of the two ecosystems Tenet lives between: .NET (MIT) and Lean 4 (Apache-2.0).
Contributions are accepted under the same terms.
