using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using Vagabond.Client.Services;

namespace Vagabond.Client.Patches;

public class ActiveHealthControllerPatch : ModulePatch
{
    public static bool EnableFallDamage = false;
    private static Vector3? _spawnAnchor;

    public static void ResetFallDamageArming()
    {
        EnableFallDamage = false;
        _spawnAnchor = null;
    }

    public static void UpdateFallDamageArming()
    {
        if (EnableFallDamage)
        {
            return;
        }

        var player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null)
        {
            _spawnAnchor = null;
            return;
        }

        var position = player.Position;
        if (!_spawnAnchor.HasValue)
        {
            _spawnAnchor = position;
            return;
        }

        var anchor = _spawnAnchor.Value;
        var dx = position.x - anchor.x;
        var dz = position.z - anchor.z;
        var radius = ForcedSpawnService.GetSafetyRadius();
        if ((dx * dx) + (dz * dz) >= radius * radius)
        {
            EnableFallDamage = true;
        }
    }

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.HandleFall));
    }

    [PatchPrefix]
    public static bool PatchPrefix(ActiveHealthController __instance, float height, Player ___Player)
    {
        if (___Player.IsAI || height <= 0)
        {
            return true;
        }

        return EnableFallDamage;
    }
}