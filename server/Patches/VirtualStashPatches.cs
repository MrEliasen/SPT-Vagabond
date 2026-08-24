using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Services.Commerce;
using Vagabond.Server.Services;

namespace Vagabond.Server.Patches;

public sealed class ItemEventRouterHandleEventsPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ItemEventCallbacks).GetMethod(nameof(ItemEventCallbacks.HandleEvents))!;
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionID, out IDisposable __state)
    {
        __state = VirtualStashService.OpenStash(sessionID);
    }

    [PatchPostfix]
    public static void Postfix(ref ValueTask<string> __result, IDisposable __state)
    {
        __result = AttachCleanup(__result, __state);
    }

    private static async ValueTask<string> AttachCleanup(
        ValueTask<string> originalResult,
        IDisposable scope)
    {
        try
        {
            return await originalResult.ConfigureAwait(false);
        }
        finally
        {
            scope.Dispose();
        }
    }
}

public sealed class TradeHelperBuyItemPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(TradeHelper).GetMethod(nameof(TradeHelper.BuyItem))!;
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, PmcData pmcData, out IDisposable __state)
    {
        __state = VirtualStashService.OpenStash(sessionId, pmcData);
    }

    [PatchFinalizer]
    public static Exception? Finalizer(Exception? __exception, IDisposable __state)
    {
        __state.Dispose();
        return __exception;
    }
}

public sealed class TradeHelperSellItemPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(TradeHelper).GetMethod(nameof(TradeHelper.SellItem))!;
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, PmcData profileWithItemsToSell, out IDisposable __state)
    {
        __state = VirtualStashService.OpenStash(sessionId, profileWithItemsToSell);
    }

    [PatchFinalizer]
    public static Exception? Finalizer(Exception? __exception, IDisposable __state)
    {
        __state.Dispose();
        return __exception;
    }
}

public sealed class PaymentServicePayMoneyPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(PaymentService).GetMethod(nameof(PaymentService.PayMoney))!;
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionID, PmcData pmcData, out IDisposable __state)
    {
        __state = VirtualStashService.OpenStash(sessionID, pmcData);
    }

    [PatchFinalizer]
    public static Exception? Finalizer(Exception? __exception, IDisposable __state)
    {
        __state.Dispose();
        return __exception;
    }
}

public sealed class InventoryHelperAddItemsToStashPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(InventoryHelper).GetMethod(nameof(InventoryHelper.AddItemsToStash))!;
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, PmcData pmcData, out IDisposable __state)
    {
        __state = VirtualStashService.OpenStash(sessionId, pmcData);
    }

    [PatchFinalizer]
    public static Exception? Finalizer(Exception? __exception, IDisposable __state)
    {
        __state.Dispose();
        return __exception;
    }
}

public sealed class InventoryHelperAddItemToStashPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(InventoryHelper).GetMethod(nameof(InventoryHelper.AddItemToStash))!;
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, PmcData pmcData, out IDisposable __state)
    {
        __state = VirtualStashService.OpenStash(sessionId, pmcData);
    }

    [PatchFinalizer]
    public static Exception? Finalizer(Exception? __exception, IDisposable __state)
    {
        __state.Dispose();
        return __exception;
    }
}

public sealed class InventoryHelperCanPlaceItemsInInventoryPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(InventoryHelper).GetMethod(nameof(InventoryHelper.CanPlaceItemsInInventory))!;
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, out IDisposable __state)
    {
        __state = VirtualStashService.OpenStash(sessionId);
    }

    [PatchFinalizer]
    public static Exception? Finalizer(Exception? __exception, IDisposable __state)
    {
        __state.Dispose();
        return __exception;
    }
}