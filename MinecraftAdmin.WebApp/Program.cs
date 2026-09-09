using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using MinecraftAdmin.WebApp.Components;
using MinecraftAdmin.WebApp.Models;
using MinecraftAdmin.WebApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<MinecraftOptions>(builder.Configuration.GetSection("Minecraft"));
builder.Services.Configure<ServerManagementOptions>(builder.Configuration.GetSection("ServerManagement"));
builder.Services.AddSingleton<RconClient>();
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton<MinecraftService>();
builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<ModpackService>();

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

api.MapGet("/modpack", async (ModpackService modpacks, CancellationToken ct) =>
    Results.Ok(await modpacks.GetConfigurationAsync(ct)));

api.MapGet("/modpacks", async (ModpackService modpacks, CancellationToken ct) =>
    Results.Ok(await modpacks.GetLibraryAsync(ct)));

api.MapPost("/modpacks/upload", UploadModpackAsync)
    .DisableAntiforgery();

api.MapPost("/modpack/switch", async (ModpackSwitchRequest request, ModpackService modpacks, CancellationToken ct) =>
    Results.Ok(await modpacks.SwitchAsync(request, ct: ct)));

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

static async Task<IResult> UploadModpackAsync(
    HttpContext context,
    ModpackService modpacks,
    CancellationToken ct)
{
    try
    {
        var maxBodySize = checked(modpacks.MaxUploadBytes + 4L * 1024 * 1024);
        var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
            bodySizeFeature.MaxRequestBodySize = maxBodySize;

        if (string.IsNullOrWhiteSpace(context.Request.ContentType))
            return Results.BadRequest(new { error = "The upload request is missing a Content-Type header." });

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "The upload must use multipart/form-data." });
        }

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            return Results.BadRequest(new { error = "The multipart request is missing a boundary." });

        if (boundary.Length > 256)
            return Results.BadRequest(new { error = "The multipart boundary is too long." });

        var reader = new MultipartReader(boundary, context.Request.Body)
        {
            BodyLengthLimit = modpacks.MaxUploadBytes
        };

        string? name = null;
        string? environment = null;
        long? fileSize = null;

        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            ct.ThrowIfCancellationRequested();

            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value;
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = HeaderUtilities.RemoveQuotes(disposition.FileName).Value;

            if (!string.IsNullOrWhiteSpace(fileName))
            {
                if (!string.Equals(fieldName, "serverPack", StringComparison.Ordinal))
                    continue;

                if (name is null || environment is null || fileSize is null)
                    return Results.BadRequest(new { error = "The upload metadata must be sent before the server pack file." });

                if (fileSize.Value < 0 || fileSize.Value > modpacks.MaxUploadBytes)
                    return Results.BadRequest(new { error = $"The upload exceeds the configured limit of {FormatBytes(modpacks.MaxUploadBytes)}." });

                var uploaded = await modpacks.UploadAsync(
                    new ModpackUploadRequest(name, environment),
                    section.Body,
                    fileName,
                    fileSize.Value,
                    progress: null,
                    ct);

                return Results.Ok(uploaded);
            }

            switch (fieldName)
            {
                case "name":
                    name = await ReadTextSectionAsync(section.Body, 16 * 1024, ct);
                    break;

                case "environment":
                    environment = await ReadTextSectionAsync(section.Body, 1024 * 1024, ct);
                    break;

                case "fileSize":
                {
                    var text = await ReadTextSectionAsync(section.Body, 128, ct);
                    if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSize))
                        return Results.BadRequest(new { error = "The uploaded file size is invalid." });

                    fileSize = parsedSize;
                    break;
                }
            }
        }

        return Results.BadRequest(new { error = "No server pack ZIP was included in the upload." });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(new { error = $"The upload request is invalid: {ex.Message}" });
    }
}

static async Task<string> ReadTextSectionAsync(Stream stream, int maxBytes, CancellationToken ct)
{
    await using var buffer = new MemoryStream();
    var chunk = new byte[8 * 1024];

    while (true)
    {
        var read = await stream.ReadAsync(chunk, ct);
        if (read == 0)
            break;

        if (buffer.Length + read > maxBytes)
            throw new InvalidOperationException("An upload metadata field is too large.");

        await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
    }

    return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
}

static string FormatBytes(long bytes)
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
