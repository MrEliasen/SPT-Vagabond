using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using Vagabond.Common.Api;
using Vagabond.Common.Data;
using Vagabond.Common.Definitions;
using Vagabond.Common.Enums;

namespace Vagabond.ApiExample;

public class ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "dev.oogabooga.vagabond-api-example";
    public string Name { get; init; } = "Vagabond API Example";
    public string Author { get; init; } = "Oogabooga.dev";
    public SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public string? Url { get; init; } = "https://github.com/MrEliasen/spt-vagabond";
    public string License { get; init; } = "MIT";
    public List<string>? Contributors { get; init; } = new() { "Oogabooga.dev" };
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
}

// The "+2" makes sure your mod initialises after Vagabond's database loader (which is set to PostLoad + 1).
// This is needed for the API to be available for you to use.
[Injectable(TypePriority = OnLoadOrder.PostLoad + 2)]
public sealed class VagabondApiExampleLoader : IOnLoad
{
    private readonly ISptLogger<VagabondApiExampleLoader> _logger;

    public VagabondApiExampleLoader(ISptLogger<VagabondApiExampleLoader> logger)
    {
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (IsVagabondEnabled())
        {
            RunVagabondIntegration();
        }

        return Task.CompletedTask;
    }

    private static bool IsVagabondEnabled()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, "Vagabond.Common", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void RunVagabondIntegration()
    {
        _logger.Warning("Adding stuff via Vagabond API");
        List<CustomExfil> myCustomExfils =
        [
            // example of adding a custom extract, which we will hook up to give access to Fence trader.
            new CustomExfil
            {
                // The unique Identifier for your exfil.
                // If an exfil exists with this Identifier it will be overwritten by this one
                Identifier = "MYMOD_EXT_FENCE",
                DisplayName = "Fence's Woods Hideout",
                // only fill if you want this extract to copy a specific exfil's template which already exists in the game
                TemplateExitName = "",
                // Set to true ONLY if you want to repurpose an existing in-scene exfil
                // (e.g. Reserve's D2 bunker switch). When true, X/Y/Z are IGNORED — the
                // hijacked exfil keeps its original position. For a brand-new exfil at
                // your X/Y/Z coords, leave this false.
                HijackExfil = false,
                // only fill if you want to use specific entry points
                EntryPoints = "",
                // how long extraction timer is 20s in this case
                ExfiltrationTime = 20f,
                // the XYZ coords of the exfil location on the map
                X = -211.918f,
                Y = 76.064f,
                Z = -269.262f,
                // and which way to look when spawning back in from this exfil
                RotationY = 161.529f,
                // who can use it, players are Pmc, but maybe you want to do a scav extension or something.
                Side = "Pmc"
            }
        ];

        List<CustomExfil> myCustomTransits =
        [
            new CustomExfil
            {
                // The unique Identifier for your exfil.
                // If an exfil exists with this Identifier it will be overwritten by this one
                Identifier = "MYMOD_WOODS_TO_GZ",
                // since its a transit, make sure you designate it as such
                IsTransit = true,
                // What raid this transit goes to.
                DestinationLocation = VagabondLocations.RaidLocationToMapName(RaidLocation.GroundZero),
                // If you want a player to infil / spawn on a specific extract or transit location,
                // specify the exfils Identifier here.
                // Example: assuming we had another transit or extract we added to GZ
                ConnectedIdentifier = "MYMOD_GZ_TO_WOODS",
                Description = "Transit to Ground Zero",
                ExfiltrationTime = 15f, // 15s transit timer
                IsActive = true,
                // the XYZ coords of the transit location on the map
                X = -178.181f,
                Y = 58.151f,
                Z = -306.06f,
                // and which way to look if this location is used as an infil from another transition
                RotationY = 194.459f,
            },
            new CustomExfil
            {
                // The unique Identifier for your exfil.
                // If an exfil exists with this Identifier it will be overwritten by this one
                Identifier = "MYMOD_WOODS_TO_LABS",
                // since its a transit, make sure you designate it as such
                IsTransit = true,
                // What raid this transit goes to.
                DestinationLocation = VagabondLocations.RaidLocationToMapName(RaidLocation.Labs),
                // here we override the need for a labs key to transit to labs via this transit,
                // by specifying a location which does not require a key, like customs.
                // If you leave it blank it will use the Destination's default key (in this case Labs) - vanilla behavior 
                AccessKeysSourceLocation = VagabondLocations.RaidLocationToMapName(RaidLocation.Customs),
                Description = "Transit to Labs",
                ExfiltrationTime = 15f,
                IsActive = true,
                // in case we didn't override AccessKeysSourceLocation, it would require a labs key to use this transit.
                // in which case, if you set this to true, it would hide this transit if you didn't have a labs key on you.
                HideIfNoKey = false,
                // the XYZ coords of the transit location on the map
                X = -207.251f,
                Y = 57.438f,
                Z = -322.552f,
                // and which way to look if this location is used as an infil from another transition
                RotationY = 45.435f,
            },
        ];

        // here we add the transits and exfils we made, to "Woods"
        Api.AddExfils(RaidLocation.Woods, myCustomTransits, myCustomExfils);
        _logger.Success("Added additional exfils via Vagabond API");

        // Now, lets add  the new Fence exfil we made
        Api.AddTraderLocations([
            new TraderLocation
            {
                // The ID of the trader
                TraderId = "579dc571d53a0658a154fbec",
                // the raid we are adding the location to
                Raid = RaidLocation.Woods,
                // the Identifier of the exfil they need to use to access this trader
                ExfilIdentifier = "MYMOD_EXT_FENCE",
            }
        ]);
        _logger.Success("Added additional fence location via Vagabond API");

        // placeholder for sessionId or profileId for a player
        var sessionId = "69ebbbcfa4878b67303ec776";
        var vagabondState = Api.GetState(sessionId);
        if (vagabondState != null)
        {
            // change the current map and exit of the profile to the fence we just created.
            vagabondState.CurrentMap = nameof(RaidLocation.Woods);
            vagabondState.LastExit = "MYMOD_EXT_FENCE";
            Api.SaveState(sessionId, vagabondState);
        }

        _logger.Success("Changed the profiles location via Vagabond API");
    }
}