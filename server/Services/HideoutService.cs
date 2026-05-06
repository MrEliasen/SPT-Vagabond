using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Vagabond.Common.Data;
using Vagabond.Common.Definitions;
using Vagabond.Common.Enums;
using Vagabond.Server.Config;

namespace Vagabond.Server.Services;

internal static class HideoutService
{
    public const string HideoutIdPrefix = "VGB_HO_";

    public const string HideoutNamePrefix = "Hideout Entrance";

    // access is not changed by extraction.
    private static readonly List<string> IgnoredTraders =
    [
        "656f0f98d80a697f855d34b1", // BTR Driver
        "688246518448b05efd61d461", // Mr. Kerman
        "638f541a29ffd1183d187f57", // Lightkeeper
        "68fe15990f29ba3fdbba9d55", // Radio station
        "68fe15910f29ba3fdbba9d54", // Taran
        "688246958448b05efd61d462", // Voevoda
    ];

    public static List<TraderLocation> TraderLocations = new();

    public static void LoadTraderLocations(IEnumerable<TraderLocation> seed)
    {
        TraderLocations = new List<TraderLocation>(seed);
    }

    public static IReadOnlyCollection<string> GetAllTraderIds()
    {
        return TraderLocations
            .Select(x => x.TraderId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static string? GetCurrentTraderId(VagabondSessionState state)
    {
        var raid = VagabondLocations.NormaliseMapName(state.CurrentMap);
        if (raid == RaidLocation.Nil || string.IsNullOrWhiteSpace(state.LastExit))
        {
            return null;
        }

        return TraderLocations.FirstOrDefault(x =>
            x.Raid == raid
            && string.Equals(x.ExfilIdentifier, state.LastExit, StringComparison.OrdinalIgnoreCase))?.TraderId;
    }

    public static IReadOnlyCollection<string> GetTraderIds(VagabondSessionState state)
    {
        var raid = VagabondLocations.NormaliseMapName(state.CurrentMap);
        if (raid == RaidLocation.Nil || string.IsNullOrWhiteSpace(state.LastExit))
        {
            return [];
        }

        return TraderLocations
            .Where(x => x.Raid == raid
                        && string.Equals(x.ExfilIdentifier, state.LastExit, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.TraderId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyCollection<string> GetStashKeys()
    {
        return TraderLocations
            .Select(x => x.ExfilIdentifier)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void UpdateTraderAccess(PmcData pmc, VagabondSessionState state)
    {
        var currentTraderIds = new HashSet<string>(GetTraderIds(state), StringComparer.Ordinal);
        var tradersInfo = pmc.TradersInfo;
        var isOwnHideout = !string.IsNullOrEmpty(state.HideoutState?.Id) &&
                           state.LastExit == $"{HideoutIdPrefix}{state.HideoutState?.Id}";

        foreach (KeyValuePair<MongoId, TraderInfo> entry in tradersInfo)
        {
            if (IgnoredTraders.Contains(entry.Key))
            {
                continue;
            }

            if (currentTraderIds.Contains(entry.Key))
            {
                entry.Value.Disabled = false;
                entry.Value.Unlocked = true;
                continue;
            }

            if (VagabondConfig.Config.AddFenceToHideout && entry.Key == "579dc571d53a0658a154fbec")
            {
                if (state.LastExit.IndexOf(HideoutIdPrefix, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    entry.Value.Disabled = false;
                    entry.Value.Unlocked = true;
                    continue;
                }
            }

            if (isOwnHideout && state.HideoutTraders.Contains(entry.Key))
            {
                entry.Value.Disabled = false;
                entry.Value.Unlocked = true;
                continue;
            }

            entry.Value.Disabled = true;
            entry.Value.Unlocked = true;
        }
    }

    /// <summary>
    /// API: add or replace trader locations. Entries with an ExfilIdentifier matching an existing one replace it.
    /// </summary>
    internal static void AddTraderLocations(List<TraderLocation> extractions)
    {
        var newTraderLocations = new List<TraderLocation>(extractions);

        var ids = new HashSet<string>(
            newTraderLocations.Select(t => t.ExfilIdentifier),
            StringComparer.OrdinalIgnoreCase);
        TraderLocations.RemoveAll(x => ids.Contains(x.ExfilIdentifier));
        TraderLocations.AddRange(newTraderLocations);
    }

    /// <summary>
    /// API: remove a trader location by ExfilIdentifier. Returns true if removed.
    /// </summary>
    internal static bool RemoveTraderLocation(string exfilIdentifier)
    {
        if (string.IsNullOrWhiteSpace(exfilIdentifier))
        {
            return false;
        }

        return TraderLocations.RemoveAll(x =>
            string.Equals(x.ExfilIdentifier, exfilIdentifier, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>
    /// API: returns all current registered trader locations.
    /// </summary>
    internal static IReadOnlyList<TraderLocation> GetTraderLocations()
        => TraderLocations.AsReadOnly();

    /// <summary>
    /// API: add trader IDs to the player's HideoutTraders list.
    /// </summary>
    internal static void AddHideoutTraders(string sessionId, List<string> traderIds)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || traderIds.Count == 0)
        {
            return;
        }

        var state = StateService.GetState(sessionId);
        var changed = false;
        foreach (var id in traderIds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (state.HideoutTraders.Add(id))
            {
                changed = true;
            }
        }

        if (changed)
        {
            StateService.SaveState(sessionId, state);
        }
    }

    /// <summary>
    /// API: remove trader IDs from the player's HideoutTraders list.
    /// </summary>
    internal static bool RemoveHideoutTraders(string sessionId, List<string> traderIds)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || traderIds.Count == 0)
        {
            return false;
        }

        var state = StateService.GetState(sessionId);
        var wasRemoved = false;
        foreach (var id in traderIds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (state.HideoutTraders.Remove(id))
            {
                wasRemoved = true;
            }
        }

        if (wasRemoved)
        {
            StateService.SaveState(sessionId, state);
        }

        return wasRemoved;
    }

    /// <summary>
    /// API: returns the list of trader ID's in the player's HideoutTraders.
    /// </summary>
    internal static IReadOnlyCollection<string> GetHideoutTraders(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<string>();
        }

        var state = StateService.GetState(sessionId);
        return state.HideoutTraders.ToArray();
    }
}