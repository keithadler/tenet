# Kernel edge-case corpus

A small Lean library whose declarations are dense in the kernel rules a general export
exercises only rarely: structure eta, proof irrelevance, K-like reduction, quotient
reduction, nested and mutual inductives, literal arithmetic at the word boundaries, and
universe polymorphism.

It exists for two jobs.

**A baseline.** Both kernels should accept every declaration. They do.

`tenet check . --rules` says how much that is worth. The first time it was run against this
corpus the answer was 25 of the kernel's 36 rules, with K-like reduction, `Quot.ind`, function
eta and unit-like eta among the ones never reached: four of the rules this file was written for.
Writing a declaration that mentions a rule is not the same as making the kernel use it while
checking that declaration.

The cases were rewritten against the measurement and it now reaches 33 of 36, the same as all of
`Init`, with the same three left over: `InferBVar` and `NativeReduce`, which are meant to be
unreachable, and `DefEqStringLit`, which no corpus on any Lean version has reached. Keep the
measurement honest when adding a case: run `--rules` before and after and check that the rule you
meant to exercise moved.

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
