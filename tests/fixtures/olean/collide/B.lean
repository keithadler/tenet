prelude
/-- A different `Config`: different field count, different types, same name. -/
inductive Label where
  | here

structure Config where
  label : Label

def useB (c : Config) : Config := { c with label := c.label }
