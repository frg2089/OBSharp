namespace OBSharp.Runtime;

/// <summary>
/// 宿主运行参数：数据目录由 OBS 给 (<see cref="ObsModuleExtensions.GetModuleDataPath"/>)。
/// </summary>
internal sealed class OBSharpHostOptions
{
    public string BinaryPath { get; internal set; } = default!;
    public string DataPath { get; internal set; } = default!;
}
