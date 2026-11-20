/-! Declarations that Lean 4.37 accepts and Lean 4.36 rejects. -/
namespace Eta437

/-- lean4#15371: the major premise `x` of `And.rec` is eta-expanded. -/
theorem andMajor (P Q : Prop) (A : Type) (f : P → Q → A) (x : P ∧ Q) :
    (And.rec f ⟨x.1, x.2⟩ : A) = And.rec f x := rfl

structure T where
  val : Bool
  proof : True

/-- lean4#15373: structure eta against the partially applied constructor `T.mk b`. -/
theorem partialCtor (x : True → T) : (T.mk (x True.intro).val) = x := rfl

structure Box where
  val : Bool

/-- A carrier for the type `f = Box.mk`, which no `rfl` proves. -/
theorem boxType (f : Bool → Box) (h : f = Box.mk) : f = Box.mk := h
/-- A carrier for the value `fun f => Eq.refl f`. -/
theorem boxValue (f : Bool → Box) : f = f := Eq.refl f

end Eta437
