using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Game.Spawning;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using Vagabond.Client.Services;

namespace Vagabond.Client.Patches;

internal class ABPSScavDistancePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var type = AccessTools.TypeByName("BotPlacementSystemClient.Patches.TryToSpawnInZonePatch")
                   ?? AccessTools.TypeByName("acidphantasm_botplacementsystem.Patches.TryToSpawnInZonePatch");

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
            foreach (var player in players)
            {
                if (player == null || player.Profile.GetCorrectedNickname().StartsWith("headless_"))
                {
                    continue;
                }

                if (!ForcedSpawnService.TryGetPlayerPosition(player, out var playerPosition))
                {
                    continue;
                }

                if (spawnPoint.Collider.Contains(playerPosition))
                {
                    __result = false;
                    return false;
                }

                if (Vector3.Distance(spawnPoint.Position, playerPosition) < distance)
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