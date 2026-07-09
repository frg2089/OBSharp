namespace OBSharp;

public interface IRuntimeInfoProvider
{
    bool UseAspNet { get; }
    bool UseWindowsDesktop { get; }
}

