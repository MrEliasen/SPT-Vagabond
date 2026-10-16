using System.Linq;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Vagabond.Client.Patches;

public class SkipInsuranceFlowPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(MainMenuShowOperation), "method_51");
    }

    [PatchPrefix]
    public static bool Prefix(MainMenuShowOperation __instance)
    {
        // Show insurance menu if there is an available insurer. Otherwise skip
        if (__instance.iEftSession?.Insurers?.Any() == true)
        {
            return true;
        }

        __instance.method_52();
        return false;
    }
}
