using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vagabond.Common.Enums;
using Vagabond.Common.Models;
using Vagabond.Server.Services;

namespace Vagabond.Server.Config;

public sealed class StaticTransitionEntry
{
    public RaidLocation From { get; set; }
    public RaidLocation To { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Rotation { get; set; }
}

public static class StaticTransitionsConfig
{
    private const string FileName = "static_transitions.json";

    private static Dictionary<(RaidLocation, RaidLocation), ManualSpawnPoint> _spawns = new();

    public static void Initialize()
    {
        _spawns = LoadConfig();
    }

    public static ManualSpawnPoint? GetSpawn(RaidLocation from, RaidLocation to)
    {
        return _spawns.TryGetValue((from, to), out var spawn) ? spawn : null;
    }

    private static Dictionary<(RaidLocation, RaidLocation), ManualSpawnPoint> LoadConfig()
    {
        try
        {
            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ??
                              AppContext.BaseDirectory;
            var localConfig = Path.Combine(assemblyDir, "config", FileName);
            var siblingConfig = Path.Combine(Directory.GetParent(assemblyDir)?.FullName ?? assemblyDir, "config",
                FileName);

            var chosen = File.Exists(localConfig) ? localConfig : siblingConfig;
            if (!File.Exists(chosen))
            {
                throw new Exception(
                    $"{FileName} config not found, tried {localConfig} and {siblingConfig}");
            }

            var options = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            var json = File.ReadAllText(chosen);
            var rows = JsonSerializer.Deserialize<List<JsonElement>>(json, options)
                       ?? throw new Exception($"failed to read {chosen}");

            var result = new Dictionary<(RaidLocation, RaidLocation), ManualSpawnPoint>();
            for (var i = 0; i < rows.Count; i++)
            {
                StaticTransitionEntry? entry;
                try
                {
                    entry = rows[i].Deserialize<StaticTransitionEntry>(options);
                }
                catch (Exception ex)
                {
                    ConfigVerificationService.ReportSkippedRow(FileName, i,
                        $"{ex.Message} Row content: {rows[i].GetRawText()}");
                    continue;
                }

                if (entry == null)
                {
                    ConfigVerificationService.ReportSkippedRow(FileName, i, "row is null.");
                    continue;
                }

                if (entry.From == RaidLocation.Nil || entry.To == RaidLocation.Nil)
                {
                    ConfigVerificationService.ReportSkippedRow(FileName, i,
                        $"from/to must both be supported raid names, got '{entry.From}' -> '{entry.To}'.");
                    continue;
                }

                result[(entry.From, entry.To)] = new ManualSpawnPoint
                {
                    X = entry.X,
                    Y = entry.Y,
                    Z = entry.Z,
                    Rotation = entry.Rotation
                };
            }

            return result;
        }
        catch (Exception ex)
        {
            VagabondLogger.Error($"{FileName} config error, will use empty. Error: {ex}");
            return new Dictionary<(RaidLocation, RaidLocation), ManualSpawnPoint>();
        }
    }
}