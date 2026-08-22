using MinecraftAdmin.WebApp.Components;
using MinecraftAdmin.WebApp.Models;
using MinecraftAdmin.WebApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<MinecraftOptions>(builder.Configuration.GetSection("Minecraft"));
builder.Services.AddSingleton<RconClient>();
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton<MinecraftService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();
app.MapStaticAssets();

var api = app.MapGroup("/api");

api.MapGet("/server/status", async (MinecraftService minecraft, CancellationToken ct) =>
    Results.Ok(await minecraft.GetStatusAsync(ct)));

api.MapGet("/server/players", async (MinecraftService minecraft, CancellationToken ct) =>
    Results.Ok(await minecraft.GetPlayersAsync(ct)));

api.MapPost("/server/command", async (MinecraftCommandRequest request, MinecraftService minecraft, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Command))
        return Results.BadRequest(new { error = "Command is required." });

    return Results.Ok(new MinecraftCommandResponse(await minecraft.ExecuteCommandAsync(request.Command, ct)));
});

api.MapPost("/server/restart", async (MinecraftService minecraft, CancellationToken ct) =>
{
    await minecraft.RestartAsync(ct);
    return Results.Accepted();
});

api.MapGet("/config/files", async (ConfigurationService configuration, CancellationToken ct) =>
    Results.Ok(await configuration.GetFilesAsync(ct)));

api.MapGet("/config/file/{**path}", async (string path, ConfigurationService configuration, CancellationToken ct) =>
    Results.Ok(await configuration.GetAsync(path, ct)));

api.MapPut("/config/file/{**path}", async (string path, ConfigField[] fields, ConfigurationService configuration, CancellationToken ct) =>
{
    await configuration.UpdateAsync(path, fields, ct);
    return Results.NoContent();
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
