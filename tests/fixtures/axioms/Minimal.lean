/-
The reproduction from leanprover/lean4#15226, unchanged, and the reason it is a separate module.

The bug depends on the order in which a module's constant map is walked during export, so it does
not fire for every module that has the shape. `Axioms.lean` has the same structure and Lean answers
it correctly from an importing module; this one, which is the issue's own file, does not. Keeping
the reproduction exactly as reported is what makes it reproduce.
-/

noncomputable def pick : Nat := Classical.choice ⟨0⟩

structure S9 where
  x : Fin (pick + 1)

#print axioms S9
