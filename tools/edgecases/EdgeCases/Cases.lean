/-!
A corpus dense in the kernel rules that a general export exercises only rarely: structure eta,
proof irrelevance, K-like reduction, quotient reduction, nested and mutual inductives, literal
arithmetic, and universe polymorphism.

Its purpose is to be *mutated*. Damaging a general library mostly hits ordinary application and
binder structure, because that is what libraries are made of. Damaging this file hits the rules
above on nearly every edit, which is what a targeted differential campaign needs.

Every declaration here is proved by `rfl` wherever possible, so the proof term forces the kernel
down the reduction path being tested rather than through a lemma that hides it.
-/

namespace EdgeCases

/-! ## Structure eta: a structure value equals its own field-by-field rebuild. -/

structure Pair (α β : Type) where
  fst : α
  snd : β

theorem pair_eta (p : Pair Nat Bool) : p = ⟨p.fst, p.snd⟩ := rfl
theorem pair_eta_nested (p : Pair (Pair Nat Nat) Bool) :
    p = ⟨⟨p.fst.fst, p.fst.snd⟩, p.snd⟩ := rfl

structure OneField where
  only : Nat

theorem onefield_eta (x : OneField) : x = ⟨x.only⟩ := rfl

/-! ## Proof irrelevance: any two proofs of the same proposition are equal. -/

theorem irrel (p : Prop) (h₁ h₂ : p) : h₁ = h₂ := rfl
theorem irrel_under_binder (p : Prop) (f : p → Nat) (h₁ h₂ : p) : f h₁ = f h₂ := rfl

/-! ## K-like reduction: eliminating a proposition with one constructor and no fields. -/

theorem k_true (h : True) : h = True.intro := rfl
theorem k_eq_rec {α : Type} (a : α) (h : a = a) : h = rfl := rfl
theorem k_and (p q : Prop) (h : p ∧ q) : h = ⟨h.1, h.2⟩ := rfl

/-! ## Quotients: `Quot.lift` applied to `Quot.mk` must reduce. -/

private def parity (a b : Nat) : Prop := a % 2 = b % 2

def Par := Quot parity

def parOf (n : Nat) : Par := Quot.mk parity n

theorem quot_lift_reduces (n : Nat) :
    Quot.lift (fun x => x % 2) (fun _ _ h => h) (parOf n) = n % 2 := rfl

theorem quot_ind_reduces (n : Nat) : (Quot.mk parity n) = parOf n := rfl

/-! ## Nested and mutual inductives: recursors derived rather than stored. -/

inductive Rose where
  | node : List Rose → Rose

def Rose.size : Rose → Nat
  | .node cs => 1 + (cs.map Rose.size).foldl (· + ·) 0

-- Not `rfl`: a function over a nested inductive compiles to well-founded recursion, which the
-- kernel does not unfold on its own. The nested inductive itself is what this section is for,
-- since its recursor is derived rather than stored.
theorem rose_leaf_size : (Rose.node []).size = 1 := by simp [Rose.size]

mutual
  inductive Ev : Nat → Prop where
    | zero : Ev 0
    | fromOd : ∀ n, Od n → Ev (n + 1)
  inductive Od : Nat → Prop where
    | fromEv : ∀ n, Ev n → Od (n + 1)
end

theorem ev_two : Ev 2 := .fromOd 1 (.fromEv 0 .zero)

/-! ## Literal arithmetic, including the boundaries where a fast path gives way to big numbers. -/

theorem lit_pow : (2 : Nat) ^ 10 = 1024 := rfl
theorem lit_gcd : Nat.gcd 1071 462 = 21 := rfl
theorem lit_mod : 123456789 % 1000 = 789 := rfl
theorem lit_word : (2 : Nat) ^ 63 = 9223372036854775808 := rfl
theorem lit_over_word : (2 : Nat) ^ 64 = 18446744073709551616 := rfl
theorem lit_shift : Nat.shiftLeft 1 64 = 18446744073709551616 := rfl
theorem lit_sub_floor : 3 - 5 = 0 := rfl
theorem lit_div_zero : 7 / 0 = 0 := rfl
theorem lit_str_len : "abc".length = 3 := rfl
theorem lit_str_append : "ab" ++ "c" = "abc" := rfl

/-! ## Universe polymorphism, where level normalization decides equality. -/

universe u v

def idu (α : Sort u) (a : α) : α := a

theorem idu_reduces : idu Nat 3 = 3 := rfl

def constu (α : Sort u) (β : Sort v) (a : α) (_ : β) : α := a

theorem constu_reduces : constu Nat Bool 5 true = 5 := rfl

/-! ## Definitional unfolding through several layers, which drives lazy delta reduction. -/

def layer0 : Nat := 7
def layer1 : Nat := layer0 + 1
def layer2 : Nat := layer1 * 2
def layer3 : Nat := layer2 - 3

theorem layers_reduce : layer3 = 13 := rfl

end EdgeCases
