using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.Communications;
using HarmonyLib;
using SPT.Reflection.Patching;
using Vagabond.Client.Services;

namespace Vagabond.Client.Patches;

internal class BlockHideoutAccessPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(MainMenuShowOperation), "method_24");
    }

    [PatchPrefix]
    private static bool Prefix(ref Task __result)
    {
        if (!Vagabond.State.LimitHideoutAccess)
        {
            return true;
        }

        if (Vagabond.State.HideoutAccessible && !CommunicationService.HasUnsentInRaidStatus())
        {
            return true;
        }

        NotificationManager.DisplayWarningNotification(
            "You can only access you hideout from your hideout extract.");
        __result = Task.CompletedTask;
        return false;
    }
}