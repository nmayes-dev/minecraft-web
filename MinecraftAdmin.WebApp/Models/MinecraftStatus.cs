namespace MinecraftAdmin.WebApp.Models;

public sealed record MinecraftStatus(bool Online, IReadOnlyList<string> Players, string? RawResponse, string? Error)
{
    public int PlayerCount => Players.Count;
}

public sealed record MinecraftCommandRequest(string Command);
public sealed record MinecraftCommandResponse(string Response);
