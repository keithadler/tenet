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
$ tenet check Mathlib.Data.Real.Basic.ndjson
Mathlib.Data.Real.Basic.ndjson: 179215 declarations, 12858402 expressions, 1004563 names, 2230 levels (parsed in 11.1s)
  exported by lean4export 3.1.0, format 3.1.0, Lean 4.34.0-rc2 (6a10ac8c2)
OK: 179215 checked, 0 failed, 0 skipped, 186458 constants, 9.8s, 12 jobs
```

## Status

Version 0.1. Every rule of the reference kernel has a counterpart here. Tenet checks the
whole Lean 4 core library (`Init`, 58,135 declarations) and 657,351 declarations of
Mathlib (everything the exporter managed to write on a laptop) with zero failures,
deriving the recursors itself and comparing them field for field against Lean's own. It checks in parallel; all
of `Init` takes about six seconds on a laptop. See [docs/status.md](docs/status.md) for
what has been run, and for the evidence that the checks are not vacuous.

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
| `tenet check FILE` | check every declaration; `--jobs N` (default: all cores), `--only a,b`, `--fail-fast`, `--low-memory`, `--report out.json`, `--stats`, `--slow SECONDS`, `--quiet` |
| `tenet info FILE` | metadata and counts |
| `tenet show FILE NAME...` | print declarations: type, value, hints, constructor and recursor data |

Checking streams: one thread parses while the others check, so all of `Init` takes about
seven seconds wall clock including parsing, and a declaration may only refer to constants
that precede it in the export, exactly as when Lean checked it.

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
checker; `Tenet.Cli` is the `tenet` command.

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
