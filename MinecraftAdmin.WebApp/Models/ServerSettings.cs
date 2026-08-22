namespace MinecraftAdmin.WebApp.Models;

public sealed class ServerSettings
{
    public string Motd { get; set; } = "A Minecraft Server";
    public string Difficulty { get; set; } = "normal";
    public int MaxPlayers { get; set; } = 20;
    public int ViewDistance { get; set; } = 10;
    public int SimulationDistance { get; set; } = 10;
    public bool WhiteList { get; set; }
    public bool OnlineMode { get; set; } = true;
}
