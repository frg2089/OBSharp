namespace OBSharp;

public abstract class ObsPlugin(IDataFolderProvider dataFolderProvider)
{
    public virtual string Name
    {
        get
        {
            var type = GetType();
            return type.Assembly.GetName().Name ?? type.Namespace ?? type.Name;
        }
    }

    public virtual string? Description { get; }

    public abstract bool Load();
    public virtual void PostLoad() { }
    public virtual void Unload() { }
    public string GetDataFolder() => dataFolderProvider.GetDataFolder(Name);
}
