using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Enums;
using Vagabond.Common;
using Vagabond.Common.Data;
using Vagabond.Common.Definitions;

namespace Vagabond.Server.Services;

public static class MigrationService
{
    private static readonly string CurrentVersion = VagabondModInfo.Version;

    public static void MigrateProfile(MongoId sessionId, PmcData pmc, VagabondSessionState state)
    {
        if (state.Version == CurrentVersion)
        {
            return;
        }

        var i = 25;
        while (state.Version != CurrentVersion && i-- > 0)
        {
            switch (state.Version)
            {
                case "0.3.0":
                {
                    From030To031(pmc, state);
                    break;
                }

                case "0.3.1":
                case "0.3.2":
                case "0.3.3":
                case "0.3.4":
                {
                    From031To040(state);
                    break;
                }

                case "0.4.0":
                {
                    From040To050(state);
                    break;
                }

                case "0.5.0":
                case "0.5.1":
                case "0.5.2":
                {
                    From05To060(state);
                    break;
                }

                case "0.6.0":
                {
                    From060To061(sessionId, state);
                    break;
                }
            }
        }

        state.Version = VagabondModInfo.Version;
        StateService.SaveState(sessionId, state);
    }

    private static void From030To031(PmcData pmc, VagabondSessionState state)
    {
        if (pmc.Quests == null)
        {
            state.Version = "0.3.1";
            return;
        }

        foreach (var quest in pmc.Quests)
        {
            if (quest.Status != QuestStatusEnum.Started)
            {
                continue;
            }

            if (ExfilQuests.List.ContainsKey(quest.QId) && !state.QuestExfils.Contains(quest.QId))
            {
                state.QuestExfils.Add(quest.QId);
            }
        }

        state.Version = "0.3.1";
    }

    private static void From031To040(VagabondSessionState state)
    {
        state.CurrentMap = VagabondLocations.NormaliseMapName(state.CurrentMap).ToString();
        state.Version = "0.4.0";
    }

    private static void From040To050(VagabondSessionState state)
    {
        state.CanPlaceHideout = state.HideoutState?.Id == null;
        state.Version = "0.5.0";
    }

    private static void From05To060(VagabondSessionState state)
    {
        state.ConsecutiveExtractsSameMap = 1;
        state.LastExtractMap = state.CurrentMap;
        state.Version = "0.6.0";
    }

    private static void From060To061(MongoId sessionId, VagabondSessionState state)
    {
        foreach (var loc in HideoutService.TraderLocations)
        {
            VirtualStashService.RekeyStash(sessionId, loc.TraderId, loc.ExfilIdentifier);
        }

        state.Version = "0.6.1";
    }
}