namespace Recap.Recording;

public sealed class AudioDeviceOption(string id, string name)
{
    public string Id { get; } = id;
    public string Name { get; } = name;

    public override string ToString() => Name;
}
