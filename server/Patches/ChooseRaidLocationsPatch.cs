using System.Reflection;
using System.Text.Json.Nodes;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Models.Common;
using Vagabond.Common.Data;
using Vagabond.Common.Enums;
using Vagabond.Server.Config;
using Vagabond.Server.Services;

namespace Vagabond.Server.Patches;

public sealed class ChooseRaidLocationsPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationCallbacks).GetMethod(nameof(LocationCallbacks.GetLocationData))!;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionID, ref ValueTask<string> __result)
    {
        var serverOwnerSessionId = FikaAdapter.GetRaidOwnerSessionId(sessionID);
        __result = RewriteResponseAsync(serverOwnerSessionId, __result);
    }

    private static async ValueTask<string> RewriteResponseAsync(MongoId sessionId, ValueTask<string> originalResult)
    {
        string jsonString = await originalResult;

        JsonNode? root = JsonNode.Parse(jsonString);
        if (root is null)
        {
            return jsonString;
        }

        if (!VagabondService.ShouldApplyVagabondRules(sessionId))
        {
            return jsonString;
        }

        var pmc = VagabondService.GetPmcProfile(sessionId);
        if (pmc == null || pmc.CharacterData?.PmcData == null)
        {
            VagabondLogger.Error($"Raid extractions: could not resolve PMC profile for {sessionId}.");
            return jsonString;
        }

        var state = StateService.GetState(sessionId);
        if (!state.VagabondModeEnabled)
        {
            VagabondLogger.Error($"Missing state {sessionId}.");
            return jsonString;
        }

        if (string.IsNullOrEmpty(state.CurrentMap) || VagabondConfig.Config.EnablePickRaidLocation)
        {
            return jsonString;
        }

        RaidLocation currentMap = VagabondLocations.NormaliseMapName(state.CurrentMap);
        if (currentMap == RaidLocation.Nil)
        {
            return jsonString;
        }

        // a bit dirty, as I should then limit what time they can pick then.. but.. so be it for now
        if (currentMap == RaidLocation.FactoryNight)
        {
            currentMap = RaidLocation.FactoryDay;
        }

        JsonObject? data = root["data"]?.AsObject();
        JsonObject? locations = data?["locations"]?.AsObject();

        if (locations == null)
        {
            VagabondLogger.Error($"locations is null {sessionId}.");
            return jsonString;
        }

        HashSet<string> allowedMapIds = new(StringComparer.OrdinalIgnoreCase);

        RaidLocation transitMap = VagabondLocations.NormaliseMapName(state.TransitState?.ToMap);
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

        // GroundZero patch
        if (currentMap == RaidLocation.GroundZero || transitMap == RaidLocation.GroundZero)
        {
            var lvl = pmc.CharacterData.PmcData.Info?.Level ?? 1;
            var picked = VagabondService.GetGroundZeroMapIdForLevel(lvl);

            allowedMapIds.RemoveWhere(x =>
                (string.Equals(x, "Sandbox", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(x, "Sandbox_high", StringComparison.OrdinalIgnoreCase))
                && !string.Equals(x, picked, StringComparison.OrdinalIgnoreCase));
        }

        if (allowedMapIds.Count == 0)
        {
            return jsonString;
        }

        foreach (string locationKey in locations.Select(kv => kv.Key).ToList())
        {
            if (!allowedMapIds.Contains(locationKey))
            {
                JsonObject? location = locations[locationKey]?.AsObject();
                if (location != null)
                {
                    location["enabled"] = false;
                }
            }
        }

        var questExfils = QuestService.BuildExfilList(state);

        foreach (string locationKey in locations.Select(x => x.Key).ToList())
        {
            JsonObject? location = locations[locationKey]?.AsObject();
            JsonArray? exits = location?["exits"]?.AsArray();
            JsonArray? secretExits = location?["secretExits"]?.AsArray();
            bool enabled = location?["enabled"]?.GetValue<bool>() ?? true;

            if (!enabled)
            {
                continue;
            }

            VagabondLocations.IdToName.TryGetValue(locationKey, out var mapName);
            questExfils.TryGetValue(mapName!, out var mapQuestExfils);

            if (exits != null)
            {
                for (int i = exits.Count - 1; i >= 0; i--)
                {
                    JsonObject? exfil = exits[i]?.AsObject();

                    if (exfil is null)
                    {
                        exits.RemoveAt(i);
                        continue;
                    }

                    if (!ShouldKeepExtract(exfil, locationKey, mapQuestExfils ?? []))
                    {
                        exits.RemoveAt(i);
                    }
                }
            }

            if (secretExits != null)
            {
                for (int i = secretExits.Count - 1; i >= 0; i--)
                {
                    JsonObject? exfil = secretExits[i]?.AsObject();
                    if (exfil == null || !ShouldKeepExtract(exfil, locationKey, mapQuestExfils ?? []))
                    {
                        secretExits.RemoveAt(i);
                    }
                }
            }
        }

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false
        });
    }

    private static bool ShouldKeepExtract(JsonObject exfil, string locationKey, List<string> mapQuestExfils)
    {
        return IsCustomExtract(exfil, locationKey) || IsQuestExtract(exfil, mapQuestExfils);
    }

    private static bool IsQuestExtract(JsonObject exfil, List<string> mapQuestExfils)
    {
        var templ = exfil["Name"]?.GetValue<string>();
        if (templ == null)
        {
            return false;
        }

        return mapQuestExfils.Contains(templ);
    }

    private static bool IsCustomExtract(JsonObject exfil, MongoId locationKey)
    {
        string? name = exfil["Name"]?.GetValue<string>();
        var raid = VagabondLocations.NormaliseMapName(locationKey);
        if (!VagabondLocations.IdToName.TryGetValue(locationKey, out var mapName))
        {
            return false;
        }

        return ExfilService.IsCustomExtractName(name, raid, mapName);
    }
}