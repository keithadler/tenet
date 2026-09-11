# Tenet

**An independent implementation of the Lean 4 kernel on .NET, and a checker for Lean exports.**

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
Mathlib: Lean 4.34.0-rc2 (6a10ac8c2); 10726 modules mapped in 1.5s, checking all of them
OK: 765497 checked in 10726 modules, 0 failed, 10726 modules mapped, 390.5s, 12 jobs
```

## Status

Version 0.6. Every rule of the reference kernel has a counterpart here. Tenet checks all
of Mathlib and its dependencies, 765,497 declarations in 10,726 modules, directly from the
compiled `.olean` files in about six minutes on a laptop, with zero failures, deriving
the recursors itself and comparing them field for field against Lean's own. Its verdicts
have been compared with Lean's kernel on about 130,000 deliberately damaged declarations.
See [docs/status.md](docs/status.md) for what has been run and how the checks were shown
not to be vacuous.

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

#### The other answer: a formalization in progress

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

## Install

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/keithadler/tenet
cd tenet
dotnet build -c Release
dotnet run -c Release --project src/Tenet.Cli -- check path/to/export.ndjson
```

Or as a global tool once published: `dotnet tool install -g tenet`.

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
| `tenet why FILE.olean NAME` | the chain from a declaration to each assumption it rests on, module by module |
| `tenet crosscheck EXPORT OLEAN` | what the `.olean` reader decodes, against Lean's own exporter |
| `tenet compare A.olean nameA B.olean nameB` | are two separately built projects stating the same theorem? |
| `tenet audit DIR` | which of a project's declarations are complete and which rest on `sorry` |
| `tenet statement FILE.olean NAME...` | which constants a theorem's statement is built from, and which of them the project defines itself |
| `tenet axioms FILE NAME...` | print the axioms a declaration depends on, transitively, as Lean's `#print axioms` does |
| `tenet show FILE NAME...` | print declarations from an export or a compiled module: type, value, hints, constructor and recursor data |

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
- **Its verdicts are compared with Lean's own kernel**, declaration by declaration, on about
  140,000 deliberately damaged declarations. That comparison has found two real kernel bugs,
  both in Tenet: a head comparison that used reference equality where the reference compares
  structurally, and a recursor walk that accepted only one binder shape.
- **The `.olean` reader is checked against Lean's own exporter**, constant by constant, over all
  648 modules of `Init` and a Mathlib slice: 98,463 compared, zero substantive differences. That is the one path the
  kernel comparison cannot reach, since a reader that drops a hypothesis yields a weaker theorem
  both kernels would accept.
- **Other methods found three more, also all in Tenet**: checking older toolchains found a
  hardcoded string-literal constant that is actually version dependent, fuzzing the `.olean`
  reader found a corruption path that threw the wrong exception, and profiling found an
  unbounded printer that could exhaust memory while formatting an error. No bug in Lean's
  kernel has been found.
- **It derives rather than trusts.** Recursors and constructor metadata are re-derived from
  the types and constructors alone and compared field by field with what Lean wrote.

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
docs/                design notes, status, testing
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
