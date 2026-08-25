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
        return typeof(MatchController).GetMethod(nameof(MatchController.StartLocalRaidAsync))!;
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
            var picked = VagabondService.GetGroundZeroMapIdForSession(sessionId);
            if (!string.Equals(mapName, picked, StringComparison.OrdinalIgnoreCase))
            {
                request.Location = picked;
            }
        }

        var (enabled, wasNewCharacter) = StateService.WithState(sessionId, state =>
        {
            if (!state.VagabondModeEnabled)
            {
                return (false, false);
            }

            if (string.IsNullOrEmpty(state.CurrentMap) && mapNameE != RaidLocation.Nil)
            {
                state.TransitState = null;
                state.CurrentMap = mapNameE.ToString();
                state.LastExit = "";
            }

            var wasNew = state.IsNewCharacter;
            state.IsNewCharacter = false;
            StateService.SaveState(sessionId, state);
            return (true, wasNew);
        });

        if (!enabled)
        {
            return;
        }

        RaidRuntimeState.Entered(sessionId);

        {
            using var gate = VirtualStashService.AcquireGateScope(sessionId);

            if (VagabondConfig.Config.WipeStashOnFirstRaidEntry && wasNewCharacter)
            {
                VagabondService.WipeItems(
                    sessionId,
                    pmc.CharacterData.PmcData,
                    false,
                    true
                );
                VirtualStashService.ClearAllTraderStashes(sessionId);
            }
            else if (VagabondConfig.Config.WipeVirtualStashesOnRaidEntry)
            {
                VirtualStashService.ClearAllTraderStashes(sessionId);
            }
        }

        if (VagabondConfig.Config.EnableVirtualStashes)
        {
            VirtualStashService.ClearTempStash(sessionId);
        }

        VagabondService.PersistProfileIfPossible(sessionId);
    }
}