namespace MinecraftAdmin.WebApp.Models;

public sealed class ServerManagementOptions
{
    public string EnvironmentPath { get; set; } = "/server-config/minecraft.env";
    public string ArchivePath { get; set; } = "/old";
    public string ModpackLibraryPath { get; set; } = "/server-config/modpacks";
    public string DockerSocketPath { get; set; } = "/var/run/docker.sock";
    public string ContainerName { get; set; } = "minecraft";
    public int MinecraftUid { get; set; } = 1000;
    public int MinecraftGid { get; set; } = 1000;
    public long MaxModpackUploadBytes { get; set; } = 8L * 1024 * 1024 * 1024;
}
