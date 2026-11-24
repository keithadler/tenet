/-! A declaration that Lean 4.36 accepts and Lean 4.37 rejects: it needs a proof reduced to a constructor. -/
namespace Acc436

set_option linter.defProp false in
def accProof : Acc (fun _ _ : Nat => False) 0 := Acc.intro 0 fun _ h => h.elim

noncomputable def viaAcc : Nat := Acc.rec (motive := fun _ _ => Nat) (fun _ _ _ => 1) accProof

theorem accReduces : viaAcc = 1 := rfl

end Acc436
