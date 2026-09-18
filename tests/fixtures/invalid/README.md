# The negative corpus

Exports that **must be rejected**. Every file here is well formed: it parses, and every declaration in it but
the last is accepted. What is wrong with it is what it asks the kernel to believe.

This exists because "accepts all of Mathlib with 0 failures" is a statement about agreement, not about
soundness. A kernel whose check returns `true` accepts all of Mathlib too, faster. The rejection count is what
makes the acceptance count mean anything, so the two belong together whenever either is quoted.

Each case is listed in `manifest.json` with the defense that rejects it and, where the file is otherwise valid,
the environment variable that switches that defense off. **With the defense off the file must be accepted in
full.** That second assertion is the point: without it a case could pass because the file is broken rather than
because the defense works, and it would go on passing after the defense had been deleted. That is the failure
mode this project has hit before under a different name.

`NegativeCorpusTests` runs every file both ways. Adding a file without an entry in the manifest fails, and an
entry naming a file that is not here fails too.

## What is in it

Both soundness bugs found in Tenet, kept as permanent regression cases. Neither was reachable by mutating a
valid export, which is why they are here as whole files rather than as mutations: mutation damages an honest
file, and these are files written to exploit what the checker assumed.
