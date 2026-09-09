using System.ComponentModel;
using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MinecraftAdmin.WebApp.Models;

namespace MinecraftAdmin.WebApp.Services;

public sealed class ModpackService(
    IOptions<MinecraftOptions> minecraftOptions,
    IOptions<ServerManagementOptions> managementOptions,
    MinecraftService minecraft,
    DockerService docker)
{
    private const string MetadataFileName = "modpack.json";
    private const string ServerPackFileName = "server-pack.zip";
    private const string ActiveStateFileName = "active.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly MinecraftOptions minecraftOptions_ = minecraftOptions.Value;
    private readonly ServerManagementOptions managementOptions_ = managementOptions.Value;
    private readonly SemaphoreSlim switchLock_ = new(1, 1);
    private readonly SemaphoreSlim libraryLock_ = new(1, 1);

    public long MaxUploadBytes => managementOptions_.MaxModpackUploadBytes;

    public async Task<ModpackConfiguration> GetConfigurationAsync(CancellationToken ct = default)
    {
        var path = managementOptions_.EnvironmentPath;
        if (!File.Exists(path))
            return new ModpackConfiguration(string.Empty);

        return new ModpackConfiguration(await File.ReadAllTextAsync(path, ct));
    }

    public async Task<ModpackLibraryState> GetLibraryAsync(CancellationToken ct = default)
    {
        var modpacks = await GetModpacksAsync(ct);
        var activeId = await ResolveActiveModpackIdAsync(modpacks, ct);
        return new ModpackLibraryState(modpacks, activeId);
    }

    public async Task<IReadOnlyList<ModpackDefinition>> GetModpacksAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(managementOptions_.ModpackLibraryPath);
        var result = new List<ModpackDefinition>();

        foreach (var directory in Directory.EnumerateDirectories(managementOptions_.ModpackLibraryPath))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(directory).StartsWith('.'))
                continue;

            var metadataPath = Path.Combine(directory, MetadataFileName);
            if (!File.Exists(metadataPath))
                continue;

            try
            {
                var json = await File.ReadAllTextAsync(metadataPath, ct);
                var modpack = JsonSerializer.Deserialize<ModpackDefinition>(json, JsonOptions);
                if (modpack is not null && !string.IsNullOrWhiteSpace(modpack.Id))
                    result.Add(modpack);
            }
            catch (JsonException)
            {
                // Ignore invalid library entries rather than making the whole page unusable.
            }
        }

        return result
            .OrderBy(modpack => modpack.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<ModpackDefinition> GetModpackAsync(string modpackId, CancellationToken ct = default) =>
        ReadModpackAsync(modpackId, ct);

    public async Task<ModpackDefinition> UpdateAsync(
        string modpackId,
        ModpackUpdateRequest request,
        CancellationToken ct = default)
    {
        var name = request.Name.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("Enter a modpack name.");

        ParseEnvironment(request.Environment);

        await libraryLock_.WaitAsync(ct);
        try
        {
            var current = await ReadModpackAsync(modpackId, ct);
            var updated = new ModpackDefinition
            {
                Id = current.Id,
                Name = name,
                Environment = NormalizeEnvironmentText(request.Environment),
                ServerPackFile = current.ServerPackFile,
                OriginalFileName = current.OriginalFileName,
                UploadedAtUtc = current.UploadedAtUtc,
                Worlds = current.Worlds
            };

            await WriteModpackAsync(updated, ct);
            return updated;
        }
        finally
        {
            libraryLock_.Release();
        }
    }

    public async Task<ModpackDefinition> UploadAsync(
        ModpackUploadRequest request,
        Stream serverPack,
        string originalFileName,
        long totalBytes,
        IProgress<ModpackUploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var name = request.Name.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("Enter a modpack name.");

        ParseEnvironment(request.Environment);

        if (!string.Equals(Path.GetExtension(originalFileName), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The server modpack must be uploaded as a .zip file.");

        var id = CreateModpackId(name);
        var libraryPath = managementOptions_.ModpackLibraryPath;
        Directory.CreateDirectory(libraryPath);

        await libraryLock_.WaitAsync(ct);
        try
        {
            var destinationDirectory = Path.Combine(libraryPath, id);
            if (Directory.Exists(destinationDirectory))
                throw new InvalidOperationException($"A modpack with the id '{id}' already exists. Use a different name.");

            var temporaryDirectory = Path.Combine(libraryPath, $".upload-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);

            try
            {
                var zipPath = Path.Combine(temporaryDirectory, ServerPackFileName);
                await SaveUploadAsync(serverPack, zipPath, totalBytes, progress, ct);
                ValidateZip(zipPath, ct);

                var modpack = new ModpackDefinition
                {
                    Id = id,
                    Name = name,
                    Environment = NormalizeEnvironmentText(request.Environment),
                    ServerPackFile = ServerPackFileName,
                    OriginalFileName = Path.GetFileName(originalFileName),
                    UploadedAtUtc = DateTimeOffset.UtcNow,
                    Worlds = []
                };

                await WriteJsonAtomicallyAsync(Path.Combine(temporaryDirectory, MetadataFileName), modpack, ct);
                Directory.Move(temporaryDirectory, destinationDirectory);
                return modpack;
            }
            catch
            {
                if (Directory.Exists(temporaryDirectory))
                    Directory.Delete(temporaryDirectory, recursive: true);
                throw;
            }
        }
        finally
        {
            libraryLock_.Release();
        }
    }

    public async Task<ModpackSwitchResult> SwitchAsync(
        ModpackSwitchRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!await switchLock_.WaitAsync(0, ct))
            throw new InvalidOperationException("A modpack change is already in progress.");

        try
        {
            var library = await GetLibraryAsync(ct);
            var target = library.Modpacks.FirstOrDefault(modpack =>
                string.Equals(modpack.Id, request.ModpackId, StringComparison.Ordinal));

            if (target is null)
                throw new InvalidOperationException("The selected modpack no longer exists.");

            var selectedWorld = ResolveWorld(target, request.WorldArchiveId);
            var newEnvironment = ParseEnvironment(target.Environment);
            var existingConfiguration = await GetConfigurationAsync(ct);
            var oldEnvironment = ParseEnvironment(existingConfiguration.Environment, allowEmpty: true);
            var current = library.ActiveModpackId is null
                ? null
                : library.Modpacks.FirstOrDefault(modpack => modpack.Id == library.ActiveModpackId);

            progress?.Report("Reading current Docker configuration…");
            var inspection = await docker.InspectContainerAsync(ct);

            progress?.Report("Saving the current Minecraft world…");
            await TrySaveServerAsync(ct);

            progress?.Report("Stopping the Minecraft container…");
            await docker.StopContainerAsync(ct);

            string backupArchive;
            try
            {
                progress?.Report("Creating a full backup of the current server…");
                backupArchive = await ArchiveCurrentServerAsync(current?.Id ?? "unmanaged", ct);

                if (current is not null)
                {
                    progress?.Report($"Saving the current world for {current.Name}…");
                    await SaveCurrentWorldSnapshotAsync(current, ct);
                }
            }
            catch
            {
                try { await docker.StartContainerAsync(ct); } catch { }
                throw;
            }

            try
            {
                progress?.Report($"Installing {target.Name}…");
                ClearDirectory(minecraftOptions_.DataPath);
                await ExtractServerPackAsync(target, ct);

                if (selectedWorld is null)
                {
                    progress?.Report("Preparing a new world…");
                    RemoveBundledWorld(minecraftOptions_.DataPath);
                }
                else
                {
                    progress?.Report($"Restoring world from {selectedWorld.CreatedAtUtc.ToLocalTime():g}…");
                    await RestoreWorldAsync(target, selectedWorld, ct);
                }

                progress?.Report("Applying Minecraft file ownership…");
                NormalizeMinecraftOwnership();

                progress?.Report("Writing the selected modpack environment…");
                await WriteEnvironmentAtomicallyAsync(target.Environment, ct);

                progress?.Report("Recreating the Minecraft container…");
                await docker.RemoveContainerAsync(ct);
                await docker.CreateContainerAsync(inspection, oldEnvironment.Keys.ToList(), newEnvironment, ct);

                progress?.Report("Starting the selected modpack…");
                await docker.StartContainerAsync(ct);

                await WriteActiveStateAsync(target.Id, ct);

                return new ModpackSwitchResult(
                    backupArchive,
                    $"{target.Name} has been started. The server may continue installing before RCON becomes available.");
            }
            catch
            {
                progress?.Report("The change failed; restoring the previous server…");
                await RestorePreviousServerAsync(
                    inspection,
                    existingConfiguration.Environment,
                    oldEnvironment,
                    backupArchive,
                    CancellationToken.None);
                throw;
            }
        }
        finally
        {
            switchLock_.Release();
        }
    }

    private static ModpackWorldArchive? ResolveWorld(ModpackDefinition target, string? worldArchiveId)
    {
        if (string.IsNullOrWhiteSpace(worldArchiveId))
            return null;

        return target.Worlds.FirstOrDefault(world => world.Id == worldArchiveId)
            ?? throw new InvalidOperationException("The selected saved world no longer exists.");
    }

    private async Task TrySaveServerAsync(CancellationToken ct)
    {
        try
        {
            await minecraft.ExecuteCommandAsync("say Server stopping for a modpack change...", ct);
            await minecraft.ExecuteCommandAsync("save-all flush", ct);
        }
        catch
        {
            // An already-offline server can still be archived safely after Docker stops it.
        }
    }

    private async Task<string> ArchiveCurrentServerAsync(string archiveName, CancellationToken ct)
    {
        Directory.CreateDirectory(managementOptions_.ArchivePath);
        Directory.CreateDirectory(minecraftOptions_.DataPath);

        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var destination = Path.Combine(managementOptions_.ArchivePath, $"{archiveName}-{timestamp}.tar.gz");

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
            TarFile.CreateFromDirectory(minecraftOptions_.DataPath, gzip, includeBaseDirectory: false);
        }, ct);

        return destination;
    }

    private async Task SaveCurrentWorldSnapshotAsync(ModpackDefinition current, CancellationToken ct)
    {
        var levelName = ReadLevelName(minecraftOptions_.DataPath);
        var worldPath = GetSafeWorldPath(minecraftOptions_.DataPath, levelName);
        if (!Directory.Exists(worldPath))
            return;

        var modpackDirectory = GetModpackDirectory(current.Id);
        var worldsDirectory = Path.Combine(modpackDirectory, "worlds");
        Directory.CreateDirectory(worldsDirectory);

        var now = DateTimeOffset.UtcNow;
        var id = ($"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}")[..24];
        var relativeFileName = Path.Combine("worlds", $"world-{id}.tar.gz");
        var destination = Path.Combine(modpackDirectory, relativeFileName);

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
            TarFile.CreateFromDirectory(worldPath, gzip, includeBaseDirectory: true);
        }, ct);

        var archive = new ModpackWorldArchive
        {
            Id = id,
            FileName = relativeFileName.Replace('\\', '/'),
            LevelName = levelName,
            CreatedAtUtc = now,
            SizeBytes = new FileInfo(destination).Length
        };

        await libraryLock_.WaitAsync(ct);
        try
        {
            var latest = await ReadModpackAsync(current.Id, ct);
            latest.Worlds.Add(archive);
            await WriteModpackAsync(latest, ct);
        }
        catch
        {
            try { File.Delete(destination); } catch { }
            throw;
        }
        finally
        {
            libraryLock_.Release();
        }
    }

    private async Task ExtractServerPackAsync(ModpackDefinition modpack, CancellationToken ct)
    {
        var zipPath = GetContainedPath(GetModpackDirectory(modpack.Id), modpack.ServerPackFile);
        if (!File.Exists(zipPath))
            throw new InvalidOperationException($"The server pack ZIP for '{modpack.Name}' is missing.");

        await Task.Run(() => ExtractZipSafely(zipPath, minecraftOptions_.DataPath), ct);
    }

    private async Task RestoreWorldAsync(ModpackDefinition modpack, ModpackWorldArchive world, CancellationToken ct)
    {
        var modpackDirectory = GetModpackDirectory(modpack.Id);
        var archivePath = GetContainedPath(modpackDirectory, world.FileName);
        if (!File.Exists(archivePath))
            throw new InvalidOperationException("The selected world archive is missing from disk.");

        var bundledLevelName = ReadLevelName(minecraftOptions_.DataPath);
        DeleteDirectoryIfExists(GetSafeWorldPath(minecraftOptions_.DataPath, bundledLevelName));
        DeleteDirectoryIfExists(GetSafeWorldPath(minecraftOptions_.DataPath, world.LevelName));

        await Task.Run(() =>
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, minecraftOptions_.DataPath, overwriteFiles: true);
        }, ct);

        WriteServerProperty(minecraftOptions_.DataPath, "level-name", world.LevelName);
    }

    private async Task RestorePreviousServerAsync(
        System.Text.Json.Nodes.JsonObject inspection,
        string previousEnvironmentText,
        IReadOnlyDictionary<string, string> previousEnvironment,
        string? archiveFile,
        CancellationToken ct)
    {
        try
        {
            try { await docker.StopContainerAsync(ct); } catch { }
            try { await docker.RemoveContainerAsync(ct); } catch { }

            ClearDirectory(minecraftOptions_.DataPath);
            if (!string.IsNullOrWhiteSpace(archiveFile) && File.Exists(archiveFile))
            {
                await Task.Run(() =>
                {
                    using var file = File.OpenRead(archiveFile);
                    using var gzip = new GZipStream(file, CompressionMode.Decompress);
                    TarFile.ExtractToDirectory(gzip, minecraftOptions_.DataPath, overwriteFiles: true);
                }, ct);
            }

            NormalizeMinecraftOwnership();

            await WriteEnvironmentAtomicallyAsync(previousEnvironmentText, ct);
            await docker.CreateContainerAsync(inspection, previousEnvironment.Keys.ToList(), previousEnvironment, ct);
            await docker.StartContainerAsync(ct);
        }
        catch (Exception restoreException)
        {
            throw new InvalidOperationException(
                $"The modpack change failed and automatic rollback also failed: {restoreException.Message}",
                restoreException);
        }
    }

    private async Task<string?> ResolveActiveModpackIdAsync(
        IReadOnlyList<ModpackDefinition> modpacks,
        CancellationToken ct)
    {
        var activeStatePath = Path.Combine(managementOptions_.ModpackLibraryPath, ActiveStateFileName);
        if (File.Exists(activeStatePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(activeStatePath, ct);
                var state = JsonSerializer.Deserialize<ActiveModpackState>(json, JsonOptions);
                if (state is not null && modpacks.Any(modpack => modpack.Id == state.ModpackId))
                    return state.ModpackId;
            }
            catch (JsonException)
            {
            }
        }

        var currentEnvironment = NormalizeEnvironmentText((await GetConfigurationAsync(ct)).Environment);
        if (currentEnvironment.Length == 0)
            return null;

        var match = modpacks.FirstOrDefault(modpack =>
            string.Equals(NormalizeEnvironmentText(modpack.Environment), currentEnvironment, StringComparison.Ordinal));

        if (match is not null)
            await WriteActiveStateAsync(match.Id, ct);

        return match?.Id;
    }

    private Task WriteActiveStateAsync(string modpackId, CancellationToken ct)
    {
        Directory.CreateDirectory(managementOptions_.ModpackLibraryPath);
        return WriteJsonAtomicallyAsync(
            Path.Combine(managementOptions_.ModpackLibraryPath, ActiveStateFileName),
            new ActiveModpackState(modpackId),
            ct);
    }

    private async Task<ModpackDefinition> ReadModpackAsync(string modpackId, CancellationToken ct)
    {
        var path = Path.Combine(GetModpackDirectory(modpackId), MetadataFileName);
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<ModpackDefinition>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Could not read modpack metadata for '{modpackId}'.");
    }

    private Task WriteModpackAsync(ModpackDefinition modpack, CancellationToken ct) =>
        WriteJsonAtomicallyAsync(Path.Combine(GetModpackDirectory(modpack.Id), MetadataFileName), modpack, ct);

    private async Task WriteEnvironmentAtomicallyAsync(string environment, CancellationToken ct)
    {
        var path = managementOptions_.EnvironmentPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, NormalizeEnvironmentText(environment), new UTF8Encoding(false), ct);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static async Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await File.WriteAllTextAsync(temporaryPath, json + Environment.NewLine, new UTF8Encoding(false), ct);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private async Task SaveUploadAsync(
        Stream source,
        string destination,
        long totalBytes,
        IProgress<ModpackUploadProgress>? progress,
        CancellationToken ct)
    {
        if (totalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytes));

        if (totalBytes > managementOptions_.MaxModpackUploadBytes)
            throw new InvalidOperationException($"The upload exceeds the configured limit of {FormatBytes(managementOptions_.MaxModpackUploadBytes)}.");

        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        var buffer = new byte[1024 * 1024];
        long transferred = 0;
        progress?.Report(new ModpackUploadProgress(0, totalBytes));

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
                break;

            transferred += read;
            if (transferred > managementOptions_.MaxModpackUploadBytes)
                throw new InvalidOperationException($"The upload exceeds the configured limit of {FormatBytes(managementOptions_.MaxModpackUploadBytes)}.");

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            progress?.Report(new ModpackUploadProgress(transferred, totalBytes));
        }

        if (transferred == 0)
            throw new InvalidOperationException("The uploaded ZIP is empty.");

        if (totalBytes > 0 && transferred != totalBytes)
            throw new InvalidOperationException("The upload ended before the entire ZIP was received.");

        progress?.Report(new ModpackUploadProgress(transferred, totalBytes));
    }

    private static void ValidateZip(string path, CancellationToken ct)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0)
                throw new InvalidOperationException("The uploaded ZIP contains no files.");

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                ValidateArchiveEntryPath(entry.FullName);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("The uploaded file is not a valid ZIP archive.", ex);
        }
    }

    private static void ExtractZipSafely(string zipPath, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        var root = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            ValidateArchiveEntryPath(entry.FullName);

            var normalized = entry.FullName.Replace('\\', '/');
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, normalized));
            if (!destination.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsafe path in server pack ZIP: '{entry.FullName}'.");

            if (normalized.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void ValidateArchiveEntryPath(string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Contains(':') || normalized.Split('/').Any(part => part == ".."))
            throw new InvalidOperationException($"Unsafe path in server pack ZIP: '{entryName}'.");
    }

    private string GetModpackDirectory(string modpackId)
    {
        if (string.IsNullOrWhiteSpace(modpackId) || modpackId.Any(c => !(char.IsLetterOrDigit(c) || c == '-')))
            throw new InvalidOperationException("Invalid modpack id.");

        return Path.Combine(managementOptions_.ModpackLibraryPath, modpackId);
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(rootFull, StringComparison.Ordinal))
            throw new InvalidOperationException("Modpack metadata contains an unsafe file path.");
        return candidate;
    }

    private static IReadOnlyDictionary<string, string> ParseEnvironment(string text, bool allowEmpty = false)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var separator = line.IndexOf('=');
            if (separator <= 0)
                throw new InvalidOperationException($"Invalid environment line: '{rawLine}'. Expected KEY=value.");

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (!IsValidEnvironmentKey(key))
                throw new InvalidOperationException($"Invalid environment variable name '{key}'.");

            if (!result.TryAdd(key, value))
                throw new InvalidOperationException($"Environment variable '{key}' is defined more than once.");
        }

        if (!allowEmpty && result.Count == 0)
            throw new InvalidOperationException("At least one modpack environment variable is required.");

        return result;
    }

    private static bool IsValidEnvironmentKey(string key) =>
        key.Length > 0 &&
        (char.IsLetter(key[0]) || key[0] == '_') &&
        key.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');

    private static string NormalizeEnvironmentText(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Trim();
        return normalized.Length == 0 ? string.Empty : normalized + Environment.NewLine;
    }

    private static string CreateModpackId(string name)
    {
        var builder = new StringBuilder();
        var pendingDash = false;

        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingDash && builder.Length > 0)
                    builder.Append('-');
                builder.Append(c);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        var id = builder.ToString().Trim('-');
        return id.Length == 0 ? ($"modpack-{Guid.NewGuid():N}")[..16] : id;
    }

    private static string ReadLevelName(string dataPath)
    {
        var propertiesPath = Path.Combine(dataPath, "server.properties");
        if (!File.Exists(propertiesPath))
            return "world";

        foreach (var rawLine in File.ReadLines(propertiesPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            if (string.Equals(line[..separator].Trim(), "level-name", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(separator + 1)..].Trim();
                return value.Length == 0 ? "world" : value;
            }
        }

        return "world";
    }

    private static string GetSafeWorldPath(string dataPath, string levelName)
    {
        if (Path.IsPathRooted(levelName))
            throw new InvalidOperationException("server.properties contains an unsafe level-name.");

        var root = Path.GetFullPath(dataPath) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(dataPath, levelName));
        if (!candidate.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("server.properties contains an unsafe level-name.");

        return candidate;
    }

    private static void RemoveBundledWorld(string dataPath)
    {
        var levelName = ReadLevelName(dataPath);
        DeleteDirectoryIfExists(GetSafeWorldPath(dataPath, levelName));
    }

    private static void WriteServerProperty(string dataPath, string key, string value)
    {
        var path = Path.Combine(dataPath, "server.properties");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var found = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('#'))
                continue;

            var separator = line.IndexOf('=');
            if (separator <= 0 || !string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
                continue;

            lines[i] = $"{key}={value}";
            found = true;
            break;
        }

        if (!found)
            lines.Add($"{key}={value}");

        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private static void ClearDirectory(string path)
    {
        Directory.CreateDirectory(path);

        foreach (var file in Directory.EnumerateFiles(path))
            File.Delete(file);

        foreach (var directory in Directory.EnumerateDirectories(path))
            Directory.Delete(directory, recursive: true);
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private void NormalizeMinecraftOwnership()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var uid = managementOptions_.MinecraftUid;
        var gid = managementOptions_.MinecraftGid;

        if (uid < 0)
            throw new InvalidOperationException("ServerManagement:MinecraftUid cannot be negative.");

        if (gid < 0)
            throw new InvalidOperationException("ServerManagement:MinecraftGid cannot be negative.");

        var dataPath = minecraftOptions_.DataPath;
        Directory.CreateDirectory(dataPath);

        SetOwnership(dataPath, uid, gid);

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     dataPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            SetOwnership(path, uid, gid);
        }
    }

    private static void SetOwnership(string path, int uid, int gid)
    {
        if (NativeMethods.LChown(path, (uint)uid, (uint)gid) == 0)
            return;

        var error = Marshal.GetLastPInvokeError();
        throw new IOException(
            $"Failed to set ownership of '{path}' to {uid}:{gid}: " +
            $"{new Win32Exception(error).Message} (errno {error}).");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "lchown", SetLastError = true)]
        internal static extern int LChown(string path, uint owner, uint group);
    }

    private sealed record ActiveModpackState(string ModpackId);
}
