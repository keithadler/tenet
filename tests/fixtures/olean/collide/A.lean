/-- Two modules, neither importing the other, both naming a structure `Config`. -/
structure Config where
  names : Array Nat := #[]
  mode  : String

def useA (c : Config) : Config := { c with names := c.names.push 1 }
