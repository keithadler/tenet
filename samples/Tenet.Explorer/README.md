# Tenet.Explorer

A small console app that turns a Lean declaration into a page: what it says, what it rests on, and
the chain from it to each assumption. The questions `tenet why` and `tenet axioms` answer on a
terminal, laid out so the shape of a proof's provenance is visible at a glance.

```bash
dotnet run -- <target> <declaration> [-o out.html]
```

```bash
# a theorem from Lean's own library, read straight out of the compiled module
dotnet run -- "$(lake env printenv LEAN_SYSROOT)/lib/lean/Init.olean" Nat.add_comm

# a project of your own, pointing at the build tree rather than one module
dotnet run -- .lake/build/lib/lean MyProject.main_theorem -o provenance.html
```

Lean does not need to be installed. The `.olean` file is read directly.

## Why it is here

It exists to show that the packages on nuget.org are usable from an ordinary project, so it takes
`Tenet.Kernel` and `Tenet.Olean` as **package references rather than project references**, and it
is deliberately not in `Tenet.slnx`. A project reference would prove nothing about what was
published. CI builds it separately against the published packages, which makes it a standing check
that what is on nuget.org still works as a library.

That also means it pins a version. When the version is bumped, this pin moves after the release,
not before, or CI will look for a package that does not exist yet.

## What it uses

Nothing private. `OleanModule` and `OleanChecker` to read a module and its imports, `Replay.AxiomsOf`
for the transitive axiom closure, `Replay.PathTo` for the shortest chain to an assumption, and
`ExprPrinter` for the statement. Roughly two hundred lines, most of them HTML.

## Opening it in Visual Studio

`File → Open → Project/Solution` on `Tenet.Explorer.csproj`. The project targets .NET 10. Symbols
and SourceLink are published with the packages, so stepping into `Replay.AxiomsOf` shows the real
source rather than decompiled IL.
