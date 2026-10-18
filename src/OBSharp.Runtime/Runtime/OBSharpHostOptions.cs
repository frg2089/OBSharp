namespace OBSharp.Runtime;

/// <summary>
/// 宿主运行参数：数据目录由 OBS 给（<c>obs_module_t::GetModuleDataPath</c>），
/// 运行时框架来自 <c>appsettings.json</c>。
/// </summary>
internal sealed record OBSharpHostOptions(RuntimeFramework RuntimeFramework, string DataPath);
