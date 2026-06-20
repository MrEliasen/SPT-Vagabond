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
    public static void Postfix(MongoId sessionID, AcceptQuestRequestData info, ItemEventRouterResponse __result)
    {
        if (__result.Warnings != null && __result.Warnings.Count > 0)
        {
            return;
        }

        var state = StateService.GetState(sessionID);
        if (ExfilQuests.List.ContainsKey(info.QuestId) && !state.QuestExfils.Contains(info.QuestId))
        {
            state.QuestExfils.Add(info.QuestId);
            StateService.SaveState(sessionID, state);
        }
    }
}

public sealed class QuestCallbacksCompleteQuestPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(QuestCallbacks).GetMethod(nameof(QuestCallbacks.CompleteQuest))!;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionID, CompleteQuestRequestData info, ItemEventRouterResponse __result)
    {
        if (__result.Warnings != null && __result.Warnings.Count > 0)
        {
            return;
        }

        var state = StateService.GetState(sessionID);

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
    }
}