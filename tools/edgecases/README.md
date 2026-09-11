# Kernel edge-case corpus

A small Lean library whose declarations are dense in the kernel rules a general export
exercises only rarely: structure eta, proof irrelevance, K-like reduction, quotient
reduction, nested and mutual inductives, literal arithmetic at the word boundaries, and
universe polymorphism.

It exists for two jobs.

**A baseline.** Both kernels should accept every declaration. They do, which is evidence
that Tenet implements these rules the way the reference does, and it is the only direct
check of several of them the project has.

```sh
lake build
lake env ../../../lean4export/.lake/build/bin/lean4export EdgeCases.Cases > EdgeCases.ndjson
tenet check EdgeCases.ndjson
leancheck EdgeCases.ndjson
```

**A mutation target.** Damaging a general library mostly hits ordinary application and
binder structure, because that is what libraries are made of. Damaging these declarations
hits the rules above on nearly every edit.

One caveat worth knowing: `lean4export` writes the whole transitive closure, so this corpus
is a sliver at the end of a very large file and random mutation almost never lands on it.
`Tenet.DiffTest --tail N` restricts mutation to the last N lines for exactly that reason.

Every declaration is proved by `rfl` where possible, so the proof term forces the kernel
down the reduction path being tested instead of through a lemma that hides it. The one
exception is noted in the source: a function over a nested inductive compiles to
well-founded recursion, which the kernel does not unfold on its own.
