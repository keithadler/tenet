using System.Runtime.CompilerServices;

namespace Tenet.Kernel;

/// <summary>
/// Keeps kernel recursion inside the stack it was given.
///
/// Kernel recursion follows the structure of the term being checked, and reduction can grow a term without bound:
/// a definition that unfolds into itself, a mutated recursor, anything ill-typed enough to reduce forever. The
/// unfolding limit (<see cref="TypeChecker.MaxUnfolds"/>) bounds how many unfoldings that takes, but not how deep
/// the resulting term is, and a term can reach the bottom of a 512 MB stack long before it spends a hundred
/// million unfoldings.
///
/// A .NET stack overflow cannot be caught. It aborts the process, which takes down every other declaration being
/// checked on every other thread and leaves no report behind, so a run that hits one looks from the outside like a
/// run that found nothing. Probing the remaining stack turns that into an ordinary rejection of one declaration.
///
/// The probe measures the real stack rather than counting frames, so it stays correct when frames differ in size
/// and honors whatever <c>--stack-mb</c> asked for. It is cheap, but not free, so the tight structural traversals
/// probe once every <see cref="Interval"/> levels; their frames are small enough that the runtime's own margin
/// covers the gap many times over.
/// </summary>
public static class StackGuard
{
    /// <summary>How many levels of a structural traversal pass between probes. A power of two minus one, used as a mask.</summary>
    public const int Interval = 63;

    /// <summary>Throw if the remaining stack is too small to keep recursing.</summary>
    public static void Check()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new RecursionLimitException();
        }
    }

    /// <summary>Probe on every <see cref="Interval"/>th level of a traversal that tracks its own depth.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CheckEvery(int depth)
    {
        if ((depth & Interval) == 0)
        {
            Check();
        }
    }
}
