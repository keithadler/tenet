# Two modules, one name

`A.lean` and `B.lean` each declare a structure called `Config`, with different fields and different types.
Neither imports the other, which is ordinary: a project with several executables does this constantly. Verso has
eight top-level `Config` structures.

Both are `prelude` modules importing nothing at all, so checking them needs no Lean toolchain on the machine.
The first version of this fixture imported `Init` like any ordinary module and passed here while failing on
every CI runner, none of which has Lean installed. Built with Lean 4.35.0-rc2 from the two `.lean` files.

Keyed by name alone, the second `Config` is lost and every reference to it resolves to the first. The kernel
then compares a term against a type from an unrelated module and reports a type mismatch that is not there.
Tenet 0.11.0 rejects 3 declarations in this fixture and 33 in verso, all of them sound and all of them just
compiled by Lean.

`OleanScopeTests` is the regression test. Two of its three cases fail without the scoped resolver.
