using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Quests;
using Vagabond.Common.Data;
using Vagabond.Server.Config;
using Vagabond.Server.Services;

namespace Vagabond.Server.Patches;

public sealed class QuestCallbacksAcceptQuestPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(QuestCallbacks).GetMethod(nameof(QuestCallbacks.AcceptQuest))!;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionID, AcceptQuestRequestData info,
        ref ValueTask<ItemEventRouterResponse> __result)
    {
        __result = HandleAccepted(__result, sessionID, info);
    }

    private static async ValueTask<ItemEventRouterResponse> HandleAccepted(
        ValueTask<ItemEventRouterResponse> originalResult, MongoId sessionID, AcceptQuestRequestData info)
    {
        var response = await originalResult.ConfigureAwait(false);
        if (response.Warnings != null && response.Warnings.Count > 0)
        {
            return response;
        }

        StateService.WithState(sessionID, state =>
        {
            if (ExfilQuests.List.ContainsKey(info.QuestId) && !state.QuestExfils.Contains(info.QuestId))
            {
                state.QuestExfils.Add(info.QuestId);
                StateService.SaveState(sessionID, state);
            }
        });

        return response;
    }
}

public sealed class QuestCallbacksCompleteQuestPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(QuestCallbacks).GetMethod(nameof(QuestCallbacks.CompleteQuest))!;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionID, CompleteQuestRequestData info,
        ref ValueTask<ItemEventRouterResponse> __result)
    {
        __result = HandleCompleted(__result, sessionID, info);
    }

    private static async ValueTask<ItemEventRouterResponse> HandleCompleted(
        ValueTask<ItemEventRouterResponse> originalResult, MongoId sessionID, CompleteQuestRequestData info)
    {
        var response = await originalResult.ConfigureAwait(false);
        if (response.Warnings != null && response.Warnings.Count > 0)
        {
            return response;
        }

        StateService.WithState(sessionID, state =>
        {
            var questId = info.QuestId.ToString();

            if (state.QuestExfils.Contains(questId))
            {
                state.QuestExfils.Remove(questId);
            }

            if (questId == QuestsConfig.RelocationQuestId)
            {
                state.CanPlaceHideout = true;

                var pmc = VagabondService.GetPmcProfile(sessionID)?.CharacterData?.PmcData;
                pmc?.Quests?.RemoveAll(q => q.QId == questId);
            }
            else if (QuestsConfig.HideoutTraderByQuestId.TryGetValue(questId, out var traderId))
            {
                state.HideoutTraders.Add(traderId);
            }

            StateService.SaveState(sessionID, state);
        });

        return response;
    }
}