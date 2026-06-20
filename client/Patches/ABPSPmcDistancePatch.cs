using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Game.Spawning;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using Vagabond.Client.Services;

namespace Vagabond.Client.Patches;

internal class ABPSPmcDistancePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var type = AccessTools.TypeByName("BotPlacementSystemClient.Patches.PmcSpawnHookPatch")
                   ?? AccessTools.TypeByName("acidphantasm_botplacementsystem.Patches.PmcSpawnHookPatch");

        return AccessTools.Method(
            type,
            "IsValid",
            new[] { typeof(ISpawnPoint), typeof(IReadOnlyCollection<Player>), typeof(float) });
    }

    [PatchPrefix]
    private static bool Prefix(ISpawnPoint spawnPoint, IReadOnlyCollection<Player> players, float distance,
        ref bool __result)
    {
        if (spawnPoint == null || spawnPoint.Collider == null)
        {
            __result = false;
            return false;
        }

        if (ForcedSpawnService.IsBlockedByPlayerPosition(spawnPoint))
        {
            __result = false;
            return false;
        }

        if (!ForcedSpawnService.TryGetAbpsPosition(out _))
        {
            return true;
        }

        if (players != null && players.Count != 0)
        {
            foreach (var player0 in players)
            {
                if (player0 == null || player0.Profile.GetCorrectedNickname().StartsWith("headless_"))
                {
                    continue;
                }

                if (!ForcedSpawnService.TryGetPlayerPosition(player0, out var playerPosition0))
                {
                    continue;
                }

                if (spawnPoint.Collider.Contains(playerPosition0))
                {
                    __result = false;
                    return false;
                }

                if (Vector3.Distance(spawnPoint.Position, playerPosition0) < distance)
                {
                    __result = false;
                    return false;
                }
            }
        }

        __result = true;
        return false;
    }
}