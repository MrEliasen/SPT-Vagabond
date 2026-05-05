using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using Vagabond.Common.Data;
using Vagabond.Server.Config;
using Vagabond.Common.Enums;
using Vagabond.Common.Definitions;
using Vagabond.Server.State;

namespace Vagabond.Server.Services;

internal static class VagabondService
{
    public static void ResetProfile(MongoId sessionId, PmcData pmc)
    {
        var inventory = pmc.Inventory;
        if (inventory == null)
        {
            VagabondLogger.Error("PlayerCompletedChallenge: inventory was null.");
            return;
        }

        var items = inventory.Items;
        if (items == null)
        {
            VagabondLogger.Error("PlayerCompletedChallenge: inventory items list was null.");
            return;
        }

        var state = StateService.GetState(sessionId);
        state.ResetProfile = false;

        RaidRuntimeState.Left(sessionId);
        VirtualStashService.ClearAllTraderStashes(sessionId);
        WipeItems(sessionId, pmc, true, true, true);

        state.CurrentMap = VagabondConfig.Config.StartRaid;
        state.LastExit = VagabondConfig.Config.StartExfilIdentifier;
        state.TransitState = null;
        ExfilService.RemoveHideout(state.HideoutState);
        state.HideoutState = null;
        state.CanPlaceHideout = true;
        HideoutService.UpdateTraderAccess(pmc, state);
        StateService.SaveState(sessionId, state);
        using var stashState = VirtualStashService.OpenStash(sessionId, pmc);
        AddMoney(sessionId, pmc);
    }

    public static SptProfile? GetPmcProfile(MongoId sessionId)
    {
        var server = ReflectionUtil.GetService<SaveServer>();
        if (server == null)
        {
            return null;
        }

        return server.GetProfile(sessionId);
    }

    public static void PersistProfileIfPossible(MongoId sessionId)
    {
        // prevent saving if virtual stash is enabled, to avoid overwriting player profile with incorrect stash data.
        // it could still happen elsewhere, like if spt saves somewhere internally right when we have a virtual stash loaded up..
        // not sure what I can do then..
        if (VirtualStashService.IsStashActive(sessionId))
        {
            return;
        }

        try
        {
            var server = ReflectionUtil.GetService<SaveServer>();
            if (server == null)
            {
                return;
            }

            server.SaveProfileAsync(sessionId);
        }
        catch (Exception ex)
        {
            VagabondLogger.Error($"PersistProfileIfPossible failed: {ex}");
        }
    }

    public static void WipeItems(MongoId sessionId, PmcData pmc, bool wipeEquipment = false, bool wipeStash = false,
        bool removeAllMoney = false)
    {
        var inventory = pmc.Inventory;
        if (inventory?.Items == null)
        {
            VagabondLogger.Error("WipeItems: inventory was null.");
            return;
        }

        var invHelper = ReflectionUtil.GetService<InventoryHelper>();
        if (invHelper == null)
        {
            VagabondLogger.Error("WipeItems: InventoryHelper not found");
            return;
        }

        var itemsById = inventory.Items.ToDictionary(x => (string)x.Id, x => x);
        var currencyIds = new HashSet<MongoId>
        {
            Currencies.Dollar,
            Currencies.Euro,
            Currencies.Ruble
        };

        var rootIdsToKeep = new HashSet<string>
        {
            inventory.Stash!,
            inventory.Equipment!,
            inventory.SortingTable!,
        };

        var idsToRemove = new HashSet<MongoId>();

        foreach (var item in inventory.Items)
        {
            var itemId = (string)item.Id;

            if (rootIdsToKeep.Contains(itemId))
            {
                continue;
            }

            if (string.Equals(item.SlotId, "Pockets", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (removeAllMoney && currencyIds.Contains(item.Template))
            {
                idsToRemove.Add(item.Id);
                continue;
            }

            if (wipeStash && IsUnderRoot(item, inventory.Stash!, itemsById))
            {
                idsToRemove.Add(item.Id);
                continue;
            }

            if (wipeEquipment && IsUnderRoot(item, inventory.Equipment!, itemsById))
            {
                idsToRemove.Add(item.Id);
            }
        }

        foreach (var id in idsToRemove)
        {
            invHelper.RemoveItem(pmc, id, sessionId);
        }
    }

    private static bool IsUnderRoot(Item item, string rootId, Dictionary<string, Item> itemsById)
    {
        var parentId = item.ParentId;

        while (!string.IsNullOrEmpty(parentId))
        {
            if (string.Equals(parentId, rootId, StringComparison.Ordinal))
            {
                return true;
            }

            if (!itemsById.TryGetValue(parentId, out var parent))
            {
                return false;
            }

            parentId = parent.ParentId;
        }

        return false;
    }

    public static bool ShouldApplyVagabondRules(MongoId sessionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            var pmc = GetPmcProfile(sessionId);
            if (pmc == null)
            {
                return false;
            }

            if (pmc.ProfileInfo == null)
            {
                return false;
            }

            return !(pmc.ProfileInfo.Username?.StartsWith("headless_") ?? false);
        }
        catch
        {
            return false;
        }
    }


    public static void AddMoney(MongoId sessionId, PmcData pmcData)
    {
        var invHelper = ReflectionUtil.GetService<InventoryHelper>();
        var eventOutputHolder = ReflectionUtil.GetService<EventOutputHolder>();

        if (invHelper == null || eventOutputHolder == null)
        {
            VagabondLogger.Error("AddMoney: required service was null.");
            return;
        }

        var amount = VagabondConfig.Config.StartingRoubles;
        while (amount > 0)
        {
            var moneyItem = new Item
            {
                Id = new MongoId(),
                Template = Currencies.Ruble,
                Upd = new Upd
                {
                    StackObjectsCount = amount > 500_000 ? 500_000 : amount,
                }
            };

            amount -= (int)(moneyItem.Upd?.StackObjectsCount ?? 500_000);

            var request = new AddItemDirectRequest
            {
                ItemWithModsToAdd = new List<Item> { moneyItem }
            };

            invHelper.AddItemToStash(sessionId, request, pmcData, eventOutputHolder.GetOutput(sessionId));
        }
    }

    public static string GetGroundZeroMapIdForLevel(int playerLevel)
    {
        if (VagabondConfig.Config.ForceGroundZeroHigh)
        {
            return "Sandbox_high";
        }

        var db = ReflectionUtil.GetService<DatabaseService>();
        var cap = db?.GetLocations().Sandbox?.Base?.RequiredPlayerLevelMax ?? 20;
        return playerLevel > cap ? "Sandbox_high" : "Sandbox";
    }

    public static string GetCurrentRaidId(MongoId sessionId, VagabondSessionState state)
    {
        if (string.IsNullOrEmpty(state.CurrentMap))
        {
            return "";
        }

        if (VagabondConfig.Config.EnablePickRaidLocation)
        {
            return "";
        }

        RaidLocation currentMap = VagabondLocations.NormaliseMapName(state.CurrentMap);
        if (currentMap == RaidLocation.Nil)
        {
            return "";
        }

        HashSet<string> allowedMapIds = new(StringComparer.OrdinalIgnoreCase);
        RaidLocation transitMap = VagabondLocations.NormaliseMapName(state.TransitState?.ToMap);
        if (transitMap != RaidLocation.Nil)
        {
            if (VagabondLocations.Locations.TryGetValue(transitMap, out var mapIds))
            {
                foreach (var mapId in mapIds)
                {
                    allowedMapIds.Add(mapId);
                }
            }
        }
        else
        {
            if (VagabondLocations.Locations.TryGetValue(currentMap, out var mapIds))
            {
                foreach (var mapId in mapIds)
                {
                    allowedMapIds.Add(mapId);
                }
            }
        }

        // GroundZero fix
        var effective = transitMap != RaidLocation.Nil ? transitMap : currentMap;
        if (effective == RaidLocation.GroundZero)
        {
            var lvl = GetPmcProfile(sessionId)?.CharacterData?.PmcData?.Info?.Level ?? 1;
            return GetGroundZeroMapIdForLevel(lvl);
        }

        return allowedMapIds.First();
    }

    public static bool IsInRaid(MongoId profileId)
    {
        if (RaidRuntimeState.IsInRaid(profileId))
        {
            return true;
        }

        var canonicalId = FikaAdapter.GetCanonicalSessionId(profileId);
        if (RaidRuntimeState.IsInRaid(canonicalId))
        {
            return true;
        }

        var ownerId = FikaAdapter.GetRaidOwnerSessionId(profileId);
        if (RaidRuntimeState.IsInRaid(ownerId))
        {
            return true;
        }

        return false;
    }
}