using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;
using Vagabond.Common.Data;
using Vagabond.Common.Enums;

namespace Vagabond.Client.Services;

public static class ExfilService
{
    internal static readonly HashSet<int> SuppressedCustomExtractPointIds = new();

    internal static readonly List<ExfiltrationPoint> PendingSpawnOverlapPoints = new();

    public static bool ShouldSuppressSpawnOverlap(ExfiltrationPoint point, Player player)
    {
        if (point == null || player == null)
        {
            return false;
        }

        var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
        if (mainPlayer == null || !ReferenceEquals(player, mainPlayer))
        {
            return false;
        }

        return SuppressedCustomExtractPointIds.Contains(point.GetInstanceID())
               || PendingSpawnOverlapPoints.Contains(point);
    }

    internal static void ResolvePendingSpawnOverlapSuppression()
    {
        if (PendingSpawnOverlapPoints.Count == 0)
        {
            return;
        }

        var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
        if (mainPlayer == null)
        {
            return;
        }

        foreach (var point in PendingSpawnOverlapPoints)
        {
            if (point != null && IsPlayerInsidePointTrigger(mainPlayer, point))
            {
                SuppressedCustomExtractPointIds.Add(point.GetInstanceID());
            }
        }

        PendingSpawnOverlapPoints.Clear();
    }

    public static bool ShouldSuppressSpawnOverlap(ExfiltrationPoint point, Collider collider)
    {
        if (point == null || collider == null)
        {
            return false;
        }

        var player = Singleton<GameWorld>.Instance?.GetPlayerByCollider(collider);
        return ShouldSuppressSpawnOverlap(point, player);
    }

    public static void ClearSpawnOverlapSuppression(ExfiltrationPoint point, Collider collider)
    {
        if (point == null || collider == null)
        {
            return;
        }

        var player = Singleton<GameWorld>.Instance?.GetPlayerByCollider(collider);
        var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || mainPlayer == null || !ReferenceEquals(player, mainPlayer))
        {
            return;
        }

        SuppressedCustomExtractPointIds.Remove(point.GetInstanceID());
    }

    public static bool IsPlayerInsidePointTrigger(Player player, ExfiltrationPoint point)
    {
        if (player == null || point == null)
        {
            return false;
        }

        var playerPosition = player.Position;
        foreach (var collider in point.GetComponentsInChildren<Collider>(true))
        {
            if (collider == null || !collider.enabled)
            {
                continue;
            }

            try
            {
                var closestPoint = collider.ClosestPoint(playerPosition);
                if ((closestPoint - playerPosition).sqrMagnitude <= 0.0001f)
                {
                    return true;
                }
            }
            catch
            {
                if (collider.bounds.Contains(playerPosition))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsCustomExfil(ExitTriggerSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings?.Name))
        {
            return false;
        }

        var locationId = Singleton<GameWorld>.Instance?.LocationId;
        if (string.IsNullOrWhiteSpace(locationId))
        {
            return false;
        }

        var raid = VagabondLocations.NormaliseMapName(locationId);
        if (raid == RaidLocation.Nil)
        {
            return false;
        }

        if (!Vagabond.State.CustomExfils.TryGetValue(raid, out var mapExfils))
        {
            return false;
        }

        if (!mapExfils.TryGetValue(locationId, out var definitions) || definitions == null)
        {
            return false;
        }

        return definitions.Any(def =>
            !def.IsTransit &&
            string.Equals(def.DisplayName, settings.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsQuestNativeExfil(ExitTriggerSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings?.Name))
        {
            return false;
        }

        var locationId = Singleton<GameWorld>.Instance?.LocationId;
        if (string.IsNullOrWhiteSpace(locationId))
        {
            return false;
        }

        if (!Vagabond.State.QuestExfils.TryGetValue(locationId, out var kept) || kept == null)
        {
            return false;
        }

        return kept.Any(x => string.Equals(x, settings.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsVehicleTemplate(ExfiltrationPoint point)
    {
        return string.Equals(point.Settings?.Id, Currencies.Ruble, StringComparison.OrdinalIgnoreCase);
    }

    public static void NormalizeExtractColliders(ExfiltrationPoint clone, ExfiltrationPoint template)
    {
        var rootCollider = clone.GetComponent<Collider>();
        var templateRootCollider = template.GetComponent<Collider>();
        TryConfigureTriggerCollider(rootCollider, templateRootCollider);

        var extendedCollider = clone.ExtendedCollider;
        if (extendedCollider != null && extendedCollider != rootCollider)
        {
            var templateExtendedCollider =
                template.ExtendedCollider != null && template.ExtendedCollider != templateRootCollider
                    ? template.ExtendedCollider
                    : templateRootCollider;

            if (!TryConfigureTriggerCollider(extendedCollider, templateExtendedCollider))
            {
                extendedCollider.enabled = false;
                clone.ExtendedCollider = null;
                extendedCollider = null;
            }
        }

        foreach (var collider in clone.GetComponentsInChildren<Collider>(true))
        {
            if (collider == null || collider == rootCollider || collider == extendedCollider)
            {
                continue;
            }

            collider.enabled = false;
        }
    }

    public static bool TryConfigureTriggerCollider(Collider collider, Collider templateCollider)
    {
        if (collider == null)
        {
            return false;
        }

        collider.enabled = true;
        collider.isTrigger = true;

        if (collider is BoxCollider box)
        {
            if (templateCollider is BoxCollider templateBox)
            {
                box.center = templateBox.center;
                box.size = new Vector3(5f, templateBox.size.y, 5f);
            }
            else
            {
                box.size = new Vector3(5f, box.size.y, 5f);
            }

            return true;
        }

        return false;
    }
}