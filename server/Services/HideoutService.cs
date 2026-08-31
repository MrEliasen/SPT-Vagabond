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

    private static readonly Lock TraderLocationsLock = new();
    private static volatile List<TraderLocation> _traderLocations = new();

    public static IReadOnlyList<TraderLocation> TraderLocations => _traderLocations;

    public static void LoadTraderLocations(IEnumerable<TraderLocation> seed)
    {
        lock (TraderLocationsLock)
        {
            _traderLocations = new List<TraderLocation>(seed);
        }
    }

    public static IReadOnlyCollection<string> GetAllTraderIds()
    {
        return _traderLocations
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

        return _traderLocations.FirstOrDefault(x =>
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

        return _traderLocations
            .Where(x => x.Raid == raid
                        && string.Equals(x.ExfilIdentifier, state.LastExit, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.TraderId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyCollection<string> GetStashKeys()
    {
        return _traderLocations
            .Select(x => x.ExfilIdentifier)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void UpdateTraderAccess(PmcData pmc, VagabondSessionState state)
    {
        var currentTraderIds = new HashSet<string>(GetTraderIds(state), StringComparer.Ordinal);
        var tradersInfo = pmc.TradersInfo;
        if (tradersInfo == null)
        {
            return;
        }

        var isOwnHideout = !string.IsNullOrEmpty(state.HideoutState?.Id) &&
                           state.LastExit == $"{HideoutIdPrefix}{state.HideoutState?.Id}";

        foreach (KeyValuePair<MongoId, TraderInfo> entry in tradersInfo)
        {
            if (VagabondConfig.Config.IgnoredTraders.Contains(entry.Key))
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

    internal static void AddTraderLocations(List<TraderLocation> extractions)
    {
        lock (TraderLocationsLock)
        {
            var ids = new HashSet<string>(
                extractions.Select(t => t.ExfilIdentifier),
                StringComparer.OrdinalIgnoreCase);

            var updated = new List<TraderLocation>(_traderLocations);
            updated.RemoveAll(x => ids.Contains(x.ExfilIdentifier));
            updated.AddRange(extractions);
            _traderLocations = updated;
        }
    }

    internal static bool RemoveTraderLocation(string exfilIdentifier)
    {
        if (string.IsNullOrWhiteSpace(exfilIdentifier))
        {
            return false;
        }

        lock (TraderLocationsLock)
        {
            var updated = new List<TraderLocation>(_traderLocations);
            var removed = updated.RemoveAll(x =>
                string.Equals(x.ExfilIdentifier, exfilIdentifier, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                _traderLocations = updated;
            }

            return removed;
        }
    }

    internal static IReadOnlyList<TraderLocation> GetTraderLocations()
        => _traderLocations.AsReadOnly();

    internal static void AddHideoutTraders(string sessionId, List<string> traderIds)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || traderIds.Count == 0)
        {
            return;
        }

        StateService.WithState(sessionId, state =>
        {
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
        });
    }

    internal static bool RemoveHideoutTraders(string sessionId, List<string> traderIds)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || traderIds.Count == 0)
        {
            return false;
        }

        return StateService.WithState(sessionId, state =>
        {
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
        });
    }

    internal static IReadOnlyCollection<string> GetHideoutTraders(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<string>();
        }

        return StateService.WithState(sessionId, state => state.HideoutTraders.ToArray());
    }
}