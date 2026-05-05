using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.Mod;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using Vagabond.Common;
using Vagabond.Common.Api;
using Vagabond.Server.Config;
using Vagabond.Server.Services;

namespace Vagabond.Server;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = VagabondModInfo.Guid;
    public override string Name { get; init; } = VagabondModInfo.Name;
    public override string Author { get; init; } = VagabondModInfo.Author;
    public override SemanticVersioning.Version Version { get; init; } = new(VagabondModInfo.Version);
    public override SemanticVersioning.Range SptVersion { get; init; } = new(VagabondModInfo.SptVersion);
    public override string? Url { get; init; } = VagabondModInfo.Url;
    public override string License { get; init; } = VagabondModInfo.License;
    public override List<string>? Contributors { get; init; } = new() { VagabondModInfo.Author };
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override bool? IsBundleMod { get; init; }
}

[Injectable(TypePriority = OnLoadOrder.PreSptModLoader)]
public sealed class VagabondLoader : IOnLoad
{
    public VagabondLoader(ISptLogger<VagabondLoader> logger)
    {
        VagabondLogger.Init(logger);
    }

    public Task OnLoad()
    {
        // exfils/transits
        Api.AddExfilsImpl = ExfilService.AddCustomExfils;
        Api.RemoveExfilImpl = ExfilService.RemoveCustomExfil;
        Api.GetExfilsImpl = ExfilService.GetCustomExfils;
        // traders
        Api.AddTraderLocationsImpl = HideoutService.AddTraderLocations;
        Api.RemoveTraderLocationImpl = HideoutService.RemoveTraderLocation;
        Api.GetTraderLocationsImpl = HideoutService.GetTraderLocations;
        // state
        Api.GetStateImpl = StateService.GetState;
        Api.SaveStateImpl = StateService.SaveState;

        VagabondConfig.Initialize();
        ExfilsConfig.Initialize();
        TraderLocationsConfig.Initialize();
        StaticTransitionsConfig.Initialize();
        HideoutService.LoadTraderLocations(TraderLocationsConfig.Locations);

        new Patches.MailAttachmentsPatch().Enable();
        new Patches.ProfileBootstrapPatch().Enable();
        new Patches.ProfileCreatePatch().Enable();
        new Patches.RaidEndPatch().Enable();
        new Patches.RaidJoinPatch().Enable();
        new Patches.ChooseRaidLocationsPatch().Enable();
        new Patches.StartLocalRaidPatch().Enable();
        new Patches.GetCompleteProfilePatch().Enable();
        new Patches.QuestCallbacksAcceptQuestPatch().Enable();
        new Patches.QuestCallbacksCompleteQuestPatch().Enable();
        new Patches.QuestControllerGetClientQuestsPatch().Enable();
        new Patches.ItemEventRouterHandleEventsPatch().Enable();
        new Patches.TradeHelperBuyItemPatch().Enable();
        new Patches.TradeHelperSellItemPatch().Enable();
        new Patches.PaymentServicePayMoneyPatch().Enable();
        new Patches.InventoryHelperAddItemsToStashPatch().Enable();
        new Patches.InventoryHelperAddItemToStashPatch().Enable();
        new Patches.InventoryHelperCanPlaceItemsInInventoryPatch().Enable();
        new Patches.InventoryCallbacksTagItemPatch().Enable();
        new Patches.InventoryCallbacksSortInventoryPatch().Enable();
        new Patches.InventoryCallbacksPinOrLockPatch().Enable();
        new Patches.InventoryCallbacksSetFavoriteItemPatch().Enable();
        new Patches.RaidLootGenerationPatch().Enable();
        new Patches.LooseLootMultiplierPatch().Enable();
        new Patches.StaticLootMultiplierPatch().Enable();

        return Task.CompletedTask;
    }
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public sealed class VagabondDbLoader : IOnLoad
{
    private readonly IServiceProvider _services;
    private readonly ProfileDataService _profileDataService;
    private readonly SaveServer _saveServer;
    private readonly InventoryHelper _invHelper;
    private readonly EventOutputHolder _eventOutputHolder;
    private readonly DatabaseService _databaseService;
    private readonly MailSendService _mailSendService;
    private readonly LocationController _locationController;
    private readonly CustomQuestService _customQuestService;
    private readonly LocaleService _localeService;

    private readonly ISptLogger<VagabondDbLoader> _logger;

    public VagabondDbLoader(
        IServiceProvider services,
        ProfileDataService profileDataService,
        DatabaseService databaseService,
        SaveServer saveServer,
        InventoryHelper invHelper,
        EventOutputHolder eventOutputHolder,
        MailSendService mailSendService,
        LocationController locationController,
        CustomQuestService customQuestService,
        LocaleService localeService,
        ISptLogger<VagabondDbLoader> logger)
    {
        _services = services;
        _profileDataService = profileDataService;
        _saveServer = saveServer;
        _logger = logger;
        _invHelper = invHelper;
        _eventOutputHolder = eventOutputHolder;
        _mailSendService = mailSendService;
        _locationController = locationController;
        _customQuestService = customQuestService;
        _databaseService = databaseService;
        _localeService = localeService;
    }

    public Task OnLoad()
    {
        ReflectionUtil.Register(_services);
        ReflectionUtil.Register(_profileDataService);
        ReflectionUtil.Register(_saveServer);
        ReflectionUtil.Register(_invHelper);
        ReflectionUtil.Register(_eventOutputHolder);
        ReflectionUtil.Register(_mailSendService);
        ReflectionUtil.Register(_locationController);
        ReflectionUtil.Register(_databaseService);
        ReflectionUtil.Register(_customQuestService);
        ReflectionUtil.Register(_localeService);
        ExfilService.Apply(_databaseService);

        if (FikaAdapter.Init(_services))
        {
            _logger.Success("[Vagabond] Fika detected.");
        }

        _logger.Success($"[Vagabond] modules loaded.");
        return Task.CompletedTask;
    }
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public class GameChanges(DatabaseService databaseService) : IOnLoad
{
    public Task OnLoad()
    {
        var locationsdb = databaseService.GetLocations();
        locationsdb.SandboxHigh.Base.Enabled = true;

        Globals globals = databaseService.GetGlobals();
        globals.Configuration.SavagePlayCooldown = 14400;
        globals.Configuration.Exp.MatchEnd.SurvivedExperienceRequirement = 0;
        globals.Configuration.Exp.MatchEnd.SurvivedSecondsRequirement = 0;

        if (VagabondConfig.Config.DisableFlea)
        {
            globals.Configuration.RagFair.MinUserLevel = 99;
        }

        if (VagabondConfig.Config.DisableEvents)
        {
            globals.Configuration.EventSettings.EventActive = false;
        }

        // Credit: https://github.com/GhostFenixx/svm-csharp
        // ty ty <3
        var items = databaseService.GetItems();
        foreach (TemplateItem basetemplate in items.Values)
        {
            //Remove container Restrictions
            if (basetemplate.Parent == "5448e53e4bdc2d60728b4567")
            {
                if (basetemplate.Properties?.Grids == null)
                {
                    continue;
                }

                List<Grid> gridsfilters = basetemplate.Properties.Grids.ToList();
                gridsfilters.ForEach(grid =>
                {
                    try
                    {
                        if (grid.Properties?.Filters is not null)
                        {
                            var filters = grid.Properties.Filters.ToList();
                            if (filters.Count > 0)
                            {
                                filters[0].Filter?.Clear();
                                filters[0].Filter?.Add(new MongoId("54009119af1c881c07000029"));
                                filters[0].ExcludedFilter = [];
                                grid.Properties.Filters = filters;
                            }
                        }
                    }
                    catch
                    {
                    }
                });

                basetemplate.Properties.Grids = gridsfilters;
            }

            //Remove in-raid restrictions
            if (basetemplate.Type == "Item" && basetemplate.Properties?.DiscardLimit is not null)
            {
                basetemplate.Properties.DiscardLimit = -1;
            }

            // remove trial heals
            globals.Configuration.Health.HealPrice.TrialRaids = 0;
            globals.Configuration.Health.HealPrice.TrialLevels = 0;

            //Remove max number of item you can take in raid
            // Credit: https://github.com/acidphantasm/itemlimitsbegone-csharp/
            // ty ty <3
            var restrictionsInRaid = globals.Configuration.RestrictionsInRaid;

            foreach (var restriction in restrictionsInRaid)
            {
                restriction.MaxInRaid = Int32.MaxValue;
                restriction.MaxInLobby = Int32.MaxValue;
            }
        }

        if (VagabondConfig.Config.AdjustRaidTimeMins != 0)
        {
            foreach (Location names in locationsdb.GetDictionary().Values)
            {
                names.Base.ExitAccessTime += VagabondConfig.Config.AdjustRaidTimeMins;
                names.Base.EscapeTimeLimit += VagabondConfig.Config.AdjustRaidTimeMins;
                names.Base.EscapeTimeLimitCoop += VagabondConfig.Config.AdjustRaidTimeMins;
                names.Base.EscapeTimeLimitPVE += VagabondConfig.Config.AdjustRaidTimeMins;
            }
        }

        QuestService.LoadQuests();

        return Task.CompletedTask;
    }
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 10)]
public class FenceTweaks(
    DatabaseService databaseService,
    ConfigServer configServer,
    TraderHelper traderHelper,
    FenceBaseAssortGenerator fenceBaseAssortGenerator,
    FenceService fenceService
) : IOnLoad
{
    public Task OnLoad()
    {
        if (!VagabondConfig.Config.EnableFenceChanges)
        {
            return Task.CompletedTask;
        }

        var traderConfig = configServer.GetConfig<TraderConfig>();
        traderConfig.Fence.DiscountOptions.AssortSize = 0;

        // durability
        traderConfig.Fence.WeaponDurabilityPercentMinMax.Max.Min = 90;
        traderConfig.Fence.WeaponDurabilityPercentMinMax.Max.Max = 100;
        traderConfig.Fence.WeaponDurabilityPercentMinMax.Current.Min = 80;
        traderConfig.Fence.WeaponDurabilityPercentMinMax.Current.Max = 95;

        traderConfig.Fence.ArmorMaxDurabilityPercentMinMax.Max.Min = 55;
        traderConfig.Fence.ArmorMaxDurabilityPercentMinMax.Max.Max = 85;
        traderConfig.Fence.ArmorMaxDurabilityPercentMinMax.Current.Min = 40;
        traderConfig.Fence.ArmorMaxDurabilityPercentMinMax.Current.Max = 75;

        // refreshing
        traderConfig.Fence.PartialRefreshChangePercent = 0;
        traderConfig.Fence.PartialRefreshTimeSeconds = 600;
        traderConfig.Fence.RegenerateAssortsOnRefresh = false;
        foreach (var update in traderConfig.UpdateTime)
        {
            if (update.TraderId == "579dc571d53a0658a154fbec")
            {
                update.Seconds.Min = 600;
                update.Seconds.Max = 600;
            }
        }

        // limits
        traderConfig.Fence.AmmoMaxPenLimit = 26;
        traderConfig.Fence.ItemStackSizeOverrideMinMax[BaseClasses.AMMO] = new MinMax<int>
        {
            Min = 25,
            Max = 100
        };

        traderConfig.Fence.ItemStackSizeOverrideMinMax[BaseClasses.AMMO_BOX] = new MinMax<int>
        {
            Min = 1,
            Max = 3
        };

        //  presets
        traderConfig.Fence.DiscountOptions.AssortSize = 0;
        traderConfig.Fence.WeaponPresetMinMax.Min = 20;
        traderConfig.Fence.WeaponPresetMinMax.Max = 30;
        traderConfig.Fence.EquipmentPresetMinMax.Min = 3;
        traderConfig.Fence.EquipmentPresetMinMax.Max = 5;
        traderConfig.Fence.AssortSize = 150;

        // pricing
        traderConfig.Fence.ItemCategoryRoublePriceLimit[BaseClasses.AMMO] = 180;
        traderConfig.Fence.ItemCategoryRoublePriceLimit[BaseClasses.WEAPON] = 40000;
        traderConfig.Fence.ItemCategoryRoublePriceLimit[BaseClasses.BACKPACK] = 22000;
        traderConfig.Fence.ItemCategoryRoublePriceLimit[BaseClasses.VEST] = 25000;
        traderConfig.Fence.ItemCategoryRoublePriceLimit[BaseClasses.ARMORED_EQUIPMENT] = 30000;
        traderConfig.Fence.ItemCategoryRoublePriceLimit[BaseClasses.MEDICAL] = 20000;
        traderConfig.Fence.ItemCategoryRoublePriceLimit[BaseClasses.OPTIC_SCOPE] = 10000;
        traderConfig.Fence.ItemCategoryRoublePriceLimit[BaseClasses.SILENCER] = 5000;

        // Weapons and ammo
        traderConfig.Fence.ItemTypeLimits[BaseClasses.AMMO] = 30;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.AMMO_BOX] = 15;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.MAGAZINE] = 15;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.PISTOL] = 5;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.SMG] = 5;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.SHOTGUN] = 5;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.ASSAULT_RIFLE] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.ASSAULT_CARBINE] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.MARKSMAN_RIFLE] = 0;

        // equipment
        traderConfig.Fence.ItemTypeLimits[BaseClasses.BACKPACK] = 4;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.VEST] = 3;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.ARMORED_EQUIPMENT] = 2;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.HEADWEAR] = 1;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.HEADPHONES] = 1;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.FACE_COVER] = 0;

        // misc
        traderConfig.Fence.ItemTypeLimits[BaseClasses.MEDICAL] = 4;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.HANDGUARD] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.MUZZLE] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.FLASHLIGHT] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.FUNCTIONAL_MOD] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.MOUNT] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.DRINK] = 1;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.FOOD] = 1;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.FOOD_DRINK] = 1;

        // stuff we dont want to sell
        traderConfig.Fence.ItemTypeLimits[BaseClasses.SIMPLE_CONTAINER] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.MOB_CONTAINER] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.LOOT_CONTAINER] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.GEAR_MOD] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.RECEIVER] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.PISTOL_GRIP] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.STOCK] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.IRON_SIGHT] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.MUZZLE_COMBO] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.BARREL] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.BARTER_ITEM] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.COLLIMATOR] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.COMPACT_COLLIMATOR] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.OPTIC_SCOPE] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.ASSAULT_SCOPE] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.SPECIAL_SCOPE] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.SILENCER] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.LIGHT_LASER] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.TACTICAL_COMBO] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.MASTER_MOD] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.AUXILIARY_MOD] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.CHARGE] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.BIPOD] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.LAUNCHER] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.HOUSEHOLD_GOODS] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.KEY] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.KEY_MECHANICAL] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.KEYCARD] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.MULTITOOLS] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.SPEC_ITEM] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.THERMAL_VISION] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.TOOL] = 0;
        traderConfig.Fence.ItemTypeLimits[BaseClasses.VISORS] = 0;

        // resock
        fenceBaseAssortGenerator.GenerateFenceBaseAssorts();
        fenceService.GenerateFenceAssorts();

        var traders = databaseService.GetTraders();
        if (traders.TryGetValue("579dc571d53a0658a154fbec", out var fence))
        {
            fence.Base.NextResupply = (int)traderHelper.GetNextUpdateTimestamp(fence.Base.Id);
            fence.Base.RefreshTraderRagfairOffers = true;
        }

        return Task.CompletedTask;
    }
}