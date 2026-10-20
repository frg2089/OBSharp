namespace OBSharp.Interop.Browser;

public class ConsoleMessageEventArgs(OBSharpConsoleLevel level, string? message, string? source, int line) : EventArgs
{
    public OBSharpConsoleLevel Level { get; } = level;
    public string? Message { get; } = message;
    public string? Source { get; } = source;
    public int Line { get; } = line;
}
