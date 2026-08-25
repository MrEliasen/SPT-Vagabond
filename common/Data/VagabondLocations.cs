using Vagabond.Common.Enums;

namespace Vagabond.Common.Data;

public static class VagabondLocations
{
    private static Dictionary<RaidLocation, string> _humanNames = new()
    {
        [RaidLocation.FactoryDay] = "Factory",
        [RaidLocation.FactoryNight] = "Factory",
        [RaidLocation.GroundZero] = "Ground Zero",
        [RaidLocation.Streets] = "Streets",
        [RaidLocation.Woods] = "Woods",
        [RaidLocation.Customs] = "Customs",
        [RaidLocation.Interchange] = "Interchange",
        [RaidLocation.Lighthouse] = "Lighthouse",
        [RaidLocation.Reserve] = "Reserve",
        [RaidLocation.Shoreline] = "Shoreline",
        [RaidLocation.Labs] = "Labs",
        [RaidLocation.Labyrinth] = "Labyrinth",
    };

    public static Dictionary<RaidLocation, List<string>> Locations = new()
    {
        [RaidLocation.FactoryDay] = ["55f2d3fd4bdc2d5f408b4567"],
        [RaidLocation.FactoryNight] = ["59fc81d786f774390775787e"],
        [RaidLocation.GroundZero] = ["65b8d6f5cdde2479cb2a3125", "653e6760052c01c1c805532f"],
        [RaidLocation.Streets] = ["5714dc692459777137212e12"],
        [RaidLocation.Woods] = ["5704e3c2d2720bac5b8b4567"],
        [RaidLocation.Customs] = ["56f40101d2720b2a4d8b45d6"],
        [RaidLocation.Interchange] = ["5714dbc024597771384a510d"],
        [RaidLocation.Lighthouse] = ["5704e4dad2720bb55b8b4567"],
        [RaidLocation.Reserve] = ["5704e5fad2720bc05b8b4567"],
        [RaidLocation.Shoreline] = ["5704e554d2720bac5b8b456e"],
        [RaidLocation.Labs] = ["5b0fc42d86f7744a585f9105"],
        [RaidLocation.Labyrinth] = ["6733700029c367a3d40b02af"],
    };

    public static Dictionary<string, string> IdToName = new(StringComparer.OrdinalIgnoreCase)
    {
        //EFT
        ["56f40101d2720b2a4d8b45d6"] = "bigmap",
        ["55f2d3fd4bdc2d5f408b4567"] = "factory4_day",
        ["65b8d6f5cdde2479cb2a3125"] = "Sandbox_high",
        ["653e6760052c01c1c805532f"] = "Sandbox",
        ["5714dbc024597771384a510d"] = "Interchange",
        ["5704e4dad2720bb55b8b4567"] = "Lighthouse",
        ["59fc81d786f774390775787e"] = "factory4_night",
        ["5704e5fad2720bc05b8b4567"] = "RezervBase",
        ["5704e554d2720bac5b8b456e"] = "Shoreline",
        ["5714dc692459777137212e12"] = "TarkovStreets",
        ["5b0fc42d86f7744a585f9105"] = "laboratory",
        ["6733700029c367a3d40b02af"] = "Labyrinth",
        ["5704e3c2d2720bac5b8b4567"] = "Woods",
    };

    public static Dictionary<RaidLocation, List<string>> InverseLookupTable = new()
    {
        [RaidLocation.FactoryDay] = new List<string>
        {
            "factory4_day",
        },
        [RaidLocation.FactoryNight] = new List<string>
        {
            "factory4_night",
        },
        [RaidLocation.GroundZero] = new List<string>
        {
            "Sandbox_high",
            "Sandbox",
        },
        [RaidLocation.Streets] = new List<string>
        {
            "TarkovStreets",
        },
        [RaidLocation.Woods] = new List<string>
        {
            "Woods",
        },
        [RaidLocation.Customs] = new List<string>
        {
            "bigmap",
        },
        [RaidLocation.Interchange] = new List<string>
        {
            "Interchange",
        },
        [RaidLocation.Lighthouse] = new List<string>
        {
            "Lighthouse",
        },
        [RaidLocation.Reserve] = new List<string>
        {
            "RezervBase",
        },
        [RaidLocation.Shoreline] = new List<string>
        {
            "Shoreline",
        },
        [RaidLocation.Labs] = new List<string>
        {
            "laboratory",
        },
        [RaidLocation.Labyrinth] = new List<string>
        {
            "Labyrinth",
        },
    };

    // helper used in api example
    public static string RaidLocationToMapName(RaidLocation raid)
    {
        return InverseLookupTable[raid].First();
    }

    public static Dictionary<string, RaidLocation> LookupTable = new(StringComparer.OrdinalIgnoreCase)
    {
        //EFT
        ["factory4_day"] = RaidLocation.FactoryDay,
        ["factory4_night"] = RaidLocation.FactoryNight,
        ["Sandbox_high"] = RaidLocation.GroundZero,
        ["Sandbox"] = RaidLocation.GroundZero,
        ["TarkovStreets"] = RaidLocation.Streets,
        ["Woods"] = RaidLocation.Woods,
        ["bigmap"] = RaidLocation.Customs,
        ["Interchange"] = RaidLocation.Interchange,
        ["Lighthouse"] = RaidLocation.Lighthouse,
        ["RezervBase"] = RaidLocation.Reserve,
        ["Shoreline"] = RaidLocation.Shoreline,
        ["laboratory"] = RaidLocation.Labs,
        ["labyrinth"] = RaidLocation.Labyrinth,
        //SPT
        ["Factory4Day"] = RaidLocation.FactoryDay,
        ["Factory4Night"] = RaidLocation.FactoryNight,
        ["SandboxHigh"] = RaidLocation.GroundZero,
    };

    public static string ToHumanName(RaidLocation location)
    {
        if (location == RaidLocation.Nil)
        {
            return "Unknown";
        }

        return _humanNames[location];
    }

    public static RaidLocation NormaliseMapName(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            return RaidLocation.Nil;
        }

        if (LookupTable.TryGetValue(mapName, out var mapped))
        {
            return mapped;
        }

        if (Enum.TryParse<RaidLocation>(mapName, true, out var parsed) && parsed != RaidLocation.Nil)
        {
            return parsed;
        }

        foreach (var loc in Locations)
        {
            if (loc.Value.Contains(mapName))
            {
                return loc.Key;
            }
        }

        return RaidLocation.Nil;
    }
}