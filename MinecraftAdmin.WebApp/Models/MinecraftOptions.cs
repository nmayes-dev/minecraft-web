namespace MinecraftAdmin.WebApp.Models;

public sealed class MinecraftOptions
{
    public string RconHost { get; set; } = "127.0.0.1";
    public int RconPort { get; set; } = 25575;
    public string RconPassword { get; set; } = string.Empty;
    public string ServerPropertiesPath { get; set; } = "/minecraft/server.properties";
}
