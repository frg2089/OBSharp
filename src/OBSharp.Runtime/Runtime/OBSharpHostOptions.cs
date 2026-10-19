namespace OBSharp.Runtime;

/// <summary>
/// 宿主运行参数：数据目录由 OBS 给（<c>obs_module_t::GetModuleDataPath</c>）。
/// </summary>
internal sealed record OBSharpHostOptions(string DataPath);
