using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MinecraftAdmin.WebApp.Models;

namespace MinecraftAdmin.WebApp.Services;

public sealed partial class ConfigurationService(IOptions<MinecraftOptions> options)
{
    private readonly string dataPath_ = Path.GetFullPath(options.Value.DataPath);
    private readonly SemaphoreSlim mutex_ = new(1, 1);

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".properties", ".cfg", ".conf", ".toml", ".json", ".json5"
    };

    public async Task<IReadOnlyList<ConfigFileInfo>> GetFilesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var files = new List<ConfigFileInfo>();
        AddFile(files, Path.Combine(dataPath_, "server.properties"), "Server Files");
        AddDirectory(files, Path.Combine(dataPath_, "defaultconfigs"), "Default Configs", ct);
        AddDirectory(files, Path.Combine(dataPath_, "config"), "Mod Configs", ct);

        var orderedFiles = files
            .OrderBy(x => GetGroupOrder(x.Group))
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in orderedFiles)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var document = await GetAsync(file.Path, ct);
                file.SearchText = BuildSearchText(file, document);
            }
            catch
            {
                // A malformed/temporarily unavailable config should still appear in the navigator.
                file.SearchText = $"{file.Name} {file.Path}";
            }
        }

        return orderedFiles;
    }

    private static int GetGroupOrder(string group) => group switch
    {
        "Server Files" => 0,
        "Default Configs" => 1,
        "Mod Configs" => 2,
        _ => int.MaxValue
    };

    private static string BuildSearchText(ConfigFileInfo file, ConfigDocument document)
    {
        var keys = document.Fields.Select(field => field.Key);
        var sections = document.Fields
            .Select(field => field.Section)
            .Where(section => !string.IsNullOrWhiteSpace(section));

        return string.Join(' ', new[] { file.Name, file.Path }.Concat(keys).Concat(sections!));
    }

    public async Task<ConfigDocument> GetAsync(string relativePath, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Configuration file was not found.", relativePath);

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
            throw new InvalidOperationException($"Unsupported configuration format '{extension}'.");

        return extension switch
        {
            ".json" => await ReadJsonAsync(relativePath, fullPath, ct),
            ".json5" => await ReadLineConfigAsync(relativePath, fullPath, "JSON5", ct),
            ".toml" => await ReadLineConfigAsync(relativePath, fullPath, "TOML", ct),
            _ => await ReadLineConfigAsync(relativePath, fullPath, "Properties", ct)
        };
    }

    public async Task UpdateAsync(string relativePath, IReadOnlyCollection<ConfigField> fields, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Configuration file was not found.", relativePath);

        await mutex_.WaitAsync(ct);
        try
        {
            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            if (extension == ".json")
                await UpdateJsonAsync(fullPath, fields, ct);
            else if (SupportedExtensions.Contains(extension))
                await UpdateLineConfigAsync(fullPath, fields, ct);
            else
                throw new InvalidOperationException($"Unsupported configuration format '{extension}'.");
        }
        finally
        {
            mutex_.Release();
        }
    }

    private void AddDirectory(List<ConfigFileInfo> files, string directory, string group, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            AddFile(files, file, group);
        }
    }

    private void AddFile(List<ConfigFileInfo> files, string fullPath, string group)
    {
        if (!File.Exists(fullPath) || !SupportedExtensions.Contains(Path.GetExtension(fullPath)))
            return;

        var relative = Path.GetRelativePath(dataPath_, fullPath).Replace('\\', '/');
        files.Add(new ConfigFileInfo
        {
            Path = relative,
            Name = Path.GetFileName(relative),
            Group = group,
            Format = GetFormatName(Path.GetExtension(fullPath))
        });
    }

    private string ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("A configuration path is required.", nameof(relativePath));

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(dataPath_, normalized));
        var rootWithSeparator = dataPath_.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal) && !string.Equals(fullPath, dataPath_, StringComparison.Ordinal))
            throw new InvalidOperationException("Configuration path is outside the Minecraft data directory.");

        var allowed = string.Equals(Path.GetFileName(fullPath), "server.properties", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(fullPath), dataPath_, StringComparison.Ordinal)
            || IsInside(fullPath, Path.Combine(dataPath_, "config"))
            || IsInside(fullPath, Path.Combine(dataPath_, "defaultconfigs"));

        if (!allowed)
            throw new InvalidOperationException("Only server.properties, config/, and defaultconfigs/ can be edited.");

        return fullPath;
    }

    private static bool IsInside(string path, string directory)
    {
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(fullDirectory, StringComparison.Ordinal);
    }

    private static async Task<ConfigDocument> ReadJsonAsync(string relativePath, string fullPath, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(fullPath, ct);
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) ?? new JsonObject();

        var fields = new List<ConfigField>();
        FlattenJson(node, "$", null, fields);

        return new ConfigDocument
        {
            Path = relativePath,
            Name = Path.GetFileName(relativePath),
            Format = "JSON",
            Fields = fields
        };
    }

    private static void FlattenJson(JsonNode node, string path, string? section, List<ConfigField> fields)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Value is null)
                    continue;

                var childPath = path + "/" + EscapeJsonPointer(pair.Key);
                if (pair.Value is JsonValue value && TryGetJsonScalar(value, out var kind, out var text))
                {
                    fields.Add(new ConfigField
                    {
                        Id = childPath,
                        Key = pair.Key,
                        Section = section,
                        Kind = kind,
                        Value = text
                    });
                }
                else
                {
                    FlattenJson(pair.Value, childPath, path == "$" ? pair.Key : section is null ? pair.Key : $"{section} / {pair.Key}", fields);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; ++i)
            {
                var child = array[i];
                if (child is null)
                    continue;

                var childPath = path + "/" + i.ToString(CultureInfo.InvariantCulture);
                if (child is JsonValue value && TryGetJsonScalar(value, out var kind, out var text))
                {
                    fields.Add(new ConfigField
                    {
                        Id = childPath,
                        Key = $"[{i}]",
                        Section = section,
                        Kind = kind,
                        Value = text
                    });
                }
                else
                {
                    FlattenJson(child, childPath, section, fields);
                }
            }
        }
    }

    private static bool TryGetJsonScalar(JsonValue value, out ConfigValueKind kind, out string text)
    {
        if (value.TryGetValue<bool>(out var boolean))
        {
            kind = ConfigValueKind.Boolean;
            text = boolean.ToString().ToLowerInvariant();
            return true;
        }
        if (value.TryGetValue<long>(out var integer))
        {
            kind = ConfigValueKind.Integer;
            text = integer.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (value.TryGetValue<double>(out var number))
        {
            kind = ConfigValueKind.Decimal;
            text = number.ToString("R", CultureInfo.InvariantCulture);
            return true;
        }
        if (value.TryGetValue<string>(out var str))
        {
            kind = ConfigValueKind.String;
            text = str ?? string.Empty;
            return true;
        }

        kind = default;
        text = string.Empty;
        return false;
    }

    private static async Task<ConfigDocument> ReadLineConfigAsync(string relativePath, string fullPath, string format, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(fullPath, ct);
        var fields = new List<ConfigField>();
        string? section = null;
        var comments = new List<string>();

        for (var i = 0; i < lines.Length; ++i)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                comments.Clear();
                continue;
            }

            if (IsComment(trimmed))
            {
                comments.Add(StripComment(trimmed));
                continue;
            }

            if (format == "TOML" && trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed.Trim('[', ']').Trim();
                comments.Clear();
                continue;
            }

            if (!TryParseLine(format, line, out var key, out var valueText))
            {
                comments.Clear();
                continue;
            }

            var value = StripInlineComment(valueText, format).Trim();
            if (format == "JSON5")
                value = value.TrimEnd().TrimEnd(',').TrimEnd();

            if (!TryParseScalar(value, format, out var kind, out var editableValue))
            {
                comments.Clear();
                continue;
            }

            fields.Add(new ConfigField
            {
                Id = $"line:{i}",
                Key = key,
                Section = section,
                Description = comments.Count == 0 ? null : string.Join(' ', comments),
                Kind = kind,
                Value = editableValue
            });
            comments.Clear();
        }

        return new ConfigDocument
        {
            Path = relativePath,
            Name = Path.GetFileName(relativePath),
            Format = format,
            Fields = fields
        };
    }

    private static bool TryParseLine(string format, string line, out string key, out string value)
    {
        if (format == "JSON5")
        {
            var match = Json5PropertyRegex().Match(line);
            if (match.Success)
            {
                key = match.Groups["key"].Value.Trim().Trim('"', '\'');
                value = match.Groups["value"].Value;
                return true;
            }
        }
        else
        {
            var equals = line.IndexOf('=');
            if (equals > 0)
            {
                key = line[..equals].Trim();
                value = line[(equals + 1)..];
                return true;
            }
        }

        key = string.Empty;
        value = string.Empty;
        return false;
    }

    private static bool TryParseScalar(string raw, string format, out ConfigValueKind kind, out string value)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            kind = ConfigValueKind.String;
            value = string.Empty;
            return true;
        }

        if (bool.TryParse(trimmed, out var boolean))
        {
            kind = ConfigValueKind.Boolean;
            value = boolean.ToString().ToLowerInvariant();
            return true;
        }

        if (long.TryParse(trimmed.Replace("_", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            kind = ConfigValueKind.Integer;
            value = integer.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (double.TryParse(trimmed.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            kind = ConfigValueKind.Decimal;
            value = number.ToString("R", CultureInfo.InvariantCulture);
            return true;
        }

        if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) || (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
        {
            kind = ConfigValueKind.String;
            value = Unquote(trimmed);
            return true;
        }

        if (format == "Properties")
        {
            kind = ConfigValueKind.String;
            value = trimmed;
            return true;
        }

        kind = default;
        value = string.Empty;
        return false;
    }

    private static async Task UpdateJsonAsync(string fullPath, IReadOnlyCollection<ConfigField> fields, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(fullPath, ct);
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) ?? new JsonObject();

        foreach (var field in fields)
            SetJsonValue(node, field.Id, ToJsonValue(field));

        var output = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(fullPath, output + Environment.NewLine, ct);
    }

    private static void SetJsonValue(JsonNode root, string path, JsonNode? replacement)
    {
        var tokens = ParseJsonPointer(path);
        if (tokens.Count == 0)
            return;

        JsonNode current = root;
        for (var i = 0; i < tokens.Count - 1; ++i)
        {
            var token = tokens[i];
            current = current switch
            {
                JsonArray array when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) =>
                    array[index] ?? throw new InvalidOperationException($"Invalid JSON path '{path}'."),
                JsonObject obj => obj[token] ?? throw new InvalidOperationException($"Invalid JSON path '{path}'."),
                _ => throw new InvalidOperationException($"Invalid JSON path '{path}'.")
            };
        }

        var last = tokens[^1];
        if (current is JsonArray targetArray && int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var arrayIndex))
            targetArray[arrayIndex] = replacement;
        else if (current is JsonObject targetObject)
            targetObject[last] = replacement;
        else
            throw new InvalidOperationException($"Invalid JSON path '{path}'.");
    }

    private static JsonNode? ToJsonValue(ConfigField field) => field.Kind switch
    {
        ConfigValueKind.Boolean => JsonValue.Create(ParseBoolean(field.Value)),
        ConfigValueKind.Integer => JsonValue.Create(long.Parse(field.Value, CultureInfo.InvariantCulture)),
        ConfigValueKind.Decimal => JsonValue.Create(double.Parse(field.Value, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(field.Value)
    };

    private static string EscapeJsonPointer(string value) => value.Replace("~", "~0").Replace("/", "~1");
    private static string UnescapeJsonPointer(string value) => value.Replace("~1", "/").Replace("~0", "~");

    private static List<string> ParseJsonPointer(string path)
    {
        if (!path.StartsWith("$/", StringComparison.Ordinal))
            return [];

        return path[2..].Split('/').Select(UnescapeJsonPointer).ToList();
    }

    private static async Task UpdateLineConfigAsync(string fullPath, IReadOnlyCollection<ConfigField> fields, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(fullPath, ct);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        var format = extension switch { ".json5" => "JSON5", ".toml" => "TOML", _ => "Properties" };

        foreach (var field in fields)
        {
            if (!field.Id.StartsWith("line:", StringComparison.Ordinal) || !int.TryParse(field.Id[5..], out var lineIndex) || lineIndex < 0 || lineIndex >= lines.Length)
                continue;

            var line = lines[lineIndex];
            if (!TryParseLine(format, line, out _, out var oldValue))
                continue;

            var oldScalar = StripInlineComment(oldValue, format).Trim();
            if (format == "JSON5")
                oldScalar = oldScalar.TrimEnd().TrimEnd(',').TrimEnd();
            var formatted = FormatScalar(field, format, oldScalar);
            lines[lineIndex] = ReplaceLineValue(line, format, formatted);
        }

        await File.WriteAllLinesAsync(fullPath, lines, ct);
    }

    private static string ReplaceLineValue(string line, string format, string value)
    {
        if (format == "JSON5")
        {
            var match = Json5PropertyRegex().Match(line);
            if (!match.Success)
                return line;

            var oldValue = match.Groups["value"].Value;
            var commentIndex = FindInlineComment(oldValue, format);
            var beforeComment = commentIndex < 0 ? oldValue : oldValue[..commentIndex];
            var comment = commentIndex < 0 ? string.Empty : oldValue[commentIndex..];
            var suffix = beforeComment.TrimEnd().EndsWith(',') ? "," : string.Empty;
            var spacingBeforeComment = comment.Length == 0 ? string.Empty : " ";
            var leading = line[..match.Groups["value"].Index];
            return leading + value + suffix + spacingBeforeComment + comment.TrimStart();
        }

        var equals = line.IndexOf('=');
        if (equals < 0)
            return line;

        var existing = line[(equals + 1)..];
        var inlineComment = GetInlineComment(existing, format);
        return line[..(equals + 1)] + value + inlineComment;
    }

    private static string FormatScalar(ConfigField field, string format, string oldRaw)
    {
        ValidateField(field);
        return field.Kind switch
        {
            ConfigValueKind.Boolean => ParseBoolean(field.Value).ToString().ToLowerInvariant(),
            ConfigValueKind.Integer => long.Parse(field.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            ConfigValueKind.Decimal => double.Parse(field.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
            ConfigValueKind.String when format is "TOML" or "JSON5" => Quote(field.Value, oldRaw.StartsWith('\'') ? '\'' : '"'),
            _ => field.Value
        };
    }

    private static void ValidateField(ConfigField field)
    {
        _ = field.Kind switch
        {
            ConfigValueKind.Boolean => ParseBoolean(field.Value),
            ConfigValueKind.Integer => long.Parse(field.Value, CultureInfo.InvariantCulture) != long.MinValue,
            ConfigValueKind.Decimal => !double.IsNaN(double.Parse(field.Value, CultureInfo.InvariantCulture)),
            _ => true
        };
    }

    private static bool ParseBoolean(string value) => bool.TryParse(value, out var result)
        ? result
        : throw new FormatException($"'{value}' is not a valid boolean value.");

    private static string Quote(string value, char quote)
    {
        var escaped = value.Replace("\\", "\\\\");
        escaped = quote == '"' ? escaped.Replace("\"", "\\\"") : escaped.Replace("'", "\\'");
        return $"{quote}{escaped}{quote}";
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2)
            return value;
        return value[1..^1].Replace("\\\"", "\"").Replace("\\'", "'").Replace("\\\\", "\\");
    }

    private static bool IsComment(string trimmed) => trimmed.StartsWith('#') || trimmed.StartsWith("//");
    private static string StripComment(string trimmed) => trimmed.TrimStart('#', '/').Trim();

    private static string StripInlineComment(string value, string format)
    {
        if (format == "Properties")
            return value;

        var index = FindInlineComment(value, format);
        return index < 0 ? value : value[..index];
    }

    private static string GetInlineComment(string value, string format)
    {
        if (format == "Properties")
            return string.Empty;

        var index = FindInlineComment(value, format);
        return index < 0 ? string.Empty : value[index..];
    }

    private static int FindInlineComment(string value, string format)
    {
        var quote = '\0';
        for (var i = 0; i < value.Length; ++i)
        {
            var c = value[i];
            if ((c == '"' || c == '\'') && (i == 0 || value[i - 1] != '\\'))
            {
                quote = quote == '\0' ? c : quote == c ? '\0' : quote;
                continue;
            }
            if (quote != '\0')
                continue;
            if (c == '#')
                return i;
            if (format == "JSON5" && c == '/' && i + 1 < value.Length && value[i + 1] == '/')
                return i;
        }
        return -1;
    }

    private static string GetFormatName(string extension) => extension.ToLowerInvariant() switch
    {
        ".json" => "JSON",
        ".json5" => "JSON5",
        ".toml" => "TOML",
        _ => "Properties"
    };

    [GeneratedRegex("""^\s*(?<key>(?:[A-Za-z0-9_.-]+)|(?:"[^"]+")|(?:'[^']+'))\s*:\s*(?<value>.+?)\s*$""")]
    private static partial Regex Json5PropertyRegex();


}
