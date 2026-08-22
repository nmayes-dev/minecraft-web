namespace MinecraftAdmin.WebApp.Models;

public sealed class ConfigTreeNode
{
    public required string Name { get; init; }
    public required string Key { get; init; }
    public ConfigFileInfo? File { get; init; }
    public List<ConfigTreeNode> Children { get; } = [];

    public bool IsDirectory => File is null;
}
