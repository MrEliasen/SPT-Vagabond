using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Vagabond.Common.Data;
using Vagabond.Common.Definitions;
using Vagabond.Common.Enums;
using Vagabond.Server.Config;
using Location = SPTarkov.Server.Core.Models.Eft.Common.Location;

namespace Vagabond.Server.Services;

internal static class ExfilService
{
    // Guards every mutation/enumeration of the collections below. Innermost lock: never held while
    // calling into StateService or VirtualStashService. Readers outside the lock only ever consume the
    // immutable snapshot reference; CustomExfil instances are frozen once registered.
    private static readonly Lock ExfilLock = new();

    private static readonly Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>> CustomExfils = new();
    private static readonly Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>> HideoutExfils = new();

    // A2: Kestrel accepts requests for the whole IOnLoad phase, while the routes and patches go live at
    // Preload and Apply only runs at PostLoad+1. Until Apply has populated the dictionaries there is
    // nothing to serve and nothing may be cached: a snapshot built from the empty dictionaries would be
    // published at a bumped version and, since ApplyLocked never invalidated it, would stay the answer
    // for the rest of the server run. The versioned exfil route answers not-ready with version 0 and an
    // empty set, which matches the client's own initial version so nothing is applied; the unversioned
    // state route answers with null, which the client reads as "no change" (MINOR-2).
    private static volatile bool _applied;

    // A5: read outside ExfilLock at BuildCustomExfilSnapshot, written inside it. Volatile for the same
    // reason HideoutService._traderLocations is: the build-new-then-swap publication needs a release
    // barrier on weakly-ordered targets (SPT ships linux-arm64).
    private static volatile Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>>? _snapshotCache;

    // Concurrency MINOR-3: the client's CustomExfilsCacheVersion (client/State/VagabondState.cs:19) is a
    // per-process static that is never reset, so it outlives a server restart. With a counter that
    // starts at the same number every boot, a surviving client sits at exactly the version the new
    // process serves first, the router's `payload.Version != version` gate never fires, and edited
    // configs are never delivered. Seeding per process puts each boot in its own version namespace.
    private static int _snapshotCacheVersion = SeedSnapshotVersion();
    private static readonly HashSet<string> _loadedHideoutExfils = new();

    private const int NotReadySnapshotVersion = 0;

    // API-added entires offset start
    private static int _nextApiExfilOffset = 20000;

    /// <summary>
    /// Boot-derived seed for the snapshot version counter (Concurrency MINOR-3). Two boots cannot land
    /// on the same value: the unix second and the process id are both mixed in. Masked to 30 bits so the
    /// value is always positive and in-process increments cannot overflow, and forced away from
    /// <see cref="NotReadySnapshotVersion"/>, which is the client's "nothing yet" sentinel. Versions stay
    /// monotonic within the process because the only other write is the increment in
    /// BuildCustomExfilSnapshotLocked. A large or non-sequential value is fine: both comparisons are
    /// plain inequality (VagabondRouter.HandleSyncExfilRoute and client CommunicationService).
    /// </summary>
    private static int SeedSnapshotVersion()
    {
        var mixed = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() << 16) ^ Environment.ProcessId;
        var seed = (int)(mixed & 0x3FFFFFFF);
        return seed == NotReadySnapshotVersion ? 1 : seed;
    }

    private static Location? RaidLocationToLocation(LocationTable locations, RaidLocation raid)
    {
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
        LocationTable locations, RaidLocation raid)
    {
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

        lock (ExfilLock)
        {
            RemoveHideoutLocked(state.Id, exfileId);
        }
    }

    private static void RemoveHideoutLocked(string hideoutId, string exfileId)
    {
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

        _loadedHideoutExfils.Remove(hideoutId);
    }

    public static void Apply(LocationTable locationTable)
    {
        lock (ExfilLock)
        {
            ApplyLocked(locationTable);
        }
    }

    private static void ApplyLocked(LocationTable locationTable)
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
            var sptLocs = RaidLocationToSptLocations(locationTable, raid).ToList();
            if (sptLocs.Count == 0)
            {
                continue;
            }

            var transits = NormalizeTransitDestinations(locationTable, entry.Transits, $"config '{raid}'");

            var offset = raidToOffset.GetValueOrDefault(raid, 12000);
            foreach (var (location, mapName) in sptLocs)
            {
                AddExtractions(offset, location, raid, mapName, entry.Extracts, transits);
            }
        }

        _applied = true;
        BuildCustomExfilSnapshotLocked(forceRebuild: true);
    }

    private static List<CustomExfil> NormalizeTransitDestinations(LocationTable locations,
        List<CustomExfil> transits, string source)
    {
        var valid = new List<CustomExfil>(transits.Count);
        foreach (var transit in transits)
        {
            var resolved = ResolveDbLocationId(locations, transit.DestinationLocation);
            if (resolved == null || !VagabondLocations.LookupTable.ContainsKey(resolved))
            {
                VagabondLogger.Error(
                    $"Transit '{transit.Identifier}' ({source}): DestinationLocation " +
                    $"'{transit.DestinationLocation}' does not resolve to a known raid location id. " +
                    "The transit is DROPPED — a broken destination id is the issue-136 no-extracts soft-lock.");
                continue;
            }

            if (!string.Equals(transit.DestinationLocation, resolved, StringComparison.Ordinal))
            {
                VagabondLogger.Warning(
                    $"Transit '{transit.Identifier}' ({source}): DestinationLocation " +
                    $"'{transit.DestinationLocation}' normalized to DB location id '{resolved}'.");
                transit.DestinationLocation = resolved;
            }

            if (!string.IsNullOrWhiteSpace(transit.AccessKeysSourceLocation))
            {
                var resolvedKeys = ResolveDbLocationId(locations, transit.AccessKeysSourceLocation);
                if (resolvedKeys == null)
                {
                    VagabondLogger.Warning(
                        $"Transit '{transit.Identifier}' ({source}): AccessKeysSourceLocation " +
                        $"'{transit.AccessKeysSourceLocation}' does not resolve to a DB location id; left as-is.");
                }
                else if (!string.Equals(transit.AccessKeysSourceLocation, resolvedKeys, StringComparison.Ordinal))
                {
                    VagabondLogger.Warning(
                        $"Transit '{transit.Identifier}' ({source}): AccessKeysSourceLocation " +
                        $"'{transit.AccessKeysSourceLocation}' normalized to DB location id '{resolvedKeys}'.");
                    transit.AccessKeysSourceLocation = resolvedKeys;
                }
            }

            valid.Add(transit);
        }

        return valid;
    }

    private static string? ResolveDbLocationId(LocationTable locations, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return locations.GetLocation(id)?.Base?.Id;
    }

    private static void AddExtractions(int pointIdOffset, Location location, RaidLocation raid, string mapName,
        List<CustomExfil> extracts, List<CustomExfil> transits)
    {
        var pmcEntryPoints = GetPmcEntryPoints(location);

        foreach (var ext in extracts)
        {
            var entryPoints = string.IsNullOrWhiteSpace(ext.EntryPoints)
                ? pmcEntryPoints
                : ext.EntryPoints;

            CustomExfils[raid][mapName].Add(ext);
            AddOrReplaceExtract(location, ext, entryPoints);
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

        lock (ExfilLock)
        {
            return TryGetExfils(CustomExfils, raid, mapName)
                ?.Any(x => string.Equals(x.DisplayName, name, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(x.Identifier, name, StringComparison.OrdinalIgnoreCase)) ?? false;
        }
    }

    public static CustomExfil? FindCustomExfil(RaidLocation raid, string mapName, string exitName)
    {
        lock (ExfilLock)
        {
            return TryGetExfils(CustomExfils, raid, mapName)?.FirstOrDefault(x =>
                string.Equals(x.Identifier, exitName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.DisplayName, exitName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static CustomExfil? FindHideoutExfilByDisplayName(RaidLocation raid, string mapName, string displayName)
    {
        lock (ExfilLock)
        {
            return TryGetExfils(HideoutExfils, raid, mapName)?.FirstOrDefault(x =>
                string.Equals(x.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static List<CustomExfil>? TryGetExfils(
        Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>> source, RaidLocation raid, string mapName)
    {
        return source.TryGetValue(raid, out var byMap) && byMap.TryGetValue(mapName, out var exfils)
            ? exfils
            : null;
    }

    public static CustomExfil? FindHideoutExfilByIdentifier(RaidLocation raid, string identifier)
    {
        lock (ExfilLock)
        {
            return HideoutExfils.TryGetValue(raid, out var byMap)
                ? byMap.SelectMany(x => x.Value).FirstOrDefault(y =>
                    string.Equals(y.Identifier, identifier, StringComparison.OrdinalIgnoreCase))
                : null;
        }
    }

    private static void AddOrReplaceExtract(Location location, CustomExfil definition, string entryPoints)
    {
        var allExtracts = location.AllExtracts.ToList();
        allExtracts.RemoveAll(x => string.Equals(x.Name, definition.DisplayName, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(x.SptName, definition.Identifier,
                                       StringComparison.OrdinalIgnoreCase));
        allExtracts.Add(CreateExit(definition, entryPoints));
        location.AllExtracts = allExtracts;

        var baseExits = location.Base.Exits.ToList();
        baseExits.RemoveAll(x => string.Equals(x.Name, definition.DisplayName, StringComparison.OrdinalIgnoreCase));
        baseExits.Add(CreateExit(definition, entryPoints));
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
            Target = ResolveLocationMongoId(
                string.IsNullOrWhiteSpace(definition.AccessKeysSourceLocation)
                    ? definition.DestinationLocation
                    : definition.AccessKeysSourceLocation),
            ActivateAfterSeconds = definition.ActivateAfterSeconds,
            Time = (long)Math.Round(definition.ExfiltrationTime),
            IsActive = definition.IsActive,
            Events = definition.Events,
            HideIfNoKey = definition.HideIfNoKey
        });

        location.Base.Transits = transits;
    }

    private static string? ResolveLocationMongoId(string? mapName)
    {
        var raid = VagabondLocations.NormaliseMapName(mapName);
        if (raid == RaidLocation.Nil || !VagabondLocations.Locations.TryGetValue(raid, out var ids) ||
            ids.Count == 0)
        {
            VagabondLogger.Warning(
                $"Transit target '{mapName}' does not resolve to a location id; the transit's key gate " +
                "will not apply.");
            return mapName;
        }

        return ids.First();
    }

    private static AllExtractsExit CreateExit(CustomExfil definition, string entryPoints)
    {
        return new AllExtractsExit
        {
            Name = definition.DisplayName,
            SptName = definition.Identifier,
            Chance = 100,
            ChancePVE = 100,
            Count = 0,
            CountPVE = 0,
            EntryPoints = entryPoints,
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
        var entryPoints = location.AllExtracts
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
        lock (ExfilLock)
        {
            return AddHideoutExfilLocked(pmc, state);
        }
    }

    private static bool AddHideoutExfilLocked(PmcData pmc, VagabondSessionState state)
    {
        if (!_applied)
        {
            return false;
        }

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

    public static (int Version, Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>>? Snapshot)
        GetSnapshotWithVersion()
    {
        if (!_applied)
        {
            return (NotReadySnapshotVersion, null);
        }

        lock (ExfilLock)
        {
            var snapshot = BuildCustomExfilSnapshotLocked(false);
            return (_snapshotCacheVersion, snapshot);
        }
    }

    public static Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>>? BuildCustomExfilSnapshot(
        bool forceRebuild = false)
    {
        if (!_applied)
        {
            return null;
        }

        var cached = _snapshotCache;
        if (cached != null && !forceRebuild)
        {
            return cached;
        }

        lock (ExfilLock)
        {
            return BuildCustomExfilSnapshotLocked(forceRebuild);
        }
    }

    private static Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>> EmptySnapshot()
    {
        return new Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>>();
    }

    private static Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>> BuildCustomExfilSnapshotLocked(
        bool forceRebuild)
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
        _snapshotCacheVersion++;
        return snapshot;
    }

    internal static void AddCustomExfils(RaidLocation raid, List<CustomExfil> transits, List<CustomExfil> extracts)
    {
        lock (ExfilLock)
        {
            AddCustomExfilsLocked(raid, transits, extracts);
        }
    }

    private static void AddCustomExfilsLocked(RaidLocation raid, List<CustomExfil> transits, List<CustomExfil> extracts)
    {
        if (!CustomExfils.TryGetValue(raid, out var raidMaps))
        {
            VagabondLogger.Warning($"AddCustomExfils: invalid raid '{raid}'.");
            return;
        }

        var locationTable = ReflectionUtil.GetService<LocationTable>();
        var location = RaidLocationToLocation(locationTable!, raid);
        if (location == null)
        {
            VagabondLogger.Warning($"AddCustomExfils: no live location for raid '{raid}'; nothing applied.");
            return;
        }

        var newTransits = NormalizeTransitDestinations(locationTable!, new List<CustomExfil>(transits),
            $"api '{raid}'");
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

        BuildCustomExfilSnapshotLocked(forceRebuild: true);
    }

    internal static bool RemoveCustomExfil(RaidLocation raid, string exfilId)
    {
        lock (ExfilLock)
        {
            return RemoveCustomExfilLocked(raid, exfilId);
        }
    }

    private static bool RemoveCustomExfilLocked(RaidLocation raid, string exfilId)
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

        var locationTable = ReflectionUtil.GetService<LocationTable>();
        if (locationTable == null)
        {
            return true;
        }

        var location = RaidLocationToLocation(locationTable, raid);

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

        BuildCustomExfilSnapshotLocked(forceRebuild: true);
        return true;
    }

    internal static IReadOnlyList<CustomExfil> GetCustomExfils(RaidLocation raid)
    {
        lock (ExfilLock)
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
}