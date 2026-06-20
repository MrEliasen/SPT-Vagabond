using System.Collections.Concurrent;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services.Mod;
using SPTarkov.Server.Core.Utils.Cloners;
using Vagabond.Server.Models;
using Vagabond.Common;
using Vagabond.Server.Config;
using Vagabond.Server.State;

namespace Vagabond.Server.Services;

internal static class VirtualStashService
{
    private const string ProfileDataKeyPrefix = VagabondModInfo.Guid + ".virtual_stash";
    private const string StashRootIdPlaceholder = "vgb_stash_root";
    private const string SortingTableRootIdPlaceholder = "vgb_sorting_root";

    // Temp stash key for exits/transits that are not a hideout or a trader location.
    private const string TempStashKey = "VGB_TEMP_STASH";

    private const string BlockedActionMessage =
        "This action is not possible in this stash, as it is not your hideout stash.";

    private static readonly ConcurrentDictionary<MongoId, Lock> ActiveScopeLocks = new();
    private static readonly Dictionary<MongoId, ActiveVirtualStashState> ActiveStashes = new();

    public static bool IsVirtualStashEnabled(MongoId sessionId)
    {
        return TryGetActiveStashId(sessionId, out _);
    }

    public static bool IsStashActive(MongoId sessionId)
    {
        var sessionLock = ActiveScopeLocks.GetOrAdd(sessionId, _ => new Lock());
        lock (sessionLock)
        {
            return ActiveStashes.ContainsKey(sessionId);
        }
    }

    public static IDisposable OpenStash(MongoId sessionId, PmcData? pmcData = null)
    {
        if (!TryGetActiveStashId(sessionId, out var stashKey))
        {
            return Noop.Instance;
        }

        if (pmcData == null)
        {
            pmcData = ResolvePmcData(sessionId);
        }

        if (pmcData?.Inventory?.Items == null)
        {
            return Noop.Instance;
        }

        var sessionLock = ActiveScopeLocks.GetOrAdd(sessionId, _ => new Lock());
        lock (sessionLock)
        {
            if (ActiveStashes.TryGetValue(sessionId, out var activeState))
            {
                if (!string.Equals(activeState.StashKey, stashKey, StringComparison.Ordinal))
                {
                    VagabondLogger.Error(
                        $"Virtual stash mismatch for {sessionId}, active key {activeState.StashKey}, requested key {stashKey}.");
                    return Noop.Instance;
                }

                activeState.Depth++;
                return new ActiveStashSession(sessionId);
            }

            var overlayState = new ActiveVirtualStashState(sessionId, stashKey, pmcData);

            try
            {
                overlayState.RealItemsSnapshot = CollectVirtualItems(pmcData);
                RemoveItems(pmcData.Inventory.Items, overlayState.RealItemsSnapshot);

                overlayState.LoadedVirtualItems = LoadProjectedItems(sessionId, stashKey, pmcData.Inventory.Stash,
                    pmcData.Inventory.SortingTable);
                if (overlayState.LoadedVirtualItems.Count > 0)
                {
                    pmcData.Inventory.Items.AddRange(overlayState.LoadedVirtualItems);
                }

                ActiveStashes[sessionId] = overlayState;
                return new ActiveStashSession(sessionId);
            }
            catch (Exception ex)
            {
                TryRestoreStashOverlay(overlayState);
                VagabondLogger.Error($"Failed to open virtual stash: {ex}");
                return Noop.Instance;
            }
        }
    }

    public static void ApplyToClientProfile(MongoId sessionId, PmcData pmcData)
    {
        if (!TryGetActiveStashId(sessionId, out var stashKey))
        {
            return;
        }

        if (pmcData.Inventory?.Items == null)
        {
            return;
        }

        var currentVisibleItems = CollectVirtualItems(pmcData);
        RemoveItems(pmcData.Inventory.Items, currentVisibleItems);

        var projectedItems =
            LoadProjectedItems(sessionId, stashKey, pmcData.Inventory.Stash, pmcData.Inventory.SortingTable);
        if (projectedItems.Count > 0)
        {
            pmcData.Inventory.Items.AddRange(projectedItems);
        }
    }

    public static void ClearAllTraderStashes(MongoId sessionId)
    {
        var profileDataService = ReflectionUtil.GetService<ProfileDataService>();
        if (profileDataService == null)
        {
            return;
        }

        var sessionLock = ActiveScopeLocks.GetOrAdd(sessionId, _ => new Lock());
        lock (sessionLock)
        {
            if (ActiveStashes.TryGetValue(sessionId, out var overlayState))
            {
                ActiveStashes.Remove(sessionId);
                TryRestoreStashOverlay(overlayState);
            }
        }

        foreach (var stashKey in HideoutService.GetStashKeys())
        {
            profileDataService.SaveProfileData(sessionId, GetProfileStashKey(stashKey), new VirtualStashData
            {
                StashKey = stashKey,
                Items = new List<Item>()
            });
        }
    }

    public static void ClearTempStash(MongoId sessionId)
    {
        var profileDataService = ReflectionUtil.GetService<ProfileDataService>();
        if (profileDataService == null)
        {
            return;
        }

        var sessionLock = ActiveScopeLocks.GetOrAdd(sessionId, _ => new Lock());
        lock (sessionLock)
        {
            if (ActiveStashes.TryGetValue(sessionId, out var overlayState) &&
                string.Equals(overlayState.StashKey, TempStashKey, StringComparison.Ordinal))
            {
                ActiveStashes.Remove(sessionId);
                TryRestoreStashOverlay(overlayState);
            }
        }

        profileDataService.SaveProfileData(sessionId, GetProfileStashKey(TempStashKey), new VirtualStashData
        {
            StashKey = TempStashKey,
            Items = new List<Item>()
        });
    }

    public static ItemEventRouterResponse CreateBlockedActionResponse(MongoId sessionId, string? message = null)
    {
        var output = ReflectionUtil.GetService<EventOutputHolder>()?.GetOutput(sessionId) ?? new ItemEventRouterResponse
        {
            Warnings = new List<Warning>()
        };

        AppendBlockedActionWarning(output, message);
        return output;
    }

    public static void AppendBlockedActionWarning(ItemEventRouterResponse output, string? message = null)
    {
        output.Warnings = new List<Warning>();
        output.Warnings.Add(new Warning
        {
            ErrorMessage = string.IsNullOrWhiteSpace(message) ? BlockedActionMessage : message,
            Code = BackendErrorCodes.None
        });
    }

    private static void CloseStash(MongoId sessionId)
    {
        var sessionLock = ActiveScopeLocks.GetOrAdd(sessionId, _ => new Lock());
        lock (sessionLock)
        {
            if (!ActiveStashes.TryGetValue(sessionId, out var overlayState))
            {
                return;
            }

            overlayState.Depth--;
            if (overlayState.Depth > 0)
            {
                return;
            }

            ActiveStashes.Remove(sessionId);

            try
            {
                var currentVirtualItems = CollectVirtualItems(overlayState.PmcData);
                SaveVirtualStash(
                    sessionId,
                    overlayState.StashKey,
                    currentVirtualItems,
                    overlayState.PmcData.Inventory?.Stash,
                    overlayState.PmcData.Inventory?.SortingTable
                );

                if (overlayState.PmcData.Inventory?.Items != null)
                {
                    RemoveItems(overlayState.PmcData.Inventory.Items, currentVirtualItems);
                    if (overlayState.RealItemsSnapshot.Count > 0)
                    {
                        overlayState.PmcData.Inventory.Items.AddRange(overlayState.RealItemsSnapshot);
                    }
                }
            }
            catch (Exception ex)
            {
                VagabondLogger.Error($"Failed to close virtual stash: {ex}");
                TryRestoreStashOverlay(overlayState);
            }
        }
    }

    private static void TryRestoreStashOverlay(ActiveVirtualStashState overlayState)
    {
        try
        {
            var inventoryItems = overlayState.PmcData.Inventory?.Items;
            if (inventoryItems == null)
            {
                return;
            }

            RemoveItems(inventoryItems, overlayState.LoadedVirtualItems);

            if (overlayState.RealItemsSnapshot.Count > 0)
            {
                var existingIds = new HashSet<MongoId>(inventoryItems.Select(x => x.Id));
                foreach (var item in overlayState.RealItemsSnapshot)
                {
                    if (!existingIds.Contains(item.Id))
                    {
                        inventoryItems.Add(item);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            VagabondLogger.Error($"Failed to restore overlay state: {ex}");
        }
    }

    private static void SaveVirtualStash(
        MongoId sessionId,
        string stashKey,
        List<Item> currentVirtualItems,
        MongoId? stashRootId,
        MongoId? sortingTableRootId)
    {
        var profileDataService = ReflectionUtil.GetService<ProfileDataService>();
        if (profileDataService == null)
        {
            return;
        }

        var itemsToPersist = CloneItems(currentVirtualItems);
        UpdateRootReferences(itemsToPersist, stashRootId, sortingTableRootId);

        profileDataService.SaveProfileData(sessionId, GetProfileStashKey(stashKey), new VirtualStashData
        {
            StashKey = stashKey,
            Items = itemsToPersist
        });
    }

    private static List<Item> LoadProjectedItems(
        MongoId sessionId,
        string stashKey,
        MongoId? targetStashRootId,
        MongoId? targetSortingTableRootId)
    {
        var overlayItems = GetActiveStashItems(sessionId, stashKey, targetStashRootId, targetSortingTableRootId);
        if (overlayItems != null)
        {
            return overlayItems;
        }

        var profileDataService = ReflectionUtil.GetService<ProfileDataService>();
        if (profileDataService == null)
        {
            return new List<Item>();
        }

        var profileData =
            profileDataService.GetProfileData<VirtualStashData>(sessionId, GetProfileStashKey(stashKey));
        var items = CloneItems(profileData?.Items);
        RebindRootReferences(items, targetStashRootId, targetSortingTableRootId);
        return items;
    }

    private static List<Item>? GetActiveStashItems(
        MongoId sessionId,
        string stashKey,
        MongoId? targetStashRootId,
        MongoId? targetSortingTableRootId)
    {
        var sessionLock = ActiveScopeLocks.GetOrAdd(sessionId, _ => new Lock());
        lock (sessionLock)
        {
            if (!ActiveStashes.TryGetValue(sessionId, out var overlayState))
            {
                return null;
            }

            if (!string.Equals(overlayState.StashKey, stashKey, StringComparison.Ordinal))
            {
                return null;
            }

            var currentItems = CollectVirtualItems(overlayState.PmcData);
            var clonedItems = CloneItems(currentItems);
            UpdateRootReferences(clonedItems, overlayState.PmcData.Inventory?.Stash,
                overlayState.PmcData.Inventory?.SortingTable);
            RebindRootReferences(clonedItems, targetStashRootId, targetSortingTableRootId);
            return clonedItems;
        }
    }

    private static List<Item> CloneItems(IEnumerable<Item>? items)
    {
        if (items == null)
        {
            return new List<Item>();
        }

        var cloner = ReflectionUtil.GetService<ICloner>();
        var materialisedItems = items.ToList();

        if (cloner == null)
        {
            return new List<Item>(materialisedItems);
        }

        return cloner.Clone(materialisedItems) ?? new List<Item>();
    }

    private static void UpdateRootReferences(List<Item> items, MongoId? stashRootId, MongoId? sortingTableRootId)
    {
        var stashRoot = stashRootId?.ToString();
        var sortingRoot = sortingTableRootId?.ToString();

        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(stashRoot) && string.Equals(item.ParentId, stashRoot, StringComparison.Ordinal))
            {
                item.ParentId = StashRootIdPlaceholder;
                continue;
            }

            if (!string.IsNullOrEmpty(sortingRoot) &&
                string.Equals(item.ParentId, sortingRoot, StringComparison.Ordinal))
            {
                item.ParentId = SortingTableRootIdPlaceholder;
            }
        }
    }

    private static void RebindRootReferences(List<Item> items, MongoId? stashRootId, MongoId? sortingTableRootId)
    {
        var stashRoot = stashRootId?.ToString() ?? string.Empty;
        var sortingRoot = sortingTableRootId?.ToString() ?? string.Empty;

        foreach (var item in items)
        {
            if (string.Equals(item.ParentId, StashRootIdPlaceholder, StringComparison.Ordinal))
            {
                item.ParentId = stashRoot;
                continue;
            }

            if (string.Equals(item.ParentId, SortingTableRootIdPlaceholder, StringComparison.Ordinal))
            {
                item.ParentId = sortingRoot;
            }
        }
    }

    private static List<Item> CollectVirtualItems(PmcData pmcData)
    {
        var inventory = pmcData.Inventory;
        var items = inventory?.Items;
        if (items == null || items.Count == 0)
        {
            return new List<Item>();
        }

        var stashRoot = inventory?.Stash?.ToString();
        var sortingRoot = inventory?.SortingTable?.ToString();
        if (string.IsNullOrEmpty(stashRoot) && string.IsNullOrEmpty(sortingRoot))
        {
            return new List<Item>();
        }

        var itemsById = items.ToDictionary(item => (string)item.Id, item => item);
        var result = new List<Item>();

        foreach (var item in items)
        {
            var itemId = (string)item.Id;
            if ((!string.IsNullOrEmpty(stashRoot) && string.Equals(itemId, stashRoot, StringComparison.Ordinal))
                || (!string.IsNullOrEmpty(sortingRoot) && string.Equals(itemId, sortingRoot, StringComparison.Ordinal)))
            {
                continue;
            }

            if (IsUnderRoot(item, stashRoot, itemsById) || IsUnderRoot(item, sortingRoot, itemsById))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static bool IsUnderRoot(Item item, string? rootId, Dictionary<string, Item> itemsById)
    {
        if (string.IsNullOrEmpty(rootId))
        {
            return false;
        }

        var parentId = item.ParentId;
        while (!string.IsNullOrEmpty(parentId))
        {
            if (string.Equals(parentId, rootId, StringComparison.Ordinal))
            {
                return true;
            }

            if (!itemsById.TryGetValue(parentId, out var parentItem))
            {
                return false;
            }

            parentId = parentItem.ParentId;
        }

        return false;
    }

    private static void RemoveItems(List<Item> sourceItems, IEnumerable<Item> itemsToRemove)
    {
        var idsToRemove = new HashSet<MongoId>(itemsToRemove.Select(x => x.Id));
        if (idsToRemove.Count == 0)
        {
            return;
        }

        sourceItems.RemoveAll(item => idsToRemove.Contains(item.Id));
    }

    private static string GetProfileStashKey(string stashKey)
    {
        return $"{ProfileDataKeyPrefix}.{stashKey}";
    }

    // Used in the migration: rename profile stash entries from a per-trader key to a per-exfil indentifier key - from 0.6.1.
    // This allows multiple traders to be at the same location. 
    internal static void RekeyStash(MongoId sessionId, string oldStashKey, string newStashKey)
    {
        if (string.IsNullOrWhiteSpace(oldStashKey) || string.IsNullOrWhiteSpace(newStashKey))
        {
            VagabondLogger.Error(
                $"Stash migraiton failed, missing stash key(s): Session={sessionId}, oldStashKey={oldStashKey}, newStashKey={newStashKey}");
            return;
        }

        // no change needed
        if (string.Equals(oldStashKey, newStashKey, StringComparison.Ordinal))
        {
            return;
        }

        VagabondLogger.Warning(
            $"Migrating stash. Session={sessionId}, oldStashKey={oldStashKey}, newStashKey={newStashKey}");

        var profileDataService = ReflectionUtil.GetService<ProfileDataService>();
        if (profileDataService == null)
        {
            VagabondLogger.Error($"Migrating failed, ProfileDataService is null");
            return;
        }

        var oldKey = GetProfileStashKey(oldStashKey);
        var oldData = profileDataService.GetProfileData<VirtualStashData>(sessionId, oldKey);
        if (oldData == null)
        {
            VagabondLogger.Success($"Stash migration successful.");
            return;
        }

        var newKey = GetProfileStashKey(newStashKey);
        var existing = profileDataService.GetProfileData<VirtualStashData>(sessionId, newKey);
        if (existing == null)
        {
            profileDataService.SaveProfileData(sessionId, newKey, new VirtualStashData
            {
                StashKey = newStashKey,
                Items = oldData.Items
            });
        }

        // delete the old stash file
        try
        {
            var oldPath = System.IO.Path.Combine("user/profileData/", sessionId.ToString(), oldKey + ".json");
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }
        }
        catch (Exception ex)
        {
            VagabondLogger.Error($"Failed to delete old stash file {oldStashKey}: {ex}");
        }

        VagabondLogger.Success($"Stash migration successful.");
    }

    private static PmcData? ResolvePmcData(MongoId sessionId)
    {
        return VagabondService.GetPmcProfile(sessionId)?.CharacterData?.PmcData;
    }

    private static bool TryGetActiveStashId(MongoId sessionId, out string stashId)
    {
        stashId = string.Empty;

        if (!VagabondConfig.Config.EnableVirtualStashes)
        {
            return false;
        }

        // I want to make sure while in-raid, whatever you do does not involve any virtual stash
        if (VagabondService.IsInRaid(sessionId))
        {
            return false;
        }

        if (!VagabondService.ShouldApplyVagabondRules(sessionId))
        {
            return false;
        }

        var state = StateService.GetState(sessionId);
        if (!state.VagabondModeEnabled)
        {
            return false;
        }

        if (HideoutService.GetTraderIds(state).Count > 0 && !string.IsNullOrWhiteSpace(state.LastExit))
        {
            stashId = state.LastExit;
            return true;
        }

        // If the player took eg. another players hideout exfil, we need a stash for that as well.
        if (!string.IsNullOrEmpty(state.HideoutState?.Id) &&
            state.LastExit != $"{HideoutService.HideoutIdPrefix}{state.HideoutState?.Id}")
        {
            if (state.LastExit.IndexOf(HideoutService.HideoutIdPrefix, StringComparison.OrdinalIgnoreCase) == 0)
            {
                // unless share hideout is enabled, in which case they use their own hideout stash
                if (VagabondConfig.Config.ShareHideoutExits)
                {
                    return false;
                }

                stashId = state.LastExit;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(state.LastExit) &&
            state.LastExit.IndexOf(HideoutService.HideoutIdPrefix, StringComparison.OrdinalIgnoreCase) != 0)
        {
            stashId = TempStashKey;
            return true;
        }

        return false;
    }

    private sealed class ActiveStashSession : IDisposable
    {
        private readonly MongoId _sessionId;
        private bool _disposed;

        public ActiveStashSession(MongoId sessionId)
        {
            _sessionId = sessionId;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseStash(_sessionId);
        }
    }
}