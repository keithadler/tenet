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
independent kernels is a proof you can trust a little more.

```
$ tenet check .lake/build/lib/lean/Mathlib.olean --all
Mathlib: Lean 4.34.0-rc2 (6a10ac8c2); 10726 modules mapped in 1.5s, checking all of them
OK: 765497 checked in 10726 modules, 0 failed, 10726 modules mapped, 390.5s, 12 jobs
```

## Status

Version 0.2. Every rule of the reference kernel has a counterpart here. Tenet checks all
of Mathlib and its dependencies, 765,497 declarations in 10,726 modules, directly from the
compiled `.olean` files in six and a half minutes on a laptop, with zero failures, deriving
the recursors itself and comparing them field for field against Lean's own. Its verdicts
have been compared with Lean's kernel on about 100,000 deliberately damaged declarations.
See [docs/status.md](docs/status.md) for what has been run and how the checks were shown
not to be vacuous.

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
| `tenet show FILE NAME...` | print declarations: type, value, hints, constructor and recursor data |

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
