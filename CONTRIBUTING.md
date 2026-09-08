# Contributing to Tenet

Thanks for looking. Tenet is a kernel, so the bar is soundness first, everything else second.

## Ground rules

1. **Never accept what Lean rejects.** A change that makes the checker more permissive needs
   a written argument for why the reference kernel accepts the same thing, with a pointer to
   the corresponding rule. When in doubt, be strict and open an issue.
2. **Write it, don't translate it.** Read the Lean 4 kernel sources (Apache-2.0) to learn the
   behavior, then write C#. Do not paste C++ or Lean code into this repository.
3. **Every reduction rule gets a test.** Small hand-built expressions in `tests/Tenet.Tests`,
   plus, for anything touching inductives or definitional equality, a run over
   `Init.Prelude` (see `docs/testing.md`).
4. **Keep the kernel dependency-free.** `Tenet.Kernel` references nothing outside the BCL.

## Workflow

```bash
dotnet build
dotnet test
TENET_EXPORTS=/path/to/exports dotnet test    # runs the large-export tests too
```

Pull requests should describe what rule or behavior changed and how it was verified.
Warnings are errors; the analyzers are configured in `.editorconfig`.

## Reporting a soundness bug

If Tenet accepts a declaration that Lean rejects, or derives a recursor different from
Lean's, that is the most important kind of report. Please include the smallest `.ndjson`
you can produce that shows it, and the Lean version that produced it.

## Licensing of contributions

By contributing you agree that your contributions are licensed under both the MIT License
and the Apache License 2.0, like the rest of the project.
