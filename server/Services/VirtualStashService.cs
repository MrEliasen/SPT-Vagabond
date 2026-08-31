using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services.Modding;
using SPTarkov.Server.Core.Utils;
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
    private const string TempStashKey = "VGB_TEMP_STASH";

    private const string BlockedActionMessage =
        "This action is not possible in this stash, as it is not your hideout stash.";

    private static readonly ConcurrentDictionary<MongoId, Lock> ActiveScopeLocks = new();
    private static readonly ConcurrentDictionary<MongoId, ActiveVirtualStashState> ActiveStashes = new();
    private static readonly ConcurrentDictionary<MongoId, SemaphoreSlim> SessionGates = new();
    private static readonly AsyncLocal<GateOwnership?> CurrentGateOwnership = new();
    private static readonly ConcurrentDictionary<string, string> PersistedStashSignatures = new();
    private static readonly ConcurrentDictionary<MongoId, byte> ResetDeferralLogged = new();
    private static readonly ConcurrentDictionary<MongoId, byte> SaveDeferralLogged = new();

    public static bool IsVirtualStashEnabled(MongoId sessionId)
    {
        return TryGetActiveStashId(sessionId, out _);
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

        var gateHold = AcquireGate(sessionId);
        try
        {
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
                    var nestedSession = new ActiveStashSession(sessionId, gateHold);
                    gateHold = null;
                    return nestedSession;
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
                    var session = new ActiveStashSession(sessionId, gateHold);
                    gateHold = null;
                    return session;
                }
                catch (Exception ex)
                {
                    TryRestoreStashOverlay(overlayState);
                    VagabondLogger.Error($"Failed to open virtual stash: {ex}");
                    return Noop.Instance;
                }
            }
        }
        finally
        {
            gateHold?.Dispose();
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

        var gateHold = AcquireGate(sessionId);
        try
        {
            var currentVisibleItems = CollectVirtualItems(pmcData);
            RemoveItems(pmcData.Inventory.Items, currentVisibleItems);

            var projectedItems =
                LoadProjectedItems(sessionId, stashKey, pmcData.Inventory.Stash, pmcData.Inventory.SortingTable);
            if (projectedItems.Count > 0)
            {
                pmcData.Inventory.Items.AddRange(projectedItems);
            }
        }
        finally
        {
            gateHold?.Dispose();
        }
    }

    public static void ClearAllTraderStashes(MongoId sessionId)
    {
        var profileDataService = ReflectionUtil.GetService<ProfileDataService>();
        if (profileDataService == null)
        {
            return;
        }

        var gateHold = AcquireGate(sessionId);
        try
        {
            var sessionLock = ActiveScopeLocks.GetOrAdd(sessionId, _ => new Lock());
            lock (sessionLock)
            {
                if (ActiveStashes.TryRemove(sessionId, out var overlayState))
                {
                    TryRestoreStashOverlay(overlayState);
                }
            }

            foreach (var stashKey in HideoutService.GetStashKeys())
            {
                profileDataService.SaveProfileDataAsync(sessionId, GetProfileStashKey(stashKey), new VirtualStashData
                {
                    StashKey = stashKey,
                    Items = new List<Item>()
                }).GetAwaiter().GetResult();
                PersistedStashSignatures.TryRemove(GetSignatureCacheKey(sessionId, stashKey), out _);
            }
        }
        finally
        {
            gateHold?.Dispose();
        }
    }

    public static void ClearTempStash(MongoId sessionId)
    {
        var profileDataService = ReflectionUtil.GetService<ProfileDataService>();
        if (profileDataService == null)
        {
            return;
        }

        var gateHold = AcquireGate(sessionId);
        try
        {
            var sessionLock = ActiveScopeLocks.GetOrAdd(sessionId, _ => new Lock());
            lock (sessionLock)
            {
                if (ActiveStashes.TryGetValue(sessionId, out var overlayState) &&
                    string.Equals(overlayState.StashKey, TempStashKey, StringComparison.Ordinal))
                {
                    ActiveStashes.TryRemove(sessionId, out _);
                    TryRestoreStashOverlay(overlayState);
                }
            }

            profileDataService.SaveProfileDataAsync(sessionId, GetProfileStashKey(TempStashKey), new VirtualStashData
            {
                StashKey = TempStashKey,
                Items = new List<Item>()
            }).GetAwaiter().GetResult();
            PersistedStashSignatures.TryRemove(GetSignatureCacheKey(sessionId, TempStashKey), out _);
        }
        finally
        {
            gateHold?.Dispose();
        }
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

            ActiveStashes.TryRemove(sessionId, out _);

            try
            {
                var currentVirtualItems = CollectVirtualItems(overlayState.PmcData);
                PersistVirtualStashIfChanged(sessionId, overlayState, currentVirtualItems);

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

    private static void PersistVirtualStashIfChanged(
        MongoId sessionId,
        ActiveVirtualStashState overlayState,
        List<Item> currentVirtualItems)
    {
        var signature = ComputeItemsSignature(currentVirtualItems);
        var cacheKey = GetSignatureCacheKey(sessionId, overlayState.StashKey);
        if (signature != null &&
            PersistedStashSignatures.TryGetValue(cacheKey, out var lastPersisted) &&
            string.Equals(lastPersisted, signature, StringComparison.Ordinal))
        {
            return;
        }

        var persisted = SaveVirtualStash(
            sessionId,
            overlayState.StashKey,
            currentVirtualItems,
            overlayState.PmcData.Inventory?.Stash,
            overlayState.PmcData.Inventory?.SortingTable
        );

        if (persisted && signature != null)
        {
            PersistedStashSignatures[cacheKey] = signature;
        }
        else
        {
            PersistedStashSignatures.TryRemove(cacheKey, out _);
        }
    }

    private static bool SaveVirtualStash(
        MongoId sessionId,
        string stashKey,
        List<Item> currentVirtualItems,
        MongoId? stashRootId,
        MongoId? sortingTableRootId)
    {
        var profileDataService = ReflectionUtil.GetService<ProfileDataService>();
        if (profileDataService == null)
        {
            return false;
        }

        var itemsToPersist = CloneItems(currentVirtualItems);
        UpdateRootReferences(itemsToPersist, stashRootId, sortingTableRootId);

        profileDataService.SaveProfileDataAsync(sessionId, GetProfileStashKey(stashKey), new VirtualStashData
        {
            StashKey = stashKey,
            Items = itemsToPersist
        }).GetAwaiter().GetResult();
        return true;
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

        var profileData = profileDataService
            .GetProfileDataAsync<VirtualStashData>(sessionId, GetProfileStashKey(stashKey))
            .GetAwaiter().GetResult();
        var items = CloneItems(profileData?.Items);
        RebindRootReferences(items, targetStashRootId, targetSortingTableRootId);

        var cacheKey = GetSignatureCacheKey(sessionId, stashKey);
        if (!PersistedStashSignatures.ContainsKey(cacheKey))
        {
            var signature = ComputeItemsSignature(items);
            if (signature != null)
            {
                PersistedStashSignatures[cacheKey] = signature;
            }
        }

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

    private static string GetSignatureCacheKey(MongoId sessionId, string stashKey)
    {
        return $"{sessionId}:{stashKey}";
    }

    private static string? ComputeItemsSignature(List<Item> items)
    {
        var jsonUtil = ReflectionUtil.GetService<JsonUtil>();
        var json = jsonUtil?.Serialize(items);
        if (json == null)
        {
            return null;
        }

        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static bool FlowOwnsGate(MongoId sessionId)
    {
        var ownership = CurrentGateOwnership.Value;
        return ownership is { Released: false } && ownership.SessionId == sessionId;
    }

    internal static bool CurrentFlowOwnsGate(MongoId sessionId)
    {
        return FlowOwnsGate(sessionId);
    }

    internal static bool CurrentFlowOwnsForeignGate(MongoId sessionId)
    {
        var ownership = CurrentGateOwnership.Value;
        return ownership is { Released: false } && ownership.SessionId != sessionId;
    }

    internal static IDisposable AcquireGateScope(MongoId sessionId)
    {
        return (IDisposable?)AcquireGate(sessionId) ?? Noop.Instance;
    }

    internal static IDisposable? TryAcquireGateScope(MongoId sessionId)
    {
        if (FlowOwnsGate(sessionId))
        {
            return Noop.Instance;
        }

        var gate = SessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(ExternalSaveGateTimeoutMs))
        {
            return null;
        }

        ClearGateTimeoutLatches(sessionId);
        var ownership = new GateOwnership(sessionId);
        CurrentGateOwnership.Value = ownership;
        return new SessionGateHold(gate, ownership);
    }

    private static SessionGateHold? AcquireGate(MongoId sessionId)
    {
        if (FlowOwnsGate(sessionId))
        {
            return null;
        }

        var gate = SessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        gate.Wait();

        ClearGateTimeoutLatches(sessionId);
        var ownership = new GateOwnership(sessionId);
        CurrentGateOwnership.Value = ownership;
        return new SessionGateHold(gate, ownership);
    }

    internal static bool ShouldLogResetDeferral(MongoId sessionId)
    {
        return ResetDeferralLogged.TryAdd(sessionId, 1);
    }

    private static void ClearGateTimeoutLatches(MongoId sessionId)
    {
        if (!ResetDeferralLogged.IsEmpty)
        {
            ResetDeferralLogged.TryRemove(sessionId, out _);
        }

        if (!SaveDeferralLogged.IsEmpty)
        {
            SaveDeferralLogged.TryRemove(sessionId, out _);
        }
    }

    private const int ExternalSaveGateTimeoutMs = 5000;

    internal static ProfileSaveScope? BeginProfileSaveScope(MongoId sessionId)
    {
        if (FlowOwnsGate(sessionId))
        {
            var suspension = SuspendOverlay(sessionId);
            return suspension == null ? null : new ProfileSaveScope(null, suspension);
        }

        var gate = SessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(ExternalSaveGateTimeoutMs))
        {
            if (SaveDeferralLogged.TryAdd(sessionId, 1))
            {
                VagabondLogger.Warning(
                    $"Skipping profile save for {sessionId}: virtual stash window still open after {ExternalSaveGateTimeoutMs} ms; autosave will retry.");
            }

            return ProfileSaveScope.Skipped;
        }

        ClearGateTimeoutLatches(sessionId);
        var ownership = new GateOwnership(sessionId);
        CurrentGateOwnership.Value = ownership;
        return new ProfileSaveScope(new SessionGateHold(gate, ownership), null);
    }

    private static OverlaySuspension? SuspendOverlay(MongoId sessionId)
    {
        var sessionLock = ActiveScopeLocks.GetOrAdd(sessionId, _ => new Lock());
        lock (sessionLock)
        {
            if (!ActiveStashes.TryGetValue(sessionId, out var overlayState))
            {
                return null;
            }

            var inventoryItems = overlayState.PmcData.Inventory?.Items;
            if (inventoryItems == null)
            {
                return null;
            }

            var currentVirtualItems = CollectVirtualItems(overlayState.PmcData);
            RemoveItems(inventoryItems, currentVirtualItems);
            if (overlayState.RealItemsSnapshot.Count > 0)
            {
                inventoryItems.AddRange(overlayState.RealItemsSnapshot);
            }

            return new OverlaySuspension(overlayState, currentVirtualItems);
        }
    }

    private static void ResumeOverlay(OverlaySuspension suspension)
    {
        var overlayState = suspension.OverlayState;
        var sessionLock = ActiveScopeLocks.GetOrAdd(overlayState.SessionId, _ => new Lock());
        lock (sessionLock)
        {
            if (!ActiveStashes.TryGetValue(overlayState.SessionId, out var currentState) ||
                !ReferenceEquals(currentState, overlayState))
            {
                return;
            }

            var inventoryItems = overlayState.PmcData.Inventory?.Items;
            if (inventoryItems == null)
            {
                return;
            }

            RemoveItems(inventoryItems, overlayState.RealItemsSnapshot);
            if (suspension.VirtualItems.Count > 0)
            {
                inventoryItems.AddRange(suspension.VirtualItems);
            }
        }
    }

    internal sealed class ProfileSaveScope
    {
        internal static readonly ProfileSaveScope Skipped = new(null, null);

        private readonly SessionGateHold? _gateHold;
        private readonly OverlaySuspension? _suspension;
        private int _completed;

        internal ProfileSaveScope(SessionGateHold? gateHold, OverlaySuspension? suspension)
        {
            _gateHold = gateHold;
            _suspension = suspension;
        }

        internal bool SaveSkipped => ReferenceEquals(this, Skipped);

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            if (_suspension != null)
            {
                ResumeOverlay(_suspension);
            }

            _gateHold?.Dispose();
        }
    }

    internal sealed class OverlaySuspension
    {
        internal OverlaySuspension(ActiveVirtualStashState overlayState, List<Item> virtualItems)
        {
            OverlayState = overlayState;
            VirtualItems = virtualItems;
        }

        internal ActiveVirtualStashState OverlayState { get; }
        internal List<Item> VirtualItems { get; }
    }

    internal sealed class SessionGateHold : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private readonly GateOwnership _ownership;
        private int _disposed;

        internal SessionGateHold(SemaphoreSlim gate, GateOwnership ownership)
        {
            _gate = gate;
            _ownership = ownership;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _ownership.Released = true;
            _gate.Release();
        }
    }

    internal sealed class GateOwnership
    {
        internal GateOwnership(MongoId sessionId)
        {
            SessionId = sessionId;
        }

        internal MongoId SessionId { get; }
        internal volatile bool Released;
    }

    internal static void RekeyStash(MongoId sessionId, string oldStashKey, string newStashKey)
    {
        if (string.IsNullOrWhiteSpace(oldStashKey) || string.IsNullOrWhiteSpace(newStashKey))
        {
            VagabondLogger.Error(
                $"Stash migraiton failed, missing stash key(s): Session={sessionId}, oldStashKey={oldStashKey}, newStashKey={newStashKey}");
            return;
        }

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
        var oldData = profileDataService.GetProfileDataAsync<VirtualStashData>(sessionId, oldKey)
            .GetAwaiter().GetResult();
        if (oldData == null)
        {
            VagabondLogger.Success($"Stash migration successful.");
            return;
        }

        var newKey = GetProfileStashKey(newStashKey);
        var existing = profileDataService.GetProfileDataAsync<VirtualStashData>(sessionId, newKey)
            .GetAwaiter().GetResult();
        if (existing == null)
        {
            profileDataService.SaveProfileDataAsync(sessionId, newKey, new VirtualStashData
            {
                StashKey = newStashKey,
                Items = oldData.Items
            }).GetAwaiter().GetResult();
        }

        PersistedStashSignatures.TryRemove(GetSignatureCacheKey(sessionId, oldStashKey), out _);
        PersistedStashSignatures.TryRemove(GetSignatureCacheKey(sessionId, newStashKey), out _);

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

        if (VagabondService.IsInRaid(sessionId))
        {
            return false;
        }

        if (!VagabondService.ShouldApplyVagabondRules(sessionId))
        {
            return false;
        }

        var (found, foundStashId) = StateService.WithState(sessionId, state =>
        {
            if (!state.VagabondModeEnabled)
            {
                return (false, string.Empty);
            }

            if (HideoutService.GetTraderIds(state).Count > 0 && !string.IsNullOrWhiteSpace(state.LastExit))
            {
                return (true, state.LastExit);
            }

            if (!string.IsNullOrEmpty(state.HideoutState?.Id) &&
                state.LastExit != $"{HideoutService.HideoutIdPrefix}{state.HideoutState?.Id}")
            {
                if (state.LastExit.IndexOf(HideoutService.HideoutIdPrefix, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    if (VagabondConfig.Config.ShareHideoutExits)
                    {
                        return (false, string.Empty);
                    }

                    return (true, state.LastExit);
                }
            }

            if (!string.IsNullOrWhiteSpace(state.LastExit) &&
                state.LastExit.IndexOf(HideoutService.HideoutIdPrefix, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return (true, TempStashKey);
            }

            return (false, string.Empty);
        });

        stashId = foundStashId;
        return found;
    }

    private sealed class ActiveStashSession : IDisposable
    {
        private readonly MongoId _sessionId;
        private readonly SessionGateHold? _gateHold;
        private bool _disposed;

        public ActiveStashSession(MongoId sessionId, SessionGateHold? gateHold)
        {
            _sessionId = sessionId;
            _gateHold = gateHold;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                CloseStash(_sessionId);
            }
            finally
            {
                _gateHold?.Dispose();
            }
        }
    }
}