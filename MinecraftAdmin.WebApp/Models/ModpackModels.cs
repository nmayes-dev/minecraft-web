namespace MinecraftAdmin.WebApp.Models;

public sealed record ModpackConfiguration(string Environment);

public sealed class ModpackDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public string ServerPackFile { get; init; } = "server-pack.zip";
    public string OriginalFileName { get; init; } = string.Empty;
    public DateTimeOffset UploadedAtUtc { get; init; }
    public List<ModpackWorldArchive> Worlds { get; init; } = [];
}

public sealed class ModpackWorldArchive
{
    public string Id { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string LevelName { get; init; } = "world";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public long SizeBytes { get; init; }
}

public sealed record ModpackUploadRequest(string Name, string Environment);

public sealed record ModpackUploadProgress(long BytesTransferred, long TotalBytes);

public sealed record ModpackSwitchRequest(string ModpackId, string? WorldArchiveId);

public sealed record ModpackSwitchResult(string BackupArchiveFile, string Message);

public sealed record ModpackLibraryState(
    IReadOnlyList<ModpackDefinition> Modpacks,
    string? ActiveModpackId);
