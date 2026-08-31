using System.Reflection;
using System.Text.Json;
using Vagabond.Common.Data;
using Vagabond.Common.Enums;
using Vagabond.Server.Services;

namespace Vagabond.Server.Config;

public sealed class VagabondConfig
{
    public const string DefaultStartRaid = "Streets";
    public const string DefaultStartExfilIdentifier = "VGB_EXT_FENCE";

    public bool ResetOnDeath { get; set; }
    public bool DisableEvents { get; set; } = true;
    public int StartingRoubles { get; set; } = 175_000;
    public int AdjustRaidTimeMins { get; set; } = 60;
    public bool EnableFenceChanges { get; set; } = true;
    public bool DisableFlea { get; set; } = true;
    public string MailAttachmentLimit { get; set; } = "same-exit";
    public bool AllowHideoutRelocation { get; set; }
    public bool EnablePickRaidLocation { get; set; }
    public bool AddFenceToHideout { get; set; }
    public bool ShareHideoutExits { get; set; }
    public bool LimitHideoutAccessToHideoutExfil { get; set; }
    public bool EnableVirtualStashes { get; set; } = true;
    public bool WipeVirtualStashesOnRaidEntry { get; set; }
    public bool AllowPostRaidHealing { get; set; } = true;
    public bool HealStatusEffectsOnDeath { get; set; } = true;
    public string OnDeathGoTo { get; set; } = "hideout";
    public string OnDeathGoToRaid { get; set; } = "";
    public string OnDeathGoToExfilIdentifier { get; set; } = "";
    public string StartRaid { get; set; } = "Streets";
    public string StartExfilIdentifier { get; set; } = "VGB_EXT_FENCE";
    public bool WipeStashOnFirstRaidEntry { get; set; } = true;
    public bool LimitTraderMailAccess { get; set; } = true;
    public bool EnableConsecutiveMapLootReduction { get; set; } = true;
    public double ConsecutiveMapLootRetentionRate { get; set; } = 0.5;
    public double ConsecutiveMapLootRetentionMin { get; set; } = 0.05;
    public double HealthOnDeath { get; set; }
    public double EnergyOnDeath { get; set; }
    public double WaterOnDeath { get; set; }
    public bool ForceGroundZeroHigh { get; set; }

    public List<string> IgnoredTraders { get; set; } =
    [
        "656f0f98d80a697f855d34b1", // BTR Driver
        "638f541a29ffd1183d187f57", // Lightkeeper
        "6864e812f9fe664cb8b8e152", // Storyteller
        "69e0d6cc77b63940375b9173", // Survivor
        "68fe15910f29ba3fdbba9d54", // Taran
        "688246958448b05efd61d462", // Voevoda
        "688246518448b05efd61d461", // Mr. Kerman
        "68fe15990f29ba3fdbba9d55", // Radio station
        "6617beeaa9cfa777ca915b7c" // Ref
    ];

    public static VagabondConfig Config = new();

    public static (string Raid, string ExfilIdentifier) GetStartLocation()
    {
        var raid = VagabondLocations.NormaliseMapName(Config.StartRaid);
        if (raid == RaidLocation.Nil)
        {
            VagabondLogger.Warning(
                $"StartRaid `{Config.StartRaid}` is not a valid raid name; using {DefaultStartRaid} / " +
                $"{DefaultStartExfilIdentifier} instead.");
            return (DefaultStartRaid, DefaultStartExfilIdentifier);
        }

        if (!ExfilsConfig.Maps.TryGetValue(raid, out var entry) ||
            !entry.Extracts.Exists(x =>
                string.Equals(x.Identifier, Config.StartExfilIdentifier, StringComparison.OrdinalIgnoreCase)))
        {
            VagabondLogger.Warning(
                $"StartExfilIdentifier `{Config.StartExfilIdentifier}` does not exist in {raid} exfils; " +
                $"using {DefaultStartRaid} / {DefaultStartExfilIdentifier} instead.");
            return (DefaultStartRaid, DefaultStartExfilIdentifier);
        }

        return (raid.ToString(), Config.StartExfilIdentifier);
    }

    public static void Initialize()
    {
        Config = LoadConfig();
    }

    private static VagabondConfig LoadConfig()
    {
        try
        {
            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ??
                              AppContext.BaseDirectory;
            var localConfig = Path.Combine(assemblyDir, "config", "vagabond.json");
            var siblingConfig = Path.Combine(Directory.GetParent(assemblyDir)?.FullName ?? assemblyDir, "config",
                "vagabond.json");

            var chosen = File.Exists(localConfig) ? localConfig : siblingConfig;
            if (!File.Exists(chosen))
            {
                throw new Exception($"vagabond.json config not found, tried {localConfig} and {siblingConfig}");
            }

            var json = File.ReadAllText(chosen);
            return JsonSerializer.Deserialize<VagabondConfig>(json, new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true
            }) ?? throw new Exception($"failed to read {chosen}");
        }
        catch (Exception ex)
        {
            VagabondLogger.Error($"config error, will use default. Error: {ex}");
            return new VagabondConfig();
        }
    }
}