/-
`S9` asked about from a module that imports it rather than the one that defines it.

Lean gives a different answer in the two places, and the answer here is the wrong one: `S9` rests on
`Classical.choice` through its constructor's field type, as the defining module correctly reports.
This is leanprover/lean4#15226. `collectAxioms` caches one axiom set per constant, and the sentinel
it inserts to break the inductive/constructor cycle cannot be told apart from a finished result, so
the inductive gets cached with whatever had been collected when the constructor's walk reached it.

Tenet keeps one accumulator for the whole walk and caches nothing per constant, so the module a
declaration is read from cannot change its answer.
-/

import Minimal

#print axioms S9
#print axioms S9.mk
