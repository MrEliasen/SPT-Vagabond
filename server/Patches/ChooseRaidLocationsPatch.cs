using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Vagabond.Common.Data;
using Vagabond.Common.Enums;
using Vagabond.Server.Config;
using Vagabond.Server.Services;

namespace Vagabond.Server.Patches;

public sealed class ChooseRaidLocationsPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationController).GetMethod(nameof(LocationController.GenerateAll))!;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId, ref LocationsGenerateAllResponse __result)
    {
        var serverOwnerSessionId = FikaAdapter.GetRaidOwnerSessionId(sessionId);
        RewriteResponse(serverOwnerSessionId, __result);
    }

    private static void RewriteResponse(MongoId sessionId, LocationsGenerateAllResponse response)
    {
        if (!VagabondService.ShouldApplyVagabondRules(sessionId))
        {
            return;
        }

        var pmc = VagabondService.GetPmcProfile(sessionId);
        if (pmc == null || pmc.CharacterData?.PmcData == null)
        {
            VagabondLogger.Error($"Raid extractions: could not resolve PMC profile for {sessionId}.");
            return;
        }

        // Copy what we need under the session state lock; the response rewrite runs on the copies.
        var stateSnapshot = StateService.WithState(sessionId, state => state.VagabondModeEnabled
            ? (state.CurrentMap, state.TransitState?.ToMap, QuestService.BuildExfilList(state))
            : ((string, string?, Dictionary<string, List<string>>)?)null);

        if (stateSnapshot == null)
        {
            VagabondLogger.Error($"Missing state {sessionId}.");
            return;
        }

        var (stateCurrentMap, transitToMap, questExfils) = stateSnapshot.Value;

        if (string.IsNullOrEmpty(stateCurrentMap) || VagabondConfig.Config.EnablePickRaidLocation)
        {
            return;
        }

        RaidLocation currentMap = VagabondLocations.NormaliseMapName(stateCurrentMap);
        if (currentMap == RaidLocation.Nil)
        {
            return;
        }

        // a bit dirty, as I should then limit what time they can pick then.. but.. so be it for now
        if (currentMap == RaidLocation.FactoryNight)
        {
            currentMap = RaidLocation.FactoryDay;
        }

        var locations = response.Locations;
        if (locations == null)
        {
            VagabondLogger.Error($"locations is null {sessionId}.");
            return;
        }

        HashSet<string> allowedMapIds = new(StringComparer.OrdinalIgnoreCase);

        RaidLocation transitMap = VagabondLocations.NormaliseMapName(transitToMap);
        if (transitMap != RaidLocation.Nil)
        {
            if (VagabondLocations.Locations.TryGetValue(transitMap, out var mapIds))
            {
                foreach (var mapId in mapIds)
                {
                    allowedMapIds.Add(mapId);
                }
            }
        }
        else if (VagabondLocations.Locations.TryGetValue(currentMap, out var mapIds))
        {
            foreach (var mapId in mapIds)
            {
                allowedMapIds.Add(mapId);
            }
        }

        if (currentMap == RaidLocation.GroundZero || transitMap == RaidLocation.GroundZero)
        {
            var picked = VagabondService.GetGroundZeroMapIdForSession(sessionId);

            allowedMapIds.RemoveWhere(x =>
                (string.Equals(x, "Sandbox", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(x, "Sandbox_high", StringComparison.OrdinalIgnoreCase))
                && !string.Equals(x, picked, StringComparison.OrdinalIgnoreCase));
        }

        if (allowedMapIds.Count == 0)
        {
            return;
        }

        foreach (MongoId locationKey in locations.Keys.ToList())
        {
            if (!allowedMapIds.Contains(locationKey.ToString()))
            {
                locations[locationKey] = locations[locationKey] with { Enabled = false };
            }
        }

        foreach (MongoId locationKey in locations.Keys.ToList())
        {
            var location = locations[locationKey];
            if (!location.Enabled)
            {
                continue;
            }

            VagabondLocations.IdToName.TryGetValue(locationKey, out var mapName);
            questExfils.TryGetValue(mapName!, out var mapQuestExfils);

            var exits = location.Exits?
                .Where(exfil => ShouldKeepExtract(exfil, locationKey, mapQuestExfils ?? []))
                .ToList();

            var secretExits = location.SecretExits?
                .Where(exfil => ShouldKeepExtract(exfil, locationKey, mapQuestExfils ?? []))
                .ToList();

            locations[locationKey] = location with
            {
                Exits = exits ?? [],
                SecretExits = secretExits
            };
        }
    }

    private static bool ShouldKeepExtract(Exit exfil, string locationKey, List<string> mapQuestExfils)
    {
        return IsCustomExtract(exfil, locationKey) || IsQuestExtract(exfil, mapQuestExfils);
    }

    private static bool IsQuestExtract(Exit exfil, List<string> mapQuestExfils)
    {
        var templ = exfil.Name;
        if (templ == null)
        {
            return false;
        }

        return mapQuestExfils.Contains(templ);
    }

    private static bool IsCustomExtract(Exit exfil, MongoId locationKey)
    {
        string? name = exfil.Name;
        var raid = VagabondLocations.NormaliseMapName(locationKey);
        if (!VagabondLocations.IdToName.TryGetValue(locationKey, out var mapName))
        {
            return false;
        }

        return ExfilService.IsCustomExtractName(name, raid, mapName);
    }
}