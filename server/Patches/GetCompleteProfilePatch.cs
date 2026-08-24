using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using Vagabond.Server.Services;

namespace Vagabond.Server.Patches;

public sealed class GetCompleteProfilePatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProfileHelper).GetMethod(nameof(ProfileHelper.GetCompleteProfile))!;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId, ref List<PmcData> __result)
    {
        if (!VagabondService.ShouldApplyVagabondRules(sessionId))
        {
            return;
        }

        if (__result.Count == 0)
        {
            return;
        }

        var state = StateService.GetState(sessionId);
        if (!state.VagabondModeEnabled)
        {
            return;
        }

        HideoutService.UpdateTraderAccess(__result[0], state);
        VirtualStashService.ApplyToClientProfile(sessionId, __result[0]);
    }
}