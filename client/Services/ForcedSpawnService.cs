using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using UnityEngine;
using Vagabond.Common.Data;
using Vagabond.Common.Enums;

namespace Vagabond.Client.Services;

internal static class ForcedSpawnService
{
    private const float DefaultSafetyRadius = 15f;

    private static readonly IReadOnlyDictionary<RaidLocation, float> SafetyRadiusOverrides =
        new Dictionary<RaidLocation, float>
        {
            [RaidLocation.FactoryDay] = 8f,
            [RaidLocation.FactoryNight] = 8f,
            [RaidLocation.GroundZero] = 15f,
            [RaidLocation.Interchange] = 15f,
            [RaidLocation.Streets] = 20f,
            [RaidLocation.Woods] = 20f,
            [RaidLocation.Customs] = 20f,
            [RaidLocation.Lighthouse] = 20f,
            [RaidLocation.Reserve] = 10f,
            [RaidLocation.Shoreline] = 20f,
        };

    private static Vector3? _abpsPosition;

    public static void Clear()
    {
        _abpsPosition = null;
    }

    public static void SetAbpsSpawn(ISpawnPoint spawn)
    {
        _abpsPosition = spawn?.Position;
    }

    public static bool TryGetAbpsPosition(out Vector3 position)
    {
        if (_abpsPosition.HasValue)
        {
            position = _abpsPosition.Value;
            return true;
        }

        position = default;
        return false;
    }

    public static bool TryGetPlayerPosition(IPlayer player, out Vector3 position)
    {
        if (player == null)
        {
            position = default;
            return false;
        }

        var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
        if (mainPlayer != null && ReferenceEquals(player, mainPlayer) && TryGetAbpsPosition(out position))
        {
            return true;
        }

        position = player.Position;
        return true;
    }

    public static bool IsBlockedByPlayerPosition(ISpawnPoint spawnPoint)
    {
        if (spawnPoint?.Collider == null)
        {
            return false;
        }

        var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
        if (mainPlayer == null)
        {
            return false;
        }

        var mainPlayerPosition = mainPlayer.Position;
        if (spawnPoint.Collider.Contains(mainPlayerPosition))
        {
            return true;
        }

        var radius = GetSafetyRadius();
        return radius > 0f && Vector3.Distance(spawnPoint.Position, mainPlayerPosition) < radius;
    }

    internal static float GetSafetyRadius()
    {
        var locationId = Singleton<GameWorld>.Instance?.LocationId;
        var raidLocation = VagabondLocations.NormaliseMapName(locationId);
        if (SafetyRadiusOverrides.TryGetValue(raidLocation, out var radius))
        {
            return radius;
        }

        return DefaultSafetyRadius;
    }
}