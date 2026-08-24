using System.Reflection;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Vagabond.Client.Patches;

internal class HealthTreatmentScreenAddTreatmentPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(HealthTreatmentServiceView),
            nameof(HealthTreatmentServiceView.AddTreatment));
    }

    [PatchPrefix]
    protected static bool PatchPrefix(HealthTreatmentServiceView __instance, ref bool ____nothingToHeal)
    {
        if (Vagabond.State.AllowPostRaidHealing)
        {
            return true;
        }

        __instance.RecalculateCost();
        ____nothingToHeal = false;
        return false;
    }
}