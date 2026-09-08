namespace Tenet.Kernel;

/// <summary>Raised when a declaration or expression fails to check. The message is meant to be read by a person.</summary>
public class KernelException : Exception
{
    public KernelException(string message) : base(message) { }
    public KernelException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The checker gave up on a construct it does not support, as opposed to finding it wrong.</summary>
public sealed class UnsupportedException : KernelException
{
    public UnsupportedException(string message) : base(message) { }
}
