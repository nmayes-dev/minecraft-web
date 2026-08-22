using Microsoft.Extensions.Options;
using MinecraftAdmin.WebApp.Models;

namespace MinecraftAdmin.WebApp.Services;

public sealed class ServerPropertiesService(IOptions<MinecraftOptions> options)
{
    private readonly string path_ = options.Value.ServerPropertiesPath;
    private readonly SemaphoreSlim mutex_ = new(1, 1);

    public async Task<ServerSettings> GetAsync(CancellationToken ct = default)
    {
        var values = await ReadValuesAsync(ct);

        return new ServerSettings
        {
            Motd = Get(values, "motd", "A Minecraft Server"),
            Difficulty = Get(values, "difficulty", "normal"),
            MaxPlayers = GetInt(values, "max-players", 20),
            ViewDistance = GetInt(values, "view-distance", 10),
            SimulationDistance = GetInt(values, "simulation-distance", 10),
            WhiteList = GetBool(values, "white-list", false),
            OnlineMode = GetBool(values, "online-mode", true)
        };
    }

    public async Task UpdateAsync(ServerSettings settings, CancellationToken ct = default)
    {
        if (settings.MaxPlayers is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(settings.MaxPlayers));
        if (settings.ViewDistance is < 2 or > 32)
            throw new ArgumentOutOfRangeException(nameof(settings.ViewDistance));
        if (settings.SimulationDistance is < 2 or > 32)
            throw new ArgumentOutOfRangeException(nameof(settings.SimulationDistance));

        await mutex_.WaitAsync(ct);
        try
        {
            var lines = File.Exists(path_) ? await File.ReadAllLinesAsync(path_, ct) : [];
            var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["motd"] = settings.Motd,
                ["difficulty"] = settings.Difficulty,
                ["max-players"] = settings.MaxPlayers.ToString(),
                ["view-distance"] = settings.ViewDistance.ToString(),
                ["simulation-distance"] = settings.SimulationDistance.ToString(),
                ["white-list"] = settings.WhiteList.ToString().ToLowerInvariant(),
                ["online-mode"] = settings.OnlineMode.ToString().ToLowerInvariant()
            };

            var output = new List<string>(lines.Length + updates.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    output.Add(line);
                    continue;
                }

                var equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    output.Add(line);
                    continue;
                }

                var key = line[..equals].Trim();
                if (updates.TryGetValue(key, out var value))
                {
                    output.Add($"{key}={value}");
                    seen.Add(key);
                }
                else
                {
                    output.Add(line);
                }
            }

            foreach (var pair in updates.Where(x => !seen.Contains(x.Key)))
                output.Add($"{pair.Key}={pair.Value}");

            var directory = Path.GetDirectoryName(path_);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllLinesAsync(path_, output, ct);
        }
        finally
        {
            mutex_.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadValuesAsync(CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path_))
            return values;

        foreach (var line in await File.ReadAllLinesAsync(path_, ct))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            values[line[..equals].Trim()] = line[(equals + 1)..];
        }

        return values;
    }

    private static string Get(Dictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static int GetInt(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var value) && int.TryParse(value, out var result) ? result : fallback;

    private static bool GetBool(Dictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var result) ? result : fallback;
}
