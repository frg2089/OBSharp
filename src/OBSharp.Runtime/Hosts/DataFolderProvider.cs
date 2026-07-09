namespace OBSharp.Hosts;

internal sealed class DataFolderProvider(string basePath) : IDataFolderProvider
{
    public string GetDataFolder(string name) => Path.Combine(basePath, name);
}
