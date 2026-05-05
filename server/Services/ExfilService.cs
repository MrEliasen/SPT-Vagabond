using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services;
using Vagabond.Common.Data;
using Vagabond.Common.Definitions;
using Vagabond.Common.Enums;
using Vagabond.Server.Config;
using Location = SPTarkov.Server.Core.Models.Eft.Common.Location;

namespace Vagabond.Server.Services;

internal static class ExfilService
{
    public static Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>> CustomExfils = new();
    public static Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>> HideoutExfils = new();
    private static Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>>? _snapshotCache;
    public static int SnapshotCacheVersion = 1;
    private static HashSet<string> _loadedHideoutExfils = new();

    // API-added entires offset start
    private static int _nextApiExfilOffset = 20000;

    private static Location? RaidLocationToLocation(DatabaseService databaseService, RaidLocation raid)
    {
        var locations = databaseService.GetLocations();
        return raid switch
        {
            RaidLocation.Customs => locations.Bigmap,
            RaidLocation.FactoryDay => locations.Factory4Day,
            RaidLocation.FactoryNight => locations.Factory4Night,
            RaidLocation.GroundZero => locations.SandboxHigh,
            RaidLocation.Interchange => locations.Interchange,
            RaidLocation.Lighthouse => locations.Lighthouse,
            RaidLocation.Reserve => locations.RezervBase,
            RaidLocation.Shoreline => locations.Shoreline,
            RaidLocation.Streets => locations.TarkovStreets,
            RaidLocation.Woods => locations.Woods,
            RaidLocation.Labs => locations.Laboratory,
            RaidLocation.Labyrinth => locations.Labyrinth,
            _ => null
        };
    }

    // GroundZero fix
    private static IEnumerable<(Location location, string mapName)> RaidLocationToSptLocations(
        DatabaseService databaseService, RaidLocation raid)
    {
        var locations = databaseService.GetLocations();
        switch (raid)
        {
            case RaidLocation.Customs: yield return (locations.Bigmap, "bigmap"); break;
            case RaidLocation.FactoryDay: yield return (locations.Factory4Day, "factory4_day"); break;
            case RaidLocation.FactoryNight: yield return (locations.Factory4Night, "factory4_night"); break;
            case RaidLocation.GroundZero:
                yield return (locations.SandboxHigh, "Sandbox_high");
                yield return (locations.Sandbox, "Sandbox");
                break;
            case RaidLocation.Interchange: yield return (locations.Interchange, "Interchange"); break;
            case RaidLocation.Lighthouse: yield return (locations.Lighthouse, "Lighthouse"); break;
            case RaidLocation.Reserve: yield return (locations.RezervBase, "RezervBase"); break;
            case RaidLocation.Shoreline: yield return (locations.Shoreline, "Shoreline"); break;
            case RaidLocation.Streets: yield return (locations.TarkovStreets, "TarkovStreets"); break;
            case RaidLocation.Woods: yield return (locations.Woods, "Woods"); break;
            case RaidLocation.Labs: yield return (locations.Laboratory, "laboratory"); break;
            case RaidLocation.Labyrinth: yield return (locations.Labyrinth, "labyrinth"); break;
        }
    }

    public static void RemoveHideout(HideoutState? state)
    {
        if (string.IsNullOrEmpty(state?.Id))
        {
            return;
        }

        var exfileId = $"{HideoutService.HideoutIdPrefix}{state.Id}";

        // remove hideout
        foreach (var raids in HideoutExfils)
        {
            foreach (var exfils in raids.Value)
            {
                for (var i = exfils.Value.Count - 1; i >= 0; i--)
                {
                    if (exfils.Value[i].Identifier == exfileId)
                    {
                        exfils.Value.RemoveAt(i);
                    }
                }
            }
        }

        // remove Extract
        foreach (var raids in CustomExfils)
        {
            foreach (var exfils in raids.Value)
            {
                for (var i = exfils.Value.Count - 1; i >= 0; i--)
                {
                    if (exfils.Value[i].Identifier == exfileId)
                    {
                        exfils.Value.RemoveAt(i);
                    }
                }
            }
        }

        _loadedHideoutExfils.Remove(state.Id);
    }

    public static void Apply(DatabaseService databaseService)
    {
        foreach (var loc in Enum.GetValues(typeof(RaidLocation)).Cast<RaidLocation>())
        {
            if (loc == RaidLocation.Nil)
            {
                continue;
            }

            if (!VagabondLocations.InverseLookupTable.TryGetValue(loc, out var maps))
            {
                continue;
            }

            var entExfils = new Dictionary<string, List<CustomExfil>>(StringComparer.OrdinalIgnoreCase);
            var entHideout = new Dictionary<string, List<CustomExfil>>(StringComparer.OrdinalIgnoreCase);
            foreach (var map in maps)
            {
                entExfils.Add(map, new List<CustomExfil>());
                entHideout.Add(map, new List<CustomExfil>());
            }

            CustomExfils.Add(loc, entExfils);
            HideoutExfils.Add(loc, entHideout);
        }

        var raidToOffset = new Dictionary<RaidLocation, int>
        {
            [RaidLocation.Customs] = 9000,
            [RaidLocation.FactoryDay] = 9100,
            [RaidLocation.FactoryNight] = 9200,
            [RaidLocation.GroundZero] = 9300,
            [RaidLocation.Interchange] = 9400,
            [RaidLocation.Lighthouse] = 9500,
            [RaidLocation.Reserve] = 9600,
            [RaidLocation.Shoreline] = 9700,
            [RaidLocation.Streets] = 9800,
            [RaidLocation.Woods] = 9900,
            [RaidLocation.Labs] = 10000,
            [RaidLocation.Labyrinth] = 11000,
        };

        foreach (var (raid, entry) in ExfilsConfig.Maps)
        {
            var sptLocs = RaidLocationToSptLocations(databaseService, raid).ToList();
            if (sptLocs.Count == 0)
            {
                continue;
            }

            var offset = raidToOffset.GetValueOrDefault(raid, 12000);
            foreach (var (location, mapName) in sptLocs)
            {
                AddExtractions(offset, location, raid, mapName, entry.Extracts, entry.Transits);
            }
        }
    }

    private static void AddExtractions(int pointIdOffset, Location location, RaidLocation raid, string mapName,
        List<CustomExfil> extracts, List<CustomExfil> transits)
    {
        var pmcEntryPoints = GetPmcEntryPoints(location);

        foreach (var ext in extracts)
        {
            ext.EntryPoints = pmcEntryPoints;
            CustomExfils[raid][mapName].Add(ext);
            AddOrReplaceExtract(location, ext);
        }

        var i = 1;
        foreach (var transit in transits)
        {
            transit.TransitPointId ??= pointIdOffset + i;
            CustomExfils[raid][mapName].Add(transit);
            AddOrReplaceTransit(location, transit);
            i++;
        }
    }

    public static bool IsCustomExtractName(string? name, RaidLocation raid, string mapName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return CustomExfils[raid][mapName]
            .Any(x => string.Equals(x.DisplayName, name, StringComparison.OrdinalIgnoreCase)
                      || string.Equals(x.Identifier, name, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddOrReplaceExtract(Location location, CustomExfil definition)
    {
        var allExtracts = location.AllExtracts.ToList();
        allExtracts.RemoveAll(x => string.Equals(x.Name, definition.DisplayName, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(x.SptName, definition.Identifier,
                                       StringComparison.OrdinalIgnoreCase));
        allExtracts.Add(CreateExit(definition));
        location.AllExtracts = allExtracts;

        var baseExits = location.Base.Exits.ToList();
        baseExits.RemoveAll(x => string.Equals(x.Name, definition.DisplayName, StringComparison.OrdinalIgnoreCase));
        baseExits.Add(CreateExit(definition));
        location.Base.Exits = baseExits;
    }

    private static void AddOrReplaceTransit(Location location, CustomExfil definition)
    {
        var transits = location.Base.Transits?.ToList() ?? new List<Transit>();
        transits.RemoveAll(x =>
            string.Equals(x.Name, definition.Identifier, StringComparison.OrdinalIgnoreCase)
            || (definition.TransitPointId.HasValue && x.Id == definition.TransitPointId.Value));

        transits.Add(new Transit
        {
            Name = definition.Identifier,
            Description = definition.Description,
            Conditions = string.Empty,
            Id = definition.TransitPointId,
            Location = definition.DestinationLocation,
            Target = string.IsNullOrWhiteSpace(definition.AccessKeysSourceLocation)
                ? definition.DestinationLocation
                : definition.AccessKeysSourceLocation,
            ActivateAfterSeconds = definition.ActivateAfterSeconds,
            Time = (long)Math.Round(definition.ExfiltrationTime),
            IsActive = definition.IsActive,
            Events = definition.Events,
            HideIfNoKey = definition.HideIfNoKey
        });

        location.Base.Transits = transits;
    }

    private static AllExtractsExit CreateExit(CustomExfil definition)
    {
        return new AllExtractsExit
        {
            Name = definition.DisplayName,
            SptName = definition.Identifier,
            Chance = 100,
            ChancePVE = 100,
            Count = 0,
            CountPVE = 0,
            EntryPoints = definition.EntryPoints,
            EventAvailable = false,
            ExfiltrationTime = definition.ExfiltrationTime,
            ExfiltrationTimePVE = definition.ExfiltrationTime,
            ExfiltrationType = ExfiltrationType.Individual,
            Id = string.Empty,
            MaxTime = 0,
            MaxTimePVE = 0,
            MinTime = 0,
            MinTimePVE = 0,
            PassageRequirement = RequirementState.None,
            PlayersCount = 0,
            PlayersCountPVE = 0,
            RequiredSlot = EquipmentSlots.FirstPrimaryWeapon,
            RequirementTip = string.Empty,
            Side = definition.Side
        };
    }

    private static string GetPmcEntryPoints(Location location)
    {
        var entryPoints = location.Base.Exits
            .Where(x => string.Equals(x.Side, "Pmc", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.EntryPoints)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .SelectMany(x => x!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return string.Join(",", entryPoints);
    }

    private static CustomExfil GetExtractTemplate(RaidLocation raid)
    {
        if (ExfilsConfig.Maps.TryGetValue(raid, out var entry) && entry.Extracts.Count > 0)
        {
            return entry.Extracts.First();
        }

        return new CustomExfil
        {
            Identifier = "",
            DisplayName = "",
            IsTransit = false,
            TemplateExitName = "",
            EntryPoints = "",
            ExfiltrationTime = 0f,
            X = 0f,
            Y = 0f,
            Z = 0f,
            RotationY = 0f,
            Side = "Pmc"
        };
    }

    public static bool AddHideoutExfil(PmcData pmc, VagabondSessionState state)
    {
        if (string.IsNullOrEmpty(state.HideoutState?.Id) || _loadedHideoutExfils.Contains(state.HideoutState.Id))
        {
            return false;
        }

        var hideoutExfil = GenerateHideoutExfil(pmc.Info?.Nickname!, state);
        if (hideoutExfil == null)
        {
            return false;
        }

        var explicitMap = state.HideoutState.Map;
        if (string.IsNullOrWhiteSpace(explicitMap) || !VagabondLocations.LookupTable.TryGetValue(explicitMap, out _))
        {
            return false;
        }

        var raid = VagabondLocations.NormaliseMapName(explicitMap);
        if (raid == RaidLocation.Nil)
        {
            return false;
        }

        if (!VagabondLocations.Locations.TryGetValue(raid, out _))
        {
            return false;
        }

        // remove existing exfil
        foreach (var raids in HideoutExfils)
        {
            foreach (var exfils in raids.Value)
            {
                for (var i = exfils.Value.Count - 1; i >= 0; i--)
                {
                    if (exfils.Value[i].Identifier == hideoutExfil.Identifier)
                    {
                        exfils.Value.RemoveAt(i);
                    }
                }
            }
        }

        List<RaidLocation> raidsToAdd = [raid];

        switch (raid)
        {
            case RaidLocation.FactoryDay:
            {
                raidsToAdd.Add(RaidLocation.FactoryNight);
                break;
            }

            case RaidLocation.FactoryNight:
            {
                raidsToAdd.Add(RaidLocation.FactoryDay);
                break;
            }
        }

        // patch in new one
        foreach (var r in raidsToAdd)
        {
            if (!VagabondLocations.InverseLookupTable.TryGetValue(r, out var mapNames))
            {
                continue;
            }

            foreach (var m in mapNames)
            {
                HideoutExfils[r][m].Add(hideoutExfil);
            }
        }

        _loadedHideoutExfils.Add(state.HideoutState.Id);
        return true;
    }

    private static CustomExfil? GenerateHideoutExfil(string profileName, VagabondSessionState state)
    {
        if (string.IsNullOrEmpty(state.HideoutState?.Id))
        {
            return null;
        }

        var template = GetExtractTemplate(
            VagabondLocations.NormaliseMapName(state.HideoutState?.Map ?? state.CurrentMap));

        var hideoutExfil = new CustomExfil
        {
            Identifier = $"{HideoutService.HideoutIdPrefix}{state.HideoutState?.Id}",
            DisplayName = $"{HideoutService.HideoutNamePrefix} ({profileName})",
            TemplateExitName = template.TemplateExitName,
            EntryPoints = template.EntryPoints,
            IsTransit = false,
            ExfiltrationTime = 20f,
            X = state.HideoutState?.X ?? 0f,
            Y = state.HideoutState?.Y ?? 0f,
            Z = state.HideoutState?.Z ?? 0f,
            RotationY = state.HideoutState?.R ?? 0f,
            Side = "Pmc"
        };

        return hideoutExfil;
    }

    public static Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>> BuildCustomExfilSnapshot(
        bool forceRebuild = false)
    {
        if (_snapshotCache != null && !forceRebuild)
        {
            return _snapshotCache;
        }

        var snapshot = new Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>>();
        foreach (var raidEntry in CustomExfils)
        {
            // VagabondLogger.Log($"populating Exfil for {raidEntry.Key}");
            if (!snapshot.TryGetValue(raidEntry.Key, out var snapshotByMap))
            {
                snapshotByMap = new Dictionary<string, List<CustomExfil>>(StringComparer.OrdinalIgnoreCase);
                snapshot[raidEntry.Key] = snapshotByMap;
            }

            foreach (var mapEntry in raidEntry.Value)
            {
                if (!snapshotByMap.TryGetValue(mapEntry.Key, out var snapshotList))
                {
                    snapshotList = new List<CustomExfil>();
                    snapshotByMap[mapEntry.Key] = snapshotList;
                }

                foreach (var exfil in mapEntry.Value)
                {
                    //VagabondLogger.Log($"Adding Exfil to {mapEntry.Key}");
                    snapshotList.Add(exfil);
                }
            }
        }

        foreach (var raidEntry in HideoutExfils)
        {
            //VagabondLogger.Log($"populating Hideouts for {raidEntry.Key}");
            if (!snapshot.TryGetValue(raidEntry.Key, out var snapshotByMap))
            {
                snapshotByMap = new Dictionary<string, List<CustomExfil>>(StringComparer.OrdinalIgnoreCase);
                snapshot[raidEntry.Key] = snapshotByMap;
            }

            foreach (var mapEntry in raidEntry.Value)
            {
                if (!snapshotByMap.TryGetValue(mapEntry.Key, out var snapshotList))
                {
                    snapshotList = new List<CustomExfil>();
                    snapshotByMap[mapEntry.Key] = snapshotList;
                }

                foreach (var exfil in mapEntry.Value)
                {
                    //VagabondLogger.Log($"Adding hideout to {mapEntry.Key}");
                    snapshotList.Add(exfil);
                }
            }
        }

        _snapshotCache = snapshot;
        SnapshotCacheVersion++;
        return snapshot;
    }

    /// <summary>
    /// API: add/replace custom exfils.
    /// </summary>
    internal static void AddCustomExfils(RaidLocation raid, List<CustomExfil> transits, List<CustomExfil> extracts)
    {
        if (!CustomExfils.TryGetValue(raid, out var raidMaps))
        {
            VagabondLogger.Warning($"AddCustomExfils: invalid raid '{raid}'.");
            return;
        }

        var databaseService = ReflectionUtil.GetService<DatabaseService>();
        var location = RaidLocationToLocation(databaseService!, raid);
        if (location == null)
        {
            VagabondLogger.Warning($"AddCustomExfils: no live location for raid '{raid}'; nothing applied.");
            return;
        }

        var newTransits = new List<CustomExfil>(transits);
        var newExtracts = new List<CustomExfil>(extracts);

        // dedupe
        var ids = new HashSet<string>(
            newTransits.Select(t => t.Identifier).Concat(newExtracts.Select(e => e.Identifier)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var list in raidMaps.Values)
        {
            list.RemoveAll(x => ids.Contains(x.Identifier));
        }

        // assign unique id
        foreach (var transit in newTransits)
        {
            transit.TransitPointId ??= Interlocked.Increment(ref _nextApiExfilOffset);
        }

        foreach (var alias in raidMaps.Keys)
        {
            AddExtractions(0, location, raid, alias, newExtracts, newTransits);
        }

        BuildCustomExfilSnapshot(forceRebuild: true);
    }

    /// <summary>
    /// API: remove custom exfil.
    /// </summary>
    internal static bool RemoveCustomExfil(RaidLocation raid, string exfilId)
    {
        if (string.IsNullOrWhiteSpace(exfilId))
        {
            return false;
        }

        if (!CustomExfils.TryGetValue(raid, out var byMap))
        {
            return false;
        }

        var removed = false;
        foreach (var list in byMap.Values)
        {
            removed |= list.RemoveAll(x =>
                string.Equals(x.Identifier, exfilId, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        if (!removed)
        {
            return false;
        }

        var databaseService = ReflectionUtil.GetService<DatabaseService>();
        if (databaseService == null)
        {
            return true;
        }

        var location = RaidLocationToLocation(databaseService, raid);

        if (location != null)
        {
            var displayNames = new HashSet<string>(
                location.AllExtracts
                    .Where(e => string.Equals(e.SptName, exfilId, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n ?? ""),
                StringComparer.OrdinalIgnoreCase);

            location.AllExtracts = location.AllExtracts
                .Where(e => !string.Equals(e.SptName, exfilId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            location.Base.Exits = location.Base.Exits
                .Where(e => string.IsNullOrEmpty(e.Name) || !displayNames.Contains(e.Name))
                .ToList();

            if (location.Base.Transits != null)
            {
                location.Base.Transits = location.Base.Transits
                    .Where(t => !string.Equals(t.Name, exfilId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        BuildCustomExfilSnapshot(forceRebuild: true);
        return true;
    }

    /// <summary>
    /// API: returns list of current custom exfils.
    /// </summary>
    internal static IReadOnlyList<CustomExfil> GetCustomExfils(RaidLocation raid)
    {
        if (!CustomExfils.TryGetValue(raid, out var byMap))
        {
            return Array.Empty<CustomExfil>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<CustomExfil>();
        foreach (var list in byMap.Values)
        {
            foreach (var exfil in list)
            {
                if (seen.Add(exfil.Identifier))
                {
                    merged.Add(exfil);
                }
            }
        }

        return merged;
    }
}