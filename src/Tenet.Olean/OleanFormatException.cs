namespace Tenet.Olean;

/// <summary>The file is not an .olean Tenet understands, or its object graph is not what a Lean 4 module should contain.</summary>
public sealed class OleanFormatException : Exception
{
    public string Path { get; }
    public OleanFormatException(string path, string message) : base($"{path}: {message}") => Path = path;
}
