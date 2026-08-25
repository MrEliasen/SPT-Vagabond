using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vagabond.Common.Definitions;
using Vagabond.Server.Services;

namespace Vagabond.Server.Config;

public static class TraderLocationsConfig
{
    private const string FileName = "trader_locations.json";

    public static List<TraderLocation> Locations = new();

    public static void Initialize()
    {
        Locations = LoadConfig();
    }

    private static List<TraderLocation> LoadConfig()
    {
        var result = new List<TraderLocation>();

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
                throw new Exception($"{FileName} config not found, tried {localConfig} and {siblingConfig}");
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

            for (var i = 0; i < rows.Count; i++)
            {
                try
                {
                    var row = rows[i].Deserialize<TraderLocation>(options);
                    if (row == null)
                    {
                        ConfigVerificationService.ReportSkippedRow(FileName, i, "row is null.");
                        continue;
                    }

                    result.Add(row);
                }
                catch (Exception ex)
                {
                    ConfigVerificationService.ReportSkippedRow(FileName, i,
                        $"{ex.Message} Row content: {rows[i].GetRawText()}");
                }
            }
        }
        catch (Exception ex)
        {
            VagabondLogger.Error($"{FileName} config error, will use empty. Error: {ex}");
        }

        return result;
    }
}