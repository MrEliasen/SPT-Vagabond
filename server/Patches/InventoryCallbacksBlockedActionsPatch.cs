using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using Vagabond.Server.Services;

namespace Vagabond.Server.Patches;

public sealed class InventoryCallbacksTagItemPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(InventoryCallbacks).GetMethod(nameof(InventoryCallbacks.TagItem))!;
    }

    [PatchPrefix]
    public static bool Prefix(MongoId sessionId, ref ValueTask<ItemEventRouterResponse> __result)
    {
        if (!VirtualStashService.IsVirtualStashEnabled(sessionId))
        {
            return true;
        }

        __result = new ValueTask<ItemEventRouterResponse>(
            VirtualStashService.CreateBlockedActionResponse(sessionId));
        return false;
    }
}

public sealed class InventoryCallbacksSortInventoryPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(InventoryCallbacks).GetMethod(nameof(InventoryCallbacks.SortInventory))!;
    }

    [PatchPrefix]
    public static bool Prefix(MongoId sessionID, ItemEventRouterResponse output,
        ref ValueTask<ItemEventRouterResponse> __result)
    {
        if (!VirtualStashService.IsVirtualStashEnabled(sessionID))
        {
            return true;
        }

        var response = output;
        VirtualStashService.AppendBlockedActionWarning(response);
        __result = new ValueTask<ItemEventRouterResponse>(response);
        return false;
    }
}

public sealed class InventoryCallbacksPinOrLockPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(InventoryCallbacks).GetMethod(nameof(InventoryCallbacks.PinOrLock))!;
    }

    [PatchPrefix]
    public static bool Prefix(MongoId sessionID, ItemEventRouterResponse output,
        ref ValueTask<ItemEventRouterResponse> __result)
    {
        if (!VirtualStashService.IsVirtualStashEnabled(sessionID))
        {
            return true;
        }

        var response = output;
        VirtualStashService.AppendBlockedActionWarning(response);
        __result = new ValueTask<ItemEventRouterResponse>(response);
        return false;
    }
}

public sealed class InventoryCallbacksSetFavoriteItemPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(InventoryCallbacks).GetMethod(nameof(InventoryCallbacks.SetFavoriteItem))!;
    }

    [PatchPrefix]
    public static bool Prefix(MongoId sessionID, ItemEventRouterResponse output,
        ref ValueTask<ItemEventRouterResponse> __result)
    {
        if (!VirtualStashService.IsVirtualStashEnabled(sessionID))
        {
            return true;
        }

        var response = output;
        VirtualStashService.AppendBlockedActionWarning(response);
        __result = new ValueTask<ItemEventRouterResponse>(response);
        return false;
    }
}