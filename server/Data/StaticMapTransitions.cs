using Vagabond.Common.Data;
using Vagabond.Common.Definitions;
using Vagabond.Common.Enums;
using Vagabond.Common.Models;
using Vagabond.Server.Config;
using Vagabond.Server.Services;

namespace Vagabond.Server.Data;

public static class StaticMapTransitions
{
    public static ManualSpawnPoint? GetSpawnLocation(VagabondSessionState state, RaidLocation location)
    {
        if (location == RaidLocation.Nil)
        {
            return null;
        }

        // if we cannot map the exit directly to a spawn location, we fall back to the below switch.
        if (GetTransitSpecificSpawnLocation(state, location, out var customTransitSpawn))
        {
            return customTransitSpawn;
        }

        // if we cannot map the exit directly to a spawn location, we fall back to the below switch.
        if (GetNormalRaidLocation(state, location, out var customExitSpawn))
        {
            return customExitSpawn;
        }

        // if we cannot map the exit directly to a spawn location, we fall back to the below switch.
        if (GetHideoutLocation(state, location, out var customHideoutSpawn))
        {
            return customHideoutSpawn;
        }

        // Honestly, I made this as my first version of custom spawn points and it works well for
        // original transits, but if you have two transits going in the same direction, it falls apart.
        // So, leave this for original transits, and we only worry about mapping our custom transits.
        var transit = state.TransitState;
        if (transit == null)
        {
            return null;
        }

        var from = VagabondLocations.NormaliseMapName(transit.FromMap);
        var to = VagabondLocations.NormaliseMapName(transit.ToMap);

        if (from == RaidLocation.Nil || to == RaidLocation.Nil)
        {
            return null;
        }

        if (location != to)
        {
            return null;
        }

        return StaticTransitionsConfig.GetSpawn(from, to);
    }

    private static bool GetNormalRaidLocation(VagabondSessionState state, RaidLocation location,
        out ManualSpawnPoint? customExitSpawn)
    {
        customExitSpawn = null;
        var raid = VagabondLocations.NormaliseMapName(state.CurrentMap);
        var exitName = state.LastExit;

        if (location != raid || raid == RaidLocation.Nil || string.IsNullOrEmpty(exitName))
        {
            return false;
        }

        var exfil = ExfilService.GetCustomExfils(raid)
            .FirstOrDefault(x => x.Identifier == exitName);
        if (exfil == null)
        {
            return false;
        }

        customExitSpawn = new ManualSpawnPoint { X = exfil.X, Y = exfil.Y, Z = exfil.Z, Rotation = exfil.RotationY };
        return true;
    }

    private static bool GetHideoutLocation(VagabondSessionState state, RaidLocation location,
        out ManualSpawnPoint? customHideoutExitSpawn)
    {
        customHideoutExitSpawn = null;
        var raid = VagabondLocations.NormaliseMapName(state.CurrentMap);
        var exitName = state.LastExit;

        if (location != raid || raid == RaidLocation.Nil || string.IsNullOrEmpty(exitName))
        {
            return false;
        }

        var exfil = ExfilService.FindHideoutExfilByIdentifier(raid, exitName);
        if (exfil == null)
        {
            return false;
        }

        customHideoutExitSpawn = new ManualSpawnPoint
            { X = exfil.X, Y = exfil.Y, Z = exfil.Z, Rotation = exfil.RotationY };
        //VagabondLogger.Error($"forcing spawn at {customHideoutExitSpawn.X},{customHideoutExitSpawn.Y},{customHideoutExitSpawn.Z},R={customHideoutExitSpawn.Rotation}");
        return true;
    }

    private static bool GetTransitSpecificSpawnLocation(VagabondSessionState state, RaidLocation location,
        out ManualSpawnPoint? customTransitSpawn)
    {
        customTransitSpawn = null;
        if (state.TransitState == null)
        {
            return false;
        }

        var from = VagabondLocations.NormaliseMapName(state.TransitState?.FromMap);
        var to = VagabondLocations.NormaliseMapName(state.TransitState?.ToMap);
        var exitName = state.TransitState?.ExitName;

        if (location != to || to == RaidLocation.Nil || from == RaidLocation.Nil || string.IsNullOrEmpty(exitName))
        {
            return false;
        }

        var exfil = ExfilService.GetCustomExfils(from)
            .FirstOrDefault(x => x.Identifier == exitName);
        if (exfil == null)
        {
            return false;
        }

        if (string.IsNullOrEmpty(exfil.ConnectedIdentifier))
        {
            VagabondLogger.Error($"No ConnectedIdentifier on map {to}, exit {exfil.Identifier}");
            return false;
        }

        var position = ExfilService.GetCustomExfils(to)
            .FirstOrDefault(x => x.Identifier == exfil.ConnectedIdentifier);
        if (position == null)
        {
            VagabondLogger.Error($"No connected spawn found for {state.TransitState?.ExitName} on {to}");
            return false;
        }

        customTransitSpawn = new ManualSpawnPoint
            { X = position.X, Y = position.Y, Z = position.Z, Rotation = position.RotationY };
        return true;
    }
}