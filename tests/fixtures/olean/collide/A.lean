prelude
/-- Self-contained: imports nothing, so checking it needs no Lean toolchain on the machine. -/
inductive Tag where
  | one
  | two

structure Config where
  tag  : Tag
  more : Tag

def useA (c : Config) : Config := { c with tag := c.more }
