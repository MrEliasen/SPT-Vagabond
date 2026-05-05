using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;
using Vagabond.Common.Data;
using Vagabond.Common.Models;
using Vagabond.Server.Config;
using Vagabond.Server.Models;
using Vagabond.Server.Services;
using Vagabond.Common.Definitions;
using Vagabond.Server.State;

namespace Vagabond.Server.Routes;

[Injectable]
public class VagabondRouter(
    JsonUtil jsonUtil) : StaticRouter(jsonUtil, [
    new RouteAction<EmptyRequestData>(
        "/vagabond/sync/state",
        (_, _, sessionID, _) =>
        {
            return ValueTask.FromResult(jsonUtil.Serialize(HandleSyncStateRoute(sessionID)) ??
                                        throw new NullReferenceException("Could not serialize sync response"));
        }
    ),
    new RouteAction<GetExfilDataServerRequest>(
        "/vagabond/sync/exfils",
        (_, payload, sessionID, _) =>
        {
            return ValueTask.FromResult(jsonUtil.Serialize(HandleSyncExfilRoute(sessionID, payload)) ??
                                        throw new NullReferenceException("Could not serialize sync response"));
        }
    ),
    new RouteAction<PlaceHideoutServerRequest>(
        "/vagabond/hideout/establish",
        (_, payload, sessionID, _) =>
        {
            return ValueTask.FromResult(
                jsonUtil.Serialize(HandleEstablishHideoutRoute(sessionID, payload)) ??
                throw new NullReferenceException("Could not serialize hideout response"));
        }
    ),
])
{
    private static SyncStateResponse HandleSyncStateRoute(MongoId sessionId)
    {
        var response = new SyncStateResponse
        {
            CurrentMap = ""
        };

        if (!VagabondService.IsInRaid(sessionId))
        {
            RaidRuntimeState.Left(sessionId);
        }

        if (!VagabondService.ShouldApplyVagabondRules(sessionId))
        {
            response.CustomExfils = ExfilService.BuildCustomExfilSnapshot();
            return response;
        }

        var pmc = VagabondService.GetPmcProfile(sessionId);
        if (pmc == null || pmc.CharacterData?.PmcData == null)
        {
            VagabondLogger.Error($"PMC data is null {sessionId}");
            response.CustomExfils = ExfilService.BuildCustomExfilSnapshot();
            return response;
        }

        var state = StateService.GetState(sessionId);
        // load their hideout first time
        if (ExfilService.AddHideoutExfil(pmc.CharacterData.PmcData, state))
        {
            ExfilService.BuildCustomExfilSnapshot(true);
        }

        response.CustomExfils = ExfilService.BuildCustomExfilSnapshot();
        response.QuestExfils = QuestService.BuildExfilList(state);
        response.AllowPostRaidHealing = VagabondConfig.Config.AllowPostRaidHealing;
        response.ResetOnDeath = VagabondConfig.Config.ResetOnDeath;
        response.WipeFirstRaid = VagabondConfig.Config.WipeStashOnFirstRaidEntry;
        response.CurrentMap = VagabondService.GetCurrentRaidId(sessionId, state);
        response.NewCharacter = string.IsNullOrEmpty(state.CurrentMap);
        response.LimitTraderMailAccess = VagabondConfig.Config.LimitTraderMailAccess;
        response.RaidFirItems = state.RaidFirItems ?? new HashSet<string>();

        var ownerSessionId = FikaAdapter.GetRaidOwnerSessionId(sessionId);
        var ownerState = ownerSessionId == sessionId ? state : StateService.GetState(ownerSessionId);

        response.LootStreakEnabled = VagabondConfig.Config.EnableConsecutiveMapLootReduction;
        response.LootStreakMultiplier = LootStreakService.GetCurrentMultiplier(ownerSessionId, ownerState.CurrentMap);
        response.LootStreakCount =
            LootStreakService.GetStreakMapName(ownerState.CurrentMap) == ownerState.LastExtractMap
                ? ownerState.ConsecutiveExtractsSameMap
                : 0;

        return response;
    }

    private static SyncExfilResponse HandleSyncExfilRoute(MongoId _, GetExfilDataServerRequest payload)
    {
        var response = new SyncExfilResponse
        {
            Version = ExfilService.SnapshotCacheVersion,
        };

        if (payload.Version != ExfilService.SnapshotCacheVersion)
        {
            response.CustomExfils = ExfilService.BuildCustomExfilSnapshot();
        }

        return response;
    }

    private static PlaceHideoutResponse HandleEstablishHideoutRoute(
        MongoId sessionId,
        PlaceHideoutServerRequest payload)
    {
        var response = new PlaceHideoutResponse();

        if (!VagabondService.ShouldApplyVagabondRules(sessionId))
        {
            return response;
        }

        var pmc = VagabondService.GetPmcProfile(sessionId);
        if (pmc?.CharacterData?.PmcData == null)
        {
            return response;
        }

        var state = StateService.GetState(sessionId);

        if (state.HideoutState != null && (!VagabondConfig.Config.AllowHideoutRelocation && !state.CanPlaceHideout))
        {
            response.Success = false;
            response.Message =
                $"You have already established your hideout in {VagabondLocations.ToHumanName(VagabondLocations.NormaliseMapName(state.HideoutState.Map))}. Talk to Skier to relocate your hideout.";
            return response;
        }

        var mapName = !string.IsNullOrWhiteSpace(payload.LocationId)
            ? payload.LocationId
            : VagabondService.GetCurrentRaidId(sessionId, state);

        if (state.HideoutState == null)
        {
            state.HideoutState = new HideoutState
            {
                // if we do not keep the same ID, any virtual stashes tied to that hideout disappear
                Id = String.Format("{0:X}", sessionId.GetHashCode()),
            };
        }

        ExfilService.RemoveHideout(state.HideoutState);

        state.CanPlaceHideout = false;
        state.HideoutState.Map = mapName;
        state.HideoutState.X = payload.X;
        state.HideoutState.Y = payload.Y;
        state.HideoutState.Z = payload.Z;
        state.HideoutState.R = payload.R;

        ExfilService.AddHideoutExfil(pmc.CharacterData.PmcData, state);
        ExfilService.BuildCustomExfilSnapshot(true);

        StateService.SaveState(sessionId, state);
        response.Success = true;
        response.CurrentRaid = mapName;
        response.MapName = mapName;
        response.Message = "Establishing hideout, please wait...";
        return response;
    }
}