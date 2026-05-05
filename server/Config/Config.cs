using System.Reflection;
using System.Text.Json;
using Vagabond.Server.Services;

namespace Vagabond.Server.Config;

public sealed class VagabondConfig
{
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
    public bool EnableVirtualStashes { get; set; } = true;
    public bool WipeVirtualStashesOnRaidEntry { get; set; }
    public bool AllowPostRaidHealing { get; set; } = true;
    public bool HealStatusEffectsOnDeath { get; set; } = true;
    public string OnDeathGoTo { get; set; } = "hideout";
    public string OnDeathGoToRaid { get; set; } = "";
    public string OnDeathGoToExfilIdentifier { get; set; } = "";
    public string StartRaid { get; set; } = "Streets";
    public string StartExfilIdentifier { get; set; } = "VGB_EXT_FENCE";
    public int HideoutRelocationFee { get; set; } = 350_000;
    public int JoinHideoutTherapistLoyaltyLevel { get; set; } = 2;
    public int JoinHideoutJaegerLoyaltyLevel { get; set; } = 2;
    public int JoinHideoutMechanicLoyaltyLevel { get; set; } = 2;
    public int JoinHideoutPeacekeeperLoyaltyLevel { get; set; } = 2;
    public int JoinHideoutPraporLoyaltyLevel { get; set; } = 2;
    public int JoinHideoutRagmanLoyaltyLevel { get; set; } = 2;
    public int JoinHideoutSkierLoyaltyLevel { get; set; } = 2;
    public bool WipeStashOnFirstRaidEntry { get; set; } = true;
    public bool LimitTraderMailAccess { get; set; } = true;
    public bool EnableConsecutiveMapLootReduction { get; set; } = true;
    public double ConsecutiveMapLootReductionRate { get; set; } = 0.5;
    public double ConsecutiveMapLootReductionMin { get; set; } = 0.05;
    public double HealthOnDeath { get; set; }
    public bool ForceGroundZeroHigh { get; set; }

    // internal
    public static VagabondConfig Config = new();

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