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

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, out IDisposable __state)
    {
        __state = VirtualStashService.AcquireGateScope(sessionId);
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

        var pmc = __result[0];
        var enabled = StateService.WithState(sessionId, state =>
        {
            if (!state.VagabondModeEnabled)
            {
                return false;
            }

            HideoutService.UpdateTraderAccess(pmc, state);
            return true;
        });

        if (enabled)
        {
            VirtualStashService.ApplyToClientProfile(sessionId, pmc);
        }
    }

    [PatchFinalizer]
    public static Exception? Finalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception;
    }
}