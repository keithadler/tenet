# What the kernel decides, rule by rule

Tenet aims to decide exactly what Lean's kernel decides. This is the correspondence: each rule the kernel applies,
the judgment it implements, where it lives here, and where it lives in Lean.

It exists so that agreement can be checked rather than believed. Running both kernels over a corpus shows they
answered the same; it does not show that they answer the same way, or that a rule one has is a rule the other has.
A reader who wants to audit this project against the reference can work down this table with both sources open.

The Lean column is against `src/kernel/type_checker.cpp` at `v4.34.0` unless noted. "inline" means Lean handles
that case in the dispatcher without a function of its own, which is worth knowing before going to look for one.

The counts come from `tenet check --rules`, which reports how often each rule fired and which never did; see
[testing.md](testing.md) for what that measurement covers and what it does not. Where Tenet deliberately decides
differently, [divergences.md](divergences.md) says so and why.

## Typing

| Rule | Judgment | Tenet | Lean |
| --- | --- | --- | --- |
| `InferBVar` | A loose bound variable reaching the checker, which a well-formed term never does. The error path. | `InferTypeCore` | `infer_type_core`<br>inline: a loose bvar is a kernel error in both |
| `InferFVar` | Γ, x : A ⊢ x : A. The local context holds the type. | `InferTypeCore` | `infer_fvar` |
| `InferSort` | ⊢ Sort u : Sort (u+1). | `InferTypeCore` | `infer_type_core`<br>inline; there is no infer_sort |
| `InferConst` | ⊢ c.{v̄} : T[ū := v̄] for c : T declared with universe parameters ū. | `InferTypeCore` | `infer_constant` |
| `InferApp` | Γ ⊢ f : (x : A) → B, Γ ⊢ a : A' with A ≡ A', therefore Γ ⊢ f a : B[x := a]. | `InferTypeCore` | `infer_app` |
| `InferLam` | Γ, x : A ⊢ b : B therefore Γ ⊢ (fun x : A => b) : (x : A) → B. | `InferTypeCore` | `infer_lambda` |
| `InferPi` | Γ ⊢ A : Sort u, Γ, x : A ⊢ B : Sort v, therefore Γ ⊢ ((x : A) → B) : Sort (imax u v). | `InferTypeCore` | `infer_pi` |
| `InferLet` | Γ ⊢ v : A, Γ, x : A := v ⊢ b : B, therefore Γ ⊢ (let x : A := v; b) : B[x := v]. | `InferTypeCore` | `infer_let` |
| `InferProj` | Γ ⊢ e : S ā for a structure S, therefore Γ ⊢ e.i : the i-th field type, earlier fields substituted. | `InferTypeCore` | `infer_proj` |
| `InferLit` | A natural number literal has type Nat, a string literal has type String. | `InferTypeCore` | `infer_lit` |

## Reduction

| Rule | Judgment | Tenet | Lean |
| --- | --- | --- | --- |
| `Beta` | (fun x => b) a ↝ b[x := a]. | `WhnfCore` | `whnf_core`<br>the App case; Lean names no beta function, it instantiates |
| `Zeta` | (let x := v; b) ↝ b[x := v]. | `WhnfCore` | `whnf_core`<br>the Let case |
| `ZetaFVar` | x ↝ v for a let-bound x := v in the local context. | `WhnfFVar` | `whnf_fvar` |
| `Delta` | c.{v̄} ↝ its value, for a definition c, during weak head normalization. | `Whnf` | `whnf` |
| `DeltaLazy` | The same unfolding during lazy delta reduction, where only the side that needs it is unfolded. | `LazyDeltaReductionStep` | `lazy_delta_reduction_step` |
| `Iota` | I.rec ... (I.ctor_i ā) ↝ the i-th minor premise applied to ā and the recursive results. | `Inductive.TryReduceRec` | `inductive_reduce_rec` |
| `IotaK` | K-like reduction. For an inductive proposition with one constructor and no fields, a major premise whose type is that proposition is replaced by the constructor even when it is a variable, so iota can proceed. | `Inductive.TryReduceRec` | `inductive_reduce_rec`<br>eligibility is computed by init_K_target in inductive.cpp |
| `Proj` | (S.mk ā).i ↝ aᵢ. | `WhnfCore` | `reduce_proj` |
| `QuotLift` | Quot.lift f h (Quot.mk r a) ↝ f a. | `Quot.TryReduceRec` | `quot_reduce_rec` |
| `QuotInd` | Quot.ind p (Quot.mk r a) ↝ p a. | `Quot.TryReduceRec` | `quot_reduce_rec` |
| `NatLitOp` | An arithmetic operation on Nat literals computed directly: add, sub, mul, div, mod, pow, gcd, and the bitwise ones. | `ReduceBinNatOp` | `reduce_nat` |
| `NatLitPred` | A predicate on Nat literals computed directly: decEq, beq, ble. | `ReduceBinNatPred` | `reduce_nat` |
| `StringLitToCtor` | A string literal expanded into its constructor form over a list of characters. | `ReduceProjCore` | `string_lit_to_constructor` |
| `NativeReduce` | Lean.reduceBool and Lean.reduceNat, which would require running compiled code. Tenet refuses rather than trusting it, so reaching this rule is a rejection and not a reduction. | `ReduceNative` | `reduce_native`<br>Lean evaluates; Tenet refuses (see divergences.md) |

## Definitional equality

| Rule | Judgment | Tenet | Lean |
| --- | --- | --- | --- |
| `DefEqSyntactic` | Structurally equal up to binder names and binder annotations, which the kernel ignores. | `QuickIsDefEq` | `quick_is_def_eq` |
| `DefEqSort` | Sort u ≡ Sort v when u and v denote the same universe, decided by level normalization. | `QuickIsDefEq` | `quick_is_def_eq` |
| `DefEqConst` | c.{ū} ≡ c.{v̄} when the two level lists are equivalent pointwise. | `IsDefEqCore` | `is_def_eq_core`<br>inline, after lazy delta |
| `DefEqApp` | f ā ≡ g b̄ when f ≡ g and the spines are equal pointwise, without unfolding either head. | `IsDefEqCore` | `is_def_eq_app` |
| `DefEqBinding` | Congruence under a binder: domains equal, then bodies equal under the extended context. | `QuickIsDefEq` | `quick_is_def_eq` |
| `DefEqEta` | f ≡ (fun x => f x), eta for functions. | `IsDefEqCore` | `try_eta_expansion` |
| `DefEqEtaStruct` | s ≡ S.mk s.1 ... s.n for a structure S, eta for structures. | `IsDefEqCore` | `try_eta_struct` |
| `DefEqUnitLike` | Any two elements of a type with one constructor taking no fields are equal. | `IsDefEqCore` | `is_def_eq_unit_like` |
| `DefEqProofIrrel` | Γ ⊢ h : p, Γ ⊢ h' : p, p : Prop, therefore h ≡ h'. Proof irrelevance. | `IsDefEqCore` | `is_def_eq_proof_irrel` |
| `DefEqStringLit` | A string literal against an application of the string constructor. Keyed on String.ofList where it exists and String.mk otherwise, matching Lean's g_string_mk, and placed after lazy delta as Lean places it. On current Lean that ordering makes the rule unreachable in both kernels, since String.ofList is now a definition that delta unfolds first; no corpus on any version reaches it, and SafetyTests covers it directly. | `IsDefEqCore` | `try_string_lit_expansion`<br>unreachable in both while String is a structure |
| `DefEqFVar` | Γ, x : A ⊢ x ≡ x. The same free variable on both sides. | `IsDefEqCore` | `is_def_eq_core`<br>inline, after the constant case |
| `DefEqReflect` | A closed term against Bool.true, reduced rather than compared. Proofs by reflection end here, which is why the kernel reduces one side fully instead of looking for a shared structure. | `IsDefEqCore` | `is_def_eq_core`<br>inline: the Bool.true step, before whnf_core |
| `DefEqLazyDeltaProj` | Two projections of the same field, compared by unfolding the structures they project from rather than by comparing those structures whole. | `IsDefEqCore` | `lazy_delta_proj_reduction` |
| `UnfoldProjApp` | A projection applied to arguments, unfolded through the projection function during lazy delta so that a structure built by a definition can meet one built by another. | `TryUnfoldProjApp` | `try_unfold_proj_app` |
| `DefEqOffset` | Nat literals and successor offsets compared by arithmetic rather than by unfolding Nat.succ. | `IsDefEqOffset` | `is_def_eq_offset` |
| `DefEqLazyDelta` | The lazy delta loop: where both sides have delta-reducible heads, unfold the one with the greater height, so a shared definition is not unfolded on both sides at once. | `IsDefEqCore` | `lazy_delta_reduction` |

## Keeping it honest

A table like this rots the moment a rule is added without a row. `SpecificationTests` asserts that every member of
the `Rule` enum appears here, so that fails the build rather than the reader.

What it is not: a proof. It says where to look, in both implementations, for each decision the kernel makes. That
is the thing an external review needs and the scaffolding a proof would hang on, and it is a weaker claim than
either.
