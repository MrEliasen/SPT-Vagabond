using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using Vagabond.Common.Data;
using Vagabond.Common.Enums;
using Vagabond.Server.Config;
using Vagabond.Server.Services;
using Vagabond.Server.State;

namespace Vagabond.Server.Patches;

public sealed class RaidJoinPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(MatchController).GetMethod(nameof(MatchController.StartLocalRaid))!;
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, StartLocalRaidRequestData request)
    {
        var serverOwnerSessionId = FikaAdapter.GetRaidOwnerSessionId(sessionId);
        HandleRaidEntry(serverOwnerSessionId, request);
    }

    public static void HandleRaidEntry(MongoId sessionId, StartLocalRaidRequestData request)
    {
        if (!VagabondService.ShouldApplyVagabondRules(sessionId))
        {
            return;
        }

        var pmc = VagabondService.GetPmcProfile(sessionId);
        if (pmc?.CharacterData?.PmcData == null)
        {
            VagabondLogger.Error($"Raid-entry hook could not resolve PMC profile for {sessionId}.");
            return;
        }

        var state = StateService.GetState(sessionId);
        if (!state.VagabondModeEnabled)
        {
            return;
        }

        var mapName = request.Location;
        if (mapName == null)
        {
            VagabondLogger.Error($"Raid-entry error: request.Location is null");
            return;
        }

        var mapNameE = VagabondLocations.NormaliseMapName(mapName);

        // GroundZero fix
        if (mapNameE == RaidLocation.GroundZero)
        {
            var lvl = pmc.CharacterData.PmcData.Info?.Level ?? 1;
            var picked = VagabondService.GetGroundZeroMapIdForLevel(lvl);
            if (!string.Equals(mapName, picked, StringComparison.OrdinalIgnoreCase))
            {
                request.Location = picked;
                mapName = picked;
            }
        }

        if (string.IsNullOrEmpty(state.CurrentMap) && mapNameE != RaidLocation.Nil)
        {
            state.TransitState = null;
            state.CurrentMap = mapNameE.ToString();
            state.LastExit = "";
        }

        RaidRuntimeState.Entered(sessionId);

        if (VagabondConfig.Config.WipeStashOnFirstRaidEntry && state.IsNewCharacter)
        {
            VagabondService.WipeItems(
                sessionId,
                pmc.CharacterData.PmcData,
                false,
                true
            );
            VirtualStashService.ClearAllTraderStashes(sessionId);
            state.IsNewCharacter = false;
        }
        else if (VagabondConfig.Config.WipeVirtualStashesOnRaidEntry)
        {
            VirtualStashService.ClearAllTraderStashes(sessionId);
        }

        state.IsNewCharacter = false;

        StateService.SaveState(sessionId, state);
        VagabondService.PersistProfileIfPossible(sessionId);
    }
}