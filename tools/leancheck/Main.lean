/-
leancheck: replay a lean4export NDJSON file through Lean's own kernel and print one verdict per
declaration. This is the oracle for Tenet's differential tests (tools/Tenet.DiffTest): the same
file is judged by Lean's kernel and by Tenet, and the verdicts are compared.

Semantics mirror `tenet check`: declarations are added in export order; a failed declaration is
reported and then installed unchecked so later declarations can still be judged; inductive blocks
are re-derived from their types and constructors and the derived constructors and recursors are
compared field by field with the exported ones.
-/
import Lean
import Export.Parse

open Lean

deriving instance BEq for Lean.QuotKind

/-- Raw insertion of a constant, bypassing the kernel. Bound to the symbol Lean exports for its own use;
both arguments are owned, matching `Lean.Kernel.Environment.add`. -/
@[extern "lean_environment_add"]
opaque kernelEnvAdd (env : Kernel.Environment) (cinfo : ConstantInfo) : Kernel.Environment

/-- Mark quotients as initialized without checking; bound to the symbol Lean exports for its own use. Used so that,
after a broken quotient block has been installed unchecked, quotient reduction is enabled exactly as in Tenet. -/
@[extern "lean_environment_mark_quot_init"]
opaque kernelMarkQuotInit (env : Kernel.Environment) : Kernel.Environment

structure St where
  env : Kernel.Environment
  ok : Nat := 0
  failed : Nat := 0
  /-- Wall-clock nanoseconds spent in the kernel by the most recent `addChecked`. -/
  lastNanos : Nat := 0
  /-- When set (environment variable `LEANCHECK_TIMES`), print `TIME name microseconds` to stderr per declaration. -/
  times : Bool := false

abbrev M := StateRefT St IO

def report (name : Name) (r : Except String Unit) : M Unit := do
  if (← get).times then
    IO.eprintln s!"TIME {name} {(← get).lastNanos / 1000}"
  match r with
  | .ok () =>
    modify fun s => { s with ok := s.ok + 1 }
    IO.println s!"OK {name}"
  | .error msg =>
    modify fun s => { s with failed := s.failed + 1 }
    IO.println s!"FAIL {name}: {msg.replace "\n" " "}"

def addChecked (d : Declaration) : M (Except String Unit) := do
  let t0 ← IO.monoNanosNow
  let r := (← get).env.addDeclCore 0 0 d (cancelTk? := none)
  let t1 ← IO.monoNanosNow
  modify fun s => { s with lastNanos := t1 - t0 }
  match r with
  | .ok env => modify fun s => { s with env := env }; return .ok ()
  | .error ex =>
    let opts : Options := ({} : Options).setBool `pp.universes true
    return .error (← ex.toMessageData opts |>.toString)

def installRaw (cis : List ConstantInfo) : M Unit :=
  for ci in cis do
    modify fun s => { s with env := kernelEnvAdd s.env ci }

def require (ok : Bool) (what : String) : Except String Unit :=
  if ok then .ok () else .error s!"exported {what} does not match the kernel's"

def compareCtor (exported : ConstructorVal) (derived : ConstructorVal) : Except String Unit := do
  require (exported.levelParams == derived.levelParams) s!"universe parameters of {exported.name}"
  require (exported.type == derived.type) s!"type of {exported.name}"
  require (exported.induct == derived.induct) s!"induct of {exported.name}"
  require (exported.cidx == derived.cidx) s!"cidx of {exported.name}"
  require (exported.numParams == derived.numParams) s!"numParams of {exported.name}"
  require (exported.numFields == derived.numFields) s!"numFields of {exported.name}"
  require (exported.isUnsafe == derived.isUnsafe) s!"isUnsafe of {exported.name}"

def compareRec (exported : RecursorVal) (derived : RecursorVal) : Except String Unit := do
  require (exported.levelParams == derived.levelParams) s!"universe parameters of {exported.name}"
  require (exported.type == derived.type) s!"type of {exported.name}"
  require (exported.all == derived.all) s!"all of {exported.name}"
  require (exported.numParams == derived.numParams) s!"numParams of {exported.name}"
  require (exported.numIndices == derived.numIndices) s!"numIndices of {exported.name}"
  require (exported.numMotives == derived.numMotives) s!"numMotives of {exported.name}"
  require (exported.numMinors == derived.numMinors) s!"numMinors of {exported.name}"
  require (exported.k == derived.k) s!"k of {exported.name}"
  require (exported.isUnsafe == derived.isUnsafe) s!"isUnsafe of {exported.name}"
  require (exported.rules.length == derived.rules.length) s!"number of rules of {exported.name}"
  for (e, d) in exported.rules.zip derived.rules do
    require (e.ctor == d.ctor) s!"rule constructor of {exported.name}"
    require (e.nfields == d.nfields) s!"rule nfields of {exported.name}"
    require (e.rhs == d.rhs) s!"rule rhs of {exported.name}"

def compareInduct (exported : InductiveVal) (derived : InductiveVal) : Except String Unit := do
  require (exported.levelParams == derived.levelParams) s!"universe parameters of {exported.name}"
  require (exported.type == derived.type) s!"type of {exported.name}"
  require (exported.numParams == derived.numParams) s!"numParams of {exported.name}"
  require (exported.numIndices == derived.numIndices) s!"numIndices of {exported.name}"
  require (exported.all == derived.all) s!"all of {exported.name}"
  require (exported.ctors == derived.ctors) s!"ctors of {exported.name}"
  require (exported.numNested == derived.numNested) s!"numNested of {exported.name}"
  require (exported.isRec == derived.isRec) s!"isRec of {exported.name}"
  require (exported.isUnsafe == derived.isUnsafe) s!"isUnsafe of {exported.name}"
  require (exported.isReflexive == derived.isReflexive) s!"isReflexive of {exported.name}"

/-- Handle one inductive block, identified by its first type. -/
def checkInductiveBlock (consts : Std.HashMap Name ConstantInfo) (first : InductiveVal) : M Unit := do
  let blockNames := first.all
  let r : Except String Unit ← (do
    let mut types : List InductiveType := []
    let mut allCis : List ConstantInfo := []
    for n in blockNames do
      let some (.inductInfo iv) := consts[n]? | return .error s!"block member {n} is not an exported inductive"
      allCis := allCis ++ [ConstantInfo.inductInfo iv]
      let mut ctors : List Constructor := []
      for c in iv.ctors do
        let some (.ctorInfo cv) := consts[c]? | return .error s!"constructor {c} is not exported"
        ctors := ctors ++ [{ name := cv.name, type := cv.type }]
        allCis := allCis ++ [.ctorInfo cv]
      types := types ++ [{ name := iv.name, type := iv.type, ctors }]
    let decl := Declaration.inductDecl first.levelParams first.numParams types first.isUnsafe
    match ← addChecked decl with
    | .error msg => return .error msg
    | .ok () =>
      let env := (← get).env
      for n in blockNames do
        let some (.inductInfo ev) := consts[n]? | unreachable!
        let some (.inductInfo dv) := env.find? n | return .error s!"kernel did not produce {n}"
        if let .error e := compareInduct ev dv then return .error e
        for c in ev.ctors do
          let some (.ctorInfo ec) := consts[c]? | unreachable!
          let some (.ctorInfo dc) := env.find? c | return .error s!"kernel did not produce {c}"
          if let .error e := compareCtor ec dc then return .error e
      -- recursors: every exported recursor whose `all` is this block
      for (n, ci) in consts.toList do
        if let .recInfo er := ci then
          if er.all == blockNames then
            let some (.recInfo dr) := env.find? n | return .error s!"kernel did not produce recursor {n}"
            if let .error e := compareRec er dr then return .error e
      return .ok ())
  if let .error _ := r then
    -- install the exporter's view so later declarations can be judged
    let cis := consts.toList.filterMap fun (n, ci) =>
      match ci with
      | .inductInfo iv => if blockNames.contains n then some ci else none
      | .ctorInfo cv => if blockNames.contains cv.induct then some ci else none
      | .recInfo rv => if rv.all == blockNames then some ci else none
      | _ => none
    installRaw cis
  report first.name r

/-- The quotient block: add it once, then report each of the four constants separately (Tenet reports per constant). -/
def checkQuot (consts : Std.HashMap Name ConstantInfo) : M Unit := do
  let names := [``Quot, ``Quot.mk, ``Quot.lift, ``Quot.ind]
  let exported := names.filter fun n => (consts[n]?).isSome
  match ← addChecked .quotDecl with
  | .error msg =>
    installRaw (exported.filterMap consts.get?)
    modify fun s => { s with env := kernelMarkQuotInit s.env }
    for n in exported do
      report n (.error msg)
  | .ok () =>
    let env := (← get).env
    let mut anyBad := false
    for n in exported do
      let some (.quotInfo eq) := consts[n]? | continue
      let r : Except String Unit :=
        match env.find? n with
        | some (.quotInfo dq) =>
          if eq.type == dq.type && eq.levelParams == dq.levelParams && eq.kind == dq.kind then .ok ()
          else .error s!"exported {n} does not match the kernel's"
        | _ => .error s!"kernel did not produce {n}"
      if let .error _ := r then anyBad := true
      report n r
    if anyBad then
      installRaw (exported.filterMap consts.get?)
      modify fun s => { s with env := kernelMarkQuotInit s.env }

def run (path : String) : IO UInt32 := do
  let handle ← IO.FS.Handle.mk path .read
  let exported ← Export.parseStream (IO.FS.Stream.ofHandle handle)
  let kenv := (← mkEmptyEnvironment).toKernelEnv
  let consts := exported.constMap
  let times := (← IO.getEnv "LEANCHECK_TIMES").isSome
  let (_, st) ← (StateRefT'.run (s := { env := kenv, times : St }) do
    let mut quotDone := false
    for name in exported.constOrder do
      let some ci := consts[name]? | continue
      match ci with
      | .axiomInfo v => report name (← addChecked (.axiomDecl v)) ; if (← get).env.find? name |>.isNone then installRaw [ci]
      | .defnInfo v =>
        let r ← addChecked (.defnDecl v)
        if let .error _ := r then installRaw [ci]
        report name r
      | .thmInfo v =>
        let r ← addChecked (.thmDecl v)
        if let .error _ := r then installRaw [ci]
        report name r
      | .opaqueInfo v =>
        let r ← addChecked (.opaqueDecl v)
        if let .error _ := r then installRaw [ci]
        report name r
      | .quotInfo _ =>
        if !quotDone then
          quotDone := true
          checkQuot consts
      | .inductInfo v =>
        -- the block is handled when its first member is reached
        if v.all.head? == some name then
          checkInductiveBlock consts v
      | .ctorInfo _ => pure ()
      | .recInfo _ => pure ())
  IO.println s!"SUMMARY ok={st.ok} failed={st.failed}"
  return if st.failed == 0 then 0 else 1

def main (args : List String) : IO UInt32 := do
  match args with
  | [path] =>
    initSearchPath (← findSysroot)
    run path
  | _ =>
    IO.eprintln "usage: leancheck <export.ndjson>"
    return 2
