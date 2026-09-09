using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MinecraftAdmin.WebApp.Models;

namespace MinecraftAdmin.WebApp.Services;

public sealed class DockerService : IDisposable
{
    private readonly HttpClient http_;
    private readonly ServerManagementOptions options_;

    public DockerService(IOptions<ServerManagementOptions> options)
    {
        options_ = options.Value;

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(
                    AddressFamily.Unix,
                    SocketType.Stream,
                    ProtocolType.Unspecified);

                try
                {
                    await socket.ConnectAsync(
                        new UnixDomainSocketEndPoint(options_.DockerSocketPath),
                        ct);

                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        http_ = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://docker")
        };
    }

    public async Task<JsonObject> InspectContainerAsync(
        CancellationToken ct = default)
    {
        using var response = await http_.GetAsync(
            $"/containers/{Uri.EscapeDataString(options_.ContainerName)}/json",
            ct);

        await EnsureSuccessAsync(
            response,
            "inspect Minecraft container",
            ct);

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(
            cancellationToken: ct);

        return json
            ?? throw new InvalidOperationException(
                "Docker returned an empty container inspection response.");
    }

    public async Task StopContainerAsync(
        CancellationToken ct = default)
    {
        using var response = await http_.PostAsync(
            $"/containers/{Uri.EscapeDataString(options_.ContainerName)}/stop?t=30",
            null,
            ct);

        if (response.IsSuccessStatusCode ||
            (int)response.StatusCode == 304)
        {
            return;
        }

        await EnsureSuccessAsync(
            response,
            "stop Minecraft container",
            ct);
    }

    public async Task RemoveContainerAsync(
        CancellationToken ct = default)
    {
        using var response = await http_.DeleteAsync(
            $"/containers/{Uri.EscapeDataString(options_.ContainerName)}?force=true",
            ct);

        await EnsureSuccessAsync(
            response,
            "remove Minecraft container",
            ct);
    }

    public async Task CreateContainerAsync(
        JsonObject inspection,
        IReadOnlyCollection<string> oldManagedEnvironmentKeys,
        IReadOnlyDictionary<string, string> newManagedEnvironment,
        CancellationToken ct = default)
    {
        var body = BuildCreateRequest(
            inspection,
            oldManagedEnvironmentKeys,
            newManagedEnvironment);

        var content = new StringContent(
            body.ToJsonString(),
            Encoding.UTF8,
            "application/json");

        using var response = await http_.PostAsync(
            $"/containers/create?name={Uri.EscapeDataString(options_.ContainerName)}",
            content,
            ct);

        await EnsureSuccessAsync(
            response,
            "create Minecraft container",
            ct);
    }

    public async Task StartContainerAsync(
        CancellationToken ct = default)
    {
        using var response = await http_.PostAsync(
            $"/containers/{Uri.EscapeDataString(options_.ContainerName)}/start",
            null,
            ct);

        if (response.IsSuccessStatusCode ||
            (int)response.StatusCode == 304)
        {
            return;
        }

        await EnsureSuccessAsync(
            response,
            "start Minecraft container",
            ct);
    }

    private static JsonObject BuildCreateRequest(
        JsonObject inspection,
        IReadOnlyCollection<string> oldManagedEnvironmentKeys,
        IReadOnlyDictionary<string, string> newManagedEnvironment)
    {
        var config = inspection["Config"]?.AsObject()
            ?? throw new InvalidOperationException(
                "Docker inspection did not contain Config.");

        var inspectedHostConfig = inspection["HostConfig"]?.AsObject()
            ?? throw new InvalidOperationException(
                "Docker inspection did not contain HostConfig.");

        var networkMode =
            inspectedHostConfig["NetworkMode"]?.GetValue<string>();

        var sharesNetworkNamespace =
            networkMode?.StartsWith(
                "container:",
                StringComparison.Ordinal) == true;

        var request = new JsonObject();

        foreach (var property in config)
        {
            if (sharesNetworkNamespace &&
                IsContainerNetworkConfigConflict(property.Key))
            {
                continue;
            }

            request[property.Key] = property.Value?.DeepClone();
        }

        var replacedKeys = oldManagedEnvironmentKeys
            .Concat(newManagedEnvironment.Keys)
            .ToHashSet(StringComparer.Ordinal);

        var environment = new List<string>();

        if (config["Env"] is JsonArray inspectedEnvironment)
        {
            foreach (var item in inspectedEnvironment)
            {
                var entry = item?.GetValue<string>();

                if (string.IsNullOrEmpty(entry))
                {
                    continue;
                }

                var separator = entry.IndexOf('=');

                var key = separator >= 0
                    ? entry[..separator]
                    : entry;

                if (!replacedKeys.Contains(key))
                {
                    environment.Add(entry);
                }
            }
        }

        environment.AddRange(
            newManagedEnvironment.Select(
                pair => $"{pair.Key}={pair.Value}"));

        request["Env"] = new JsonArray(
            environment
                .Select(entry => (JsonNode?)JsonValue.Create(entry))
                .ToArray());

        var hostConfig = new JsonObject();

        var hostConfigFields = new[]
        {
            "Binds",
            "ContainerIDFile",
            "LogConfig",
            "NetworkMode",
            "PortBindings",
            "RestartPolicy",
            "AutoRemove",
            "VolumeDriver",
            "VolumesFrom",
            "ConsoleSize",
            "CapAdd",
            "CapDrop",
            "CgroupnsMode",
            "Dns",
            "DnsOptions",
            "DnsSearch",
            "ExtraHosts",
            "GroupAdd",
            "IpcMode",
            "Cgroup",
            "Links",
            "OomScoreAdj",
            "PidMode",
            "Privileged",
            "PublishAllPorts",
            "ReadonlyRootfs",
            "SecurityOpt",
            "StorageOpt",
            "Tmpfs",
            "UTSMode",
            "UsernsMode",
            "ShmSize",
            "Sysctls",
            "Runtime",
            "Isolation",
            "CpuShares",
            "Memory",
            "NanoCpus",
            "CgroupParent",
            "BlkioWeight",
            "BlkioWeightDevice",
            "BlkioDeviceReadBps",
            "BlkioDeviceWriteBps",
            "BlkioDeviceReadIOps",
            "BlkioDeviceWriteIOps",
            "CpuPeriod",
            "CpuQuota",
            "CpuRealtimePeriod",
            "CpuRealtimeRuntime",
            "CpusetCpus",
            "CpusetMems",
            "Devices",
            "DeviceCgroupRules",
            "DeviceRequests",
            "KernelMemoryTCP",
            "MemoryReservation",
            "MemorySwap",
            "MemorySwappiness",
            "OomKillDisable",
            "PidsLimit",
            "Ulimits",
            "CpuCount",
            "CpuPercent",
            "IOMaximumIOps",
            "IOMaximumBandwidth",
            "MaskedPaths",
            "ReadonlyPaths",
            "Init"
        };

        foreach (var name in hostConfigFields)
        {
            if (sharesNetworkNamespace &&
                IsContainerNetworkHostConfigConflict(name))
            {
                continue;
            }

            CopyIfPresent(
                inspectedHostConfig,
                hostConfig,
                name);
        }

        request["HostConfig"] = hostConfig;

        return request;
    }

    private static bool IsContainerNetworkConfigConflict(
        string name)
    {
        return name is
            "Hostname" or
            "Domainname" or
            "MacAddress" or
            "ExposedPorts";
    }

    private static bool IsContainerNetworkHostConfigConflict(
        string name)
    {
        return name is
            "PortBindings" or
            "PublishAllPorts" or
            "Dns" or
            "DnsOptions" or
            "DnsSearch" or
            "ExtraHosts";
    }

    private static void CopyIfPresent(
        JsonObject source,
        JsonObject destination,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (source.TryGetPropertyValue(name, out var value))
            {
                destination[name] = value?.DeepClone();
            }
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string action,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(ct);

        throw new InvalidOperationException(
            $"Failed to {action}: Docker returned {(int)response.StatusCode} " +
            $"{response.ReasonPhrase}. {detail}".Trim());
    }

    public void Dispose()
    {
        http_.Dispose();
    }
}