using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using JsonType;
using SPT.Reflection.Patching;

namespace Vagabond.Client.Patches;

internal class TryGetLocationByIdPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(
            typeof(TarkovApplication),
            nameof(TarkovApplication.TryGetLocationById),
            new[]
            {
                typeof(string),
                typeof(LocationSettings.Location).MakeByRefType()
            });
    }

    [PatchPrefix]
    private static bool Prefix(TarkovApplication __instance, ref string locationId)
    {
        if (string.IsNullOrEmpty(locationId))
        {
            return true;
        }

        var locations = __instance.Session?.LocationSettings?.locations;
        if (locations == null)
        {
            return true;
        }

        string canonical = null;
        var matches = 0;

        foreach (var location in locations.Values)
        {
            if (location == null || location.Id == null)
            {
                continue;
            }

            if (!string.Equals(location.Id, locationId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches++;
            if (matches > 1)
            {
                return true;
            }

            canonical = location.Id;
        }

        if (matches != 1 || string.Equals(canonical, locationId, StringComparison.Ordinal))
        {
            return true;
        }

        Vagabond.Log($"Location id '{locationId}' resolved to canonical id '{canonical}'.");
        locationId = canonical;
        return true;
    }
}