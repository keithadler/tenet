# The export format Tenet reads

Tenet consumes the NDJSON export written by
[lean4export](https://github.com/leanprover/lean4export), format major version 3. The
authoritative description is lean4export's `format_ndjson.md`; this page records what
Tenet relies on and the differences between minor versions it accepts.

## Structure

One JSON object per line. The first is `{"meta": ...}` with exporter, Lean, and format
versions. Then, in dependency order, three kinds of table entries and the declarations:

| line | meaning |
| --- | --- |
| `{"in": i, "str": {"pre": p, "str": s}}` / `{"in": i, "num": {"pre": p, "i": n}}` | name `i` is prefix `p` extended by a string or number; name 0 is anonymous |
| `{"il": i, "succ": l}` / `max` / `imax` / `param` | level `i`; level 0 is zero |
| `{"ie": i, "bvar": n}` / `sort` / `const` / `app` / `lam` / `forallE` / `letE` / `proj` / `natVal` / `strVal` / `mdata` | expression `i` |
| `{"axiom": ...}` `{"def": ...}` `{"thm": ...}` `{"opaque": ...}` `{"quot": ...}` `{"inductive": ...}` | a declaration |

Table indices must be issued in sequence; the reader rejects gaps. `mdata` is dropped on
reading, as it has no effect on type checking.

## Declarations

- `def` carries `hints` (`"opaque"`, `"abbrev"`, or `{"regular": height}`) and `safety`
  (`"safe"`, `"unsafe"`, `"partial"`).
- `inductive` is a whole mutual block: the inductive types, all constructors, and all
  recursors with their computation rules, as Lean's kernel produced them. Tenet uses only
  the types and constructors as input and treats the rest as the claim to verify.
- `quot` lines carry the four quotient constants; Tenet regenerates them and compares.

## 3.0 versus 3.1

| | 3.0 | 3.1 |
| --- | --- | --- |
| inductive block fields | `inductiveVals`, `constructorVals`, `recursorVals` | `types`, `ctors`, `recs` |
| `def`, `thm`, `opaque`, `axiom` | one-element arrays (a mutual block) | objects with an `all` field |
| `numNested` on inductive types | present | present |

Both are accepted. A `def` array with more than one element is read as a mutual block of
definitions, which the kernel accepts only when they are unsafe or partial.
