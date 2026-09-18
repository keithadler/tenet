using Xunit;

// Several tests switch a kernel-wide setting off to show that a defense is what refuses an attack:
// Primitives.Validate, TypeChecker.Stats.Enabled, TypeChecker.MaxUnfolds. Those are process-wide, so with xUnit's
// default parallelism one test can observe another's setting and fail for a reason that has nothing to do with it.
// The suite runs in about ten seconds; serializing it costs nothing and removes the whole class of flake.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
