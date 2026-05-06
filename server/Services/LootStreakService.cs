using SPTarkov.Server.Core.Models.Common;
using Vagabond.Common.Data;
using Vagabond.Common.Definitions;
using Vagabond.Common.Enums;
using Vagabond.Server.Config;

namespace Vagabond.Server.Services;

public static class LootStreakService
{
    private static readonly AsyncLocal<double> Value = new();

    public static double CurrentMultiplier
    {
        get => Value.Value == 0 ? 1.0 : Value.Value;
        set => Value.Value = value;
    }

    public static string GetStreakMapName(string location)
    {
        var n = VagabondLocations.NormaliseMapName(location);
        if (n == RaidLocation.FactoryDay || n == RaidLocation.FactoryNight)
        {
            return "Factory";
        }

        return n.ToString();
    }

    public static double GetCurrentMultiplier(MongoId sessionId, string location)
    {
        if (!VagabondConfig.Config.EnableConsecutiveMapLootReduction)
        {
            return 1.0;
        }

        var state = StateService.GetState(sessionId);
        if (state.LastExtractMap != GetStreakMapName(location) || state.ConsecutiveExtractsSameMap <= 0)
        {
            return 1.0;
        }

        return Math.Max(VagabondConfig.Config.ConsecutiveMapLootRetentionMin,
            Math.Pow(VagabondConfig.Config.ConsecutiveMapLootRetentionRate, state.ConsecutiveExtractsSameMap));
    }

    public static void HandleSuccessfulExtract(VagabondSessionState state, string location)
    {
        if (!VagabondConfig.Config.EnableConsecutiveMapLootReduction)
        {
            return;
        }

        var key = GetStreakMapName(location);
        if (state.LastExtractMap == key)
        {
            state.ConsecutiveExtractsSameMap++;
            return;
        }

        state.LastExtractMap = key;
        state.ConsecutiveExtractsSameMap = 1;
    }
}