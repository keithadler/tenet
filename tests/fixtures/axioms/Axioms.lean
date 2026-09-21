/-
The declarations behind `AxiomCollectionTests`, in the language the answer is defined by.

Each one is a way for an assumption to sit underneath a declaration without appearing in its type.
`compare.sh` builds this file, reads what Lean says about each name, asks Tenet the same question
about the compiled module, and fails on any disagreement. The expectations in the C# tests are
transcriptions of what Lean prints here; this file is what keeps the transcription honest.
-/

noncomputable def pick : Nat := Classical.choice ⟨0⟩

/-- An axiom reachable only through a constructor's field type. `ViaCtor`'s own type is `Type`. -/
structure ViaCtor where
  x : Fin (pick + 1)

/-- An axiom whose own statement rests on another axiom. -/
axiom viaAxiomType : Fin (pick + 1)

/-- The same, one step removed, which already worked: the walk reaches `viaAxiomType` through a value. -/
noncomputable def usesThatAxiom : Fin (pick + 1) := viaAxiomType

/-- A hole behind a field type. This compiles, and the point of `audit` is to say that it should not count. -/
def holed : Nat := sorry

structure SorryViaCtor where
  y : Fin (holed + 1)

/-- Not a structure, and not the first constructor, so nothing here depends on a one-field shape. -/
inductive ViaLaterCtor where
  | a : ViaLaterCtor
  | b : Fin (pick + 1) → ViaLaterCtor

/-- The other half. Without a case that must come back empty, a walk that reported everything would pass. -/
inductive Clean where
  | mk : Clean

/-- `Quot.sound` is an axiom in its own right and has to keep reporting as one. -/
theorem usesSound {α} (r : α → α → Prop) (a b : α) (h : r a b) :
    Quot.mk r a = Quot.mk r b := Quot.sound h

#print axioms ViaCtor
#print axioms viaAxiomType
#print axioms usesThatAxiom
#print axioms SorryViaCtor
#print axioms ViaLaterCtor
#print axioms Clean
#print axioms usesSound
