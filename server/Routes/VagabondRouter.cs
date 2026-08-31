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
    new RouteAction(
        "/vagabond/sync/state",
        (_, info, sessionID, _, _) =>
        {
            return ValueTask.FromResult<object>(
                jsonUtil.Serialize(HandleSyncStateRoute(sessionID, info as SyncStateServerRequest)) ??
                throw new NullReferenceException("Could not serialize sync response"));
        },
        typeof(SyncStateServerRequest)
    ),
    new RouteAction<GetExfilDataServerRequest>(
        "/vagabond/sync/exfils",
        (_, payload, sessionID, _, _) =>
        {
            return ValueTask.FromResult(jsonUtil.Serialize(HandleSyncExfilRoute(sessionID, payload)) ??
                                        throw new NullReferenceException("Could not serialize sync response"));
        }
    ),
    new RouteAction<PlaceHideoutServerRequest>(
        "/vagabond/hideout/establish",
        (_, payload, sessionID, _, _) =>
        {
            return ValueTask.FromResult(
                jsonUtil.Serialize(HandleEstablishHideoutRoute(sessionID, payload)) ??
                throw new NullReferenceException("Could not serialize hideout response"));
        }
    ),
])
{
    private static SyncStateResponse HandleSyncStateRoute(MongoId sessionId, SyncStateServerRequest? payload)
    {
        var response = new SyncStateResponse
        {
            CurrentMap = ""
        };

        if (payload?.InRaid == false)
        {
            RaidRuntimeState.Left(sessionId);
        }

        if (!VagabondService.ShouldApplyVagabondRules(sessionId))
        {
            response.CustomExfils = ExfilService.BuildCustomExfilSnapshot();

            if (VagabondService.IsHeadlessSession(sessionId))
            {
                response.QuestExfils = BuildHeadlessQuestExfilUnion(sessionId);
            }

            return response;
        }

        var pmc = VagabondService.GetPmcProfile(sessionId);
        if (pmc == null || pmc.CharacterData?.PmcData == null)
        {
            VagabondLogger.Error($"PMC data is null {sessionId}");
            response.CustomExfils = ExfilService.BuildCustomExfilSnapshot();
            return response;
        }

        var pmcData = pmc.CharacterData.PmcData;
        StateService.WithState(sessionId, state =>
        {
            // load their hideout first time
            if (ExfilService.AddHideoutExfil(pmcData, state))
            {
                ExfilService.BuildCustomExfilSnapshot(true);
            }

            response.CustomExfils = ExfilService.BuildCustomExfilSnapshot();
            response.QuestExfils = QuestService.BuildExfilList(state);
            response.AllowPostRaidHealing = VagabondConfig.Config.AllowPostRaidHealing;
            response.ResetOnDeath = VagabondConfig.Config.ResetOnDeath;
            response.WipeFirstRaid = VagabondConfig.Config.WipeStashOnFirstRaidEntry;
            response.VirtualStashes = VagabondConfig.Config.EnableVirtualStashes;
            response.CurrentMap = VagabondService.GetCurrentRaidId(sessionId, state);
            response.NewCharacter = string.IsNullOrEmpty(state.CurrentMap);
            response.LimitTraderMailAccess = VagabondConfig.Config.LimitTraderMailAccess;
            response.HideoutAccessible = HideoutService.IsAtHideout(state);
            response.LimitHideoutAccess = VagabondConfig.Config.LimitHideoutAccessToHideoutExfil;
            response.RaidFirItems = state.RaidFirItems != null
                ? new HashSet<string>(state.RaidFirItems)
                : new HashSet<string>();
        });

        var ownerSessionId = FikaAdapter.GetRaidOwnerSessionId(sessionId);
        var (ownerCurrentMap, ownerLastExtractMap, ownerStreak) = StateService.WithState(ownerSessionId,
            s => (s.CurrentMap, s.LastExtractMap, s.ConsecutiveExtractsSameMap));

        response.LootStreakEnabled = VagabondConfig.Config.EnableConsecutiveMapLootReduction;
        response.LootStreakMultiplier = LootStreakService.GetCurrentMultiplier(ownerSessionId, ownerCurrentMap);
        response.LootStreakCount =
            LootStreakService.GetStreakMapName(ownerCurrentMap) == ownerLastExtractMap
                ? ownerStreak
                : 0;

        return response;
    }

    private static Dictionary<string, List<string>> BuildHeadlessQuestExfilUnion(MongoId headlessSessionId)
    {
        var members = FikaAdapter.GetHeadlessMatchMemberSessionIds(headlessSessionId);
        if (members == null || members.Count == 0)
        {
            var requester = FikaAdapter.GetRaidOwnerSessionId(headlessSessionId);
            members = requester == headlessSessionId
                ? Array.Empty<MongoId>()
                : new[] { requester };
        }

        var questIds = new HashSet<string>();
        foreach (var member in members)
        {
            if (member == headlessSessionId)
            {
                continue;
            }

            // skips headless entries, unknown ids (scav-side fika player ids) and missing profiles.
            if (!VagabondService.ShouldApplyVagabondRules(member))
            {
                continue;
            }

            var memberQuestIds = StateService.WithState(member, state => state.QuestExfils.ToList());
            questIds.UnionWith(memberQuestIds);
        }

        return QuestService.BuildExfilList(questIds);
    }

    private static SyncExfilResponse HandleSyncExfilRoute(MongoId _, GetExfilDataServerRequest payload)
    {
        var (version, snapshot) = ExfilService.GetSnapshotWithVersion();
        var response = new SyncExfilResponse
        {
            Version = version,
        };

        if (payload.Version != version)
        {
            response.CustomExfils = snapshot;
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

        var pmcData = pmc.CharacterData.PmcData;
        StateService.WithState(sessionId, state =>
        {
            if (state.HideoutState != null && (!VagabondConfig.Config.AllowHideoutRelocation && !state.CanPlaceHideout))
            {
                response.Success = false;
                response.Message =
                    $"You have already established your hideout in {VagabondLocations.ToHumanName(VagabondLocations.NormaliseMapName(state.HideoutState.Map))}. Talk to Skier to relocate your hideout.";
                return;
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

            ExfilService.AddHideoutExfil(pmcData, state);
            ExfilService.BuildCustomExfilSnapshot(true);

            StateService.SaveState(sessionId, state);
            response.Success = true;
            response.CurrentRaid = mapName;
            response.MapName = mapName;
            response.Message = "Establishing hideout, please wait...";
        });

        return response;
    }
}