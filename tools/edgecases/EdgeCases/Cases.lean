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

/-! ## K-like reduction: eliminating a proposition with one constructor and no fields.

Counting which rules the kernel actually reaches (`tenet check . --rules`) said the three
theorems below never reach it. They are true and they are checked, but a proof compared against
another proof of the same proposition is settled by proof irrelevance before any recursor is
reduced, so what they exercise is `DefEqProofIrrel`. They are kept, under their real name.

Forcing K-like reduction takes a motive that lands in `Type` rather than `Prop`, so that proof
irrelevance cannot settle the comparison and the kernel has to turn the variable `h : a = a`
into `Eq.refl` to reduce the recursor.
-/

theorem irrel_true (h : True) : h = True.intro := rfl
theorem irrel_eq_rec {α : Type} (a : α) (h : a = a) : h = rfl := rfl
theorem irrel_and (p q : Prop) (h : p ∧ q) : h = ⟨h.1, h.2⟩ := rfl

theorem k_eq_rec_type {α : Type} {a : α} (motive : (x : α) → a = x → Type)
    (m : motive a rfl) (h : a = a) : @Eq.rec α a motive m a h = m := rfl

theorem k_eq_rec_nat {α : Type} {a : α} (f : α → Nat) (h : a = a) :
    @Eq.rec α a (fun x _ => Nat) (f a) a h = f a := rfl

/-! ## Function eta: `f` and `fun x => f x` are the same function.

Nothing else in this file made the kernel compare a variable against its own eta expansion.
-/

theorem eta_fun (f : Nat → Nat) : f = fun x => f x := rfl
theorem eta_fun_dep {α : Type} (P : α → Type) (f : (x : α) → P x) : f = fun x => f x := rfl
theorem eta_fun_two (f : Nat → Nat → Nat) : f = fun x y => f x y := rfl

/-! ## Unit-like eta: any two elements of a one-constructor, no-field type are equal.

This is not proof irrelevance: `Unit` is in `Type`, so the two sides are data, and the rule that
settles them is the unit-like case of definitional equality.
-/

theorem unit_like (a b : Unit) : a = b := rfl
theorem punit_like (a b : PUnit.{u+1}) : a = b := rfl

structure NoFields where

theorem nofields_like (a b : NoFields) : a = b := rfl

/-! ## String literals against their constructor form.

A literal and the `String` value built from its character list are the same string, and deciding
that is its own rule in the kernel. No declaration in all of `Init` reaches it.
-/

-- These do not reach the definitional-equality rule for string literals, and nothing else does
-- either: `DefEqStringLit` is at zero on Init for Lean 4.12, 4.24 and 4.34 alike. Tenet keys that
-- rule on `Environment.StringLiteralConstructor`, which is `String.ofList` where it exists, and
-- `String.ofList` is an ordinary definition, so lazy delta unfolds it before the rule is reached.
-- What these do exercise is the whnf side, `StringLitToCtor`, which went from 5 firings to 11.
theorem strlit_ctor : "ab" = String.ofList ['a', 'b'] := rfl
theorem strlit_empty : "" = String.ofList [] := rfl
theorem strlit_ctor_rev : String.ofList ['h', 'i'] = "hi" := rfl
theorem strlit_length_via_list : (String.ofList ['a', 'b', 'c']).length = 3 := rfl

/-! ## Quotients: `Quot.lift` applied to `Quot.mk` must reduce. -/

private def parity (a b : Nat) : Prop := a % 2 = b % 2

def Par := Quot parity

def parOf (n : Nat) : Par := Quot.mk parity n

theorem quot_lift_reduces (n : Nat) :
    Quot.lift (fun x => x % 2) (fun _ _ h => h) (parOf n) = n % 2 := rfl

-- Not a `Quot.ind` reduction: it unfolds `parOf` and compares two `Quot.mk` applications.
theorem quot_mk_unfolds (n : Nat) : (Quot.mk parity n) = parOf n := rfl

/-- `Quot.ind` applied to `Quot.mk`, which the kernel must actually reduce.

Both sides are proofs of a proposition, so it looks as though proof irrelevance should settle it
without reducing anything. It does not, because `IsDefEqCore` puts both sides through `whnfCore`
before it consults proof irrelevance, and the recursor application reduces there. This shape is
the only one in all of `Init` that reaches the rule: bisecting the 64,658 declarations found
exactly one witness, `Quot.indBeta`, which is the library's own statement of this reduction. -/
theorem quot_ind_reduces {motive : Quot parity → Prop}
    (p : ∀ a : Nat, motive (Quot.mk parity a)) (n : Nat) :
    @Quot.ind Nat parity motive p (Quot.mk parity n) = p n := rfl

/-- `Quot.lift` under a further reduction, so the lift is not the outermost redex. -/
theorem quot_lift_nested (n : Nat) :
    (Quot.lift (fun x => x % 2) (fun _ _ h => h) (parOf n)) + 0 = n % 2 := rfl

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

/-! ## `let` in a term, which the kernel must both type and reduce.

Counting said the corpus never reached `InferLet` or the let-bound free variable in the local
context, though `Init` reaches both constantly. Nothing here had a `let` in it.
-/

theorem let_reduces : (let x := 2; x + x) = 4 := rfl
theorem let_nested : (let x := 2; let y := x + 1; y * y) = 9 := rfl
theorem let_under_lambda : (fun n : Nat => let d := n + n; d + d) 2 = 8 := rfl
theorem let_dependent : (let α := Nat; (3 : α)) = 3 := rfl

/-! ## Sorts and constants compared at levels that are equal without being identical.

`max u 0` and `u` are the same universe written two ways, so deciding these goes through level
equivalence rather than through syntactic equality.
-/

theorem sort_max_zero : (Sort (max u 0)) = (Sort u) := rfl
theorem sort_max_comm : (Sort (max u v)) = (Sort (max v u)) := rfl
-- A definition is unfolded by lazy delta before the constant rule is reached, and a constant
-- with arguments goes through application congruence, so reaching `DefEqConst` takes a bare
-- constant that cannot be unfolded: an inductive type at two spellings of one level.
inductive Bare.{w} : Sort (w + 1) where
  | mk

theorem const_levels_bare : Bare.{max u 0} = Bare.{u} := rfl
theorem const_levels_bare_comm : Bare.{max u v} = Bare.{max v u} := rfl
theorem const_levels_equal : @idu (Sort (max u 0)) = @idu (Sort u) := rfl

/-! ## One theorem stated two ways, and a near miss.

`tenet compare` exists for the case where two projects formalize the same claim independently.
These three are the test of it: the first two say the same thing through different definitions,
the third differs by one number and must not be mistaken for either.
-/

def twoAlias : Nat := 2

theorem sum_direct : 2 + 2 = 4 := rfl
theorem sum_via_alias : twoAlias + 2 = 4 := rfl
theorem sum_near_miss : 2 + 3 = 5 := rfl

/-- Same statement as `sum_direct`, differing only in how the binder is named. -/
theorem addzero_n : ∀ n : Nat, n + 0 = n := fun n => rfl
theorem addzero_m : ∀ m : Nat, m + 0 = m := fun m => rfl

end EdgeCases
