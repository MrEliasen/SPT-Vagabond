using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vagabond.Common.Definitions;
using Vagabond.Common.Enums;
using Vagabond.Server.Services;

namespace Vagabond.Server.Config;

public sealed class ExfilsConfigEntry
{
    public List<CustomExfil> Extracts { get; set; } = new();
    public List<CustomExfil> Transits { get; set; } = new();
}

internal sealed class RawExfilsConfigEntry
{
    public List<JsonElement> Extracts { get; set; } = new();
    public List<JsonElement> Transits { get; set; } = new();
}

public static class ExfilsConfig
{
    public static Dictionary<RaidLocation, ExfilsConfigEntry> Maps = new();

    public static void Initialize()
    {
        Maps = LoadAll();
    }

    private static Dictionary<RaidLocation, ExfilsConfigEntry> LoadAll()
    {
        var result = new Dictionary<RaidLocation, ExfilsConfigEntry>();

        foreach (var raid in Enum.GetValues<RaidLocation>())
        {
            if (raid == RaidLocation.Nil)
            {
                continue;
            }

            result[raid] = new ExfilsConfigEntry();
        }

        try
        {
            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ??
                              AppContext.BaseDirectory;
            var localDir = Path.Combine(assemblyDir, "config", "exfils");
            var siblingDir = Path.Combine(Directory.GetParent(assemblyDir)?.FullName ?? assemblyDir, "config",
                "exfils");

            var chosenDir = Directory.Exists(localDir) ? localDir : siblingDir;
            if (!Directory.Exists(chosenDir))
            {
                throw new Exception($"exfils config dir not found, tried {localDir} and {siblingDir}");
            }

            var options = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            foreach (var path in Directory.EnumerateFiles(chosenDir, "*.json"))
            {
                var fileKey = Path.GetFileNameWithoutExtension(path);
                if (!TryMatchRaid(fileKey, out var raid))
                {
                    VagabondLogger.Warning($"exfils config: unknown raid file '{fileKey}', skipping.");
                    continue;
                }

                RawExfilsConfigEntry? raw;
                try
                {
                    var json = File.ReadAllText(path);
                    raw = JsonSerializer.Deserialize<RawExfilsConfigEntry>(json, options);
                }
                catch (Exception ex)
                {
                    ConfigVerificationService.ReportSkippedRow($"exfils/{Path.GetFileName(path)}", 0, ex.Message);
                    continue;
                }

                if (raw == null)
                {
                    continue;
                }

                var entry = new ExfilsConfigEntry
                {
                    Extracts = ConvertDefinitions(raw.Extracts, options, Path.GetFileName(path), "extracts",
                        isTransit: false),
                    Transits = ConvertDefinitions(raw.Transits, options, Path.GetFileName(path), "transits",
                        isTransit: true)
                };

                result[raid] = entry;
            }
        }
        catch (Exception ex)
        {
            VagabondLogger.Error($"exfils config error, will use defaults. Error: {ex}");
        }

        return result;
    }

    private static List<CustomExfil> ConvertDefinitions(List<JsonElement> rows, JsonSerializerOptions options,
        string fileName, string section, bool isTransit)
    {
        var converted = new List<CustomExfil>(rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            try
            {
                var definition = rows[i].Deserialize<CustomExfil>(options);
                if (definition == null)
                {
                    ConfigVerificationService.ReportSkippedRow($"exfils/{fileName} {section}", i, "row is null.");
                    continue;
                }

                definition.IsTransit = isTransit;
                converted.Add(definition);
            }
            catch (Exception ex)
            {
                ConfigVerificationService.ReportSkippedRow($"exfils/{fileName} {section}", i,
                    $"{ex.Message} Row content: {rows[i].GetRawText()}");
            }
        }

        return converted;
    }

    private static bool TryMatchRaid(string fileKey, out RaidLocation raid)
    {
        var stripped = fileKey.Replace("_", string.Empty);
        foreach (var value in Enum.GetValues<RaidLocation>())
        {
            if (value == RaidLocation.Nil)
            {
                continue;
            }

            if (string.Equals(value.ToString(), stripped, StringComparison.OrdinalIgnoreCase))
            {
                raid = value;
                return true;
            }
        }

        raid = RaidLocation.Nil;
        return false;
    }
}