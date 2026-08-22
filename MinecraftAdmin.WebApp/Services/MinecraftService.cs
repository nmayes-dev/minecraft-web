using MinecraftAdmin.WebApp.Models;

namespace MinecraftAdmin.WebApp.Services;

public sealed class MinecraftService(RconClient rcon)
{
    public async Task<MinecraftStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await rcon.ExecuteAsync("list", ct);
            return new MinecraftStatus(true, ParsePlayers(response), response, null);
        }
        catch (Exception ex)
        {
            return new MinecraftStatus(false, [], null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> GetPlayersAsync(CancellationToken ct = default)
    {
        var response = await rcon.ExecuteAsync("list", ct);
        return ParsePlayers(response);
    }

    public Task<string> ExecuteCommandAsync(string command, CancellationToken ct = default) =>
        rcon.ExecuteAsync(command, ct);

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await rcon.ExecuteAsync("say Server restarting...", ct);
        await rcon.ExecuteAsync("save-all flush", ct);
        await rcon.ExecuteAsync("stop", ct);
    }

    private static IReadOnlyList<string> ParsePlayers(string response)
    {
        var colon = response.LastIndexOf(':');
        if (colon < 0 || colon == response.Length - 1)
            return [];

        return response[(colon + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
    }
}
