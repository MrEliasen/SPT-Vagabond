using System.Reflection;
using EFT.Interactive;
using SPT.Reflection.Patching;
using UnityEngine;
using Vagabond.Client.Services;

namespace Vagabond.Client.Patches;

internal class ExfiltrationPointOnTriggerEnterPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ExfiltrationPoint).GetMethod(
            "IPhysicsTrigger.OnTriggerEnter",
            BindingFlags.Instance | BindingFlags.NonPublic);
    }

    [PatchPrefix]
    private static bool Prefix(ExfiltrationPoint __instance, Collider col)
    {
        return !ExfilService.ShouldSuppressSpawnOverlap(__instance, col);
    }
}