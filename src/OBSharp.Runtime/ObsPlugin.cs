namespace OBSharp.Runtime;

public abstract class ObsPlugin
{
    public virtual string? Name { get; }
    public virtual string? Description { get; }

    public abstract bool Load();
    public virtual void PostLoad() { }
    public virtual void Unload() { }
}
