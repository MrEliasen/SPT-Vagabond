using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Vagabond.Server.Config;
using Vagabond.Server.Services;

namespace Vagabond.Server.Patches;

public sealed class QuestControllerGetClientQuestsPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(QuestController).GetMethod(nameof(QuestController.GetClientQuests))!;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId, ref List<Quest> __result)
    {
        if (__result.Count == 0)
        {
            return;
        }

        var state = StateService.GetState(sessionId);
        if (state.HideoutState == null || state.CanPlaceHideout)
        {
            var relocationQuestId = QuestsConfig.RelocationQuestId;
            if (relocationQuestId != null)
            {
                __result.RemoveAll(q => q.Id == relocationQuestId);
            }
        }
    }
}