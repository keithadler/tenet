/-- A different `Config` entirely: different fields, different types. -/
structure Config where
  outDir : String
  inDir  : String

def useB (c : Config) : Config := { c with outDir := c.inDir }
