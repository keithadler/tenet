# Two modules, one name

`A.lean` and `B.lean` each declare a structure called `Config`, with different fields and different types.
Neither imports the other, which is ordinary: a project with several executables does this constantly. Verso has
eight top-level `Config` structures.

Built with Lean 4.35.0-rc2 from the two `.lean` files beside them.

Keyed by name alone, the second `Config` is lost and every reference to it resolves to the first. The kernel
then compares a term against a type from an unrelated module and reports a type mismatch that is not there.
Tenet 0.11.0 rejects 12 declarations in this fixture and 33 in verso, all of them sound and all of them just
compiled by Lean.

These are checked together by `OleanScopeTests`, which is the regression test for that.
