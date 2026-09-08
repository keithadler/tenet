namespace Tenet.Kernel;

/// <summary>Raised when a declaration or expression fails to check. The message is meant to be read by a person.</summary>
public class KernelException : Exception
{
    /// <summary>When true, every kernel error records the managed stack at the point it was raised (for debugging the checker itself).</summary>
    public static bool CaptureStacks { get; set; } = System.Environment.GetEnvironmentVariable("TENET_DEBUG_THROW") is not null;

    public string? RaisedAt { get; }

    public KernelException(string message) : base(message)
    {
        if (CaptureStacks)
        {
            RaisedAt = System.Environment.StackTrace;
        }
    }

    public KernelException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The checker gave up on a construct it does not support, as opposed to finding it wrong.</summary>
public sealed class UnsupportedException : KernelException
{
    public UnsupportedException(string message) : base(message) { }
}

/// <summary>
/// The per-declaration unfolding limit (<see cref="TypeChecker.MaxUnfolds"/>) was exceeded. Reported like any other
/// rejection, but never re-checked in the faithful mode: the reference algorithm without failure caching would only
/// repeat the work.
/// </summary>
public sealed class DeterministicTimeoutException : KernelException
{
    public DeterministicTimeoutException(long limit)
        : base($"deterministic timeout: more than {limit} definition unfoldings while checking one declaration (TypeChecker.MaxUnfolds)")
    {
    }
}
