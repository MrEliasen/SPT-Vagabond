using System.Reflection;
using EFT.Interactive;
using SPT.Reflection.Patching;
using UnityEngine;
using Vagabond.Client.Services;

namespace Vagabond.Client.Patches;

internal class ExfiltrationPointOnTriggerExitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ExfiltrationPoint).GetMethod(
            "IPhysicsTrigger.OnTriggerExit",
            BindingFlags.Instance | BindingFlags.NonPublic);
    }

    [PatchPostfix]
    private static void Postfix(ExfiltrationPoint __instance, Collider col)
    {
        // as soon as they leave their infil
        ActiveHealthControllerPatch.EnableFallDamage = true;
        ExfilService.ClearSpawnOverlapSuppression(__instance, col);
    }
}