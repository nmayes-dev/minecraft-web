namespace MinecraftAdmin.WebApp.Models;

public enum ConfigValueKind
{
    String,
    Integer,
    Decimal,
    Boolean
}

public sealed class ConfigFileInfo
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Group { get; init; }
    public required string Format { get; init; }
    public string SearchText { get; set; } = string.Empty;
}

public sealed class ConfigField
{
    public required string Id { get; init; }
    public required string Key { get; init; }
    public string? Section { get; init; }
    public string? Description { get; init; }
    public ConfigValueKind Kind { get; init; }
    public string Value { get; set; } = string.Empty;
}

public sealed class ConfigDocument
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Format { get; init; }
    public List<ConfigField> Fields { get; init; } = [];
}
