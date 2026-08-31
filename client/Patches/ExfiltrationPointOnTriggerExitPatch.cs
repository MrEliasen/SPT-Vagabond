using System.Reflection;
using Comfort.Common;
using EFT;
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
        var gameWorld = Singleton<GameWorld>.Instance;
        var player = gameWorld?.GetPlayerByCollider(col);
        var mainPlayer = gameWorld?.MainPlayer;
        if (player != null && mainPlayer != null && ReferenceEquals(player, mainPlayer))
        {
            ActiveHealthControllerPatch.EnableFallDamage = true;
        }

        ExfilService.ClearSpawnOverlapSuppression(__instance, col);
    }
}