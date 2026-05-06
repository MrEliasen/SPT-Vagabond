using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;

namespace Vagabond.Client.Services;

internal static class TransitCostService
{
    private static TraderControllerClass _controller;
    private static StashItemClass _fakeStash;

    private static void EnsureSink()
    {
        if (_controller != null)
        {
            return;
        }

        _fakeStash = Singleton<ItemFactoryClass>.Instance.CreateFakeStash(MongoID.Generate());
        _controller = new TraderControllerClass(
            _fakeStash,
            "VagabondTransitCostSink",
            "VagabondTransitCostSink",
            true,
            EOwnerType.ExfilPoint);

        if (Singleton<GameWorld>.Instantiated)
        {
            Singleton<GameWorld>.Instance.ItemOwners.Add(_controller, default);
        }
    }

    public static void Cleanup()
    {
        if (_controller != null && Singleton<GameWorld>.Instantiated)
        {
            Singleton<GameWorld>.Instance.ItemOwners.Remove(_controller);
        }

        _controller = null;
        _fakeStash = null;
    }

    public static bool TryDeductCost(Player player, string currencyId, int price, out string error)
    {
        error = string.Empty;

        if (player == null)
        {
            error = "No player";
            return false;
        }

        if (price <= 0)
        {
            return true;
        }

        EnsureSink();

        var stack = player.Profile.Inventory.GetAllItemByTemplate(currencyId)
            .FirstOrDefault(it => it.StackObjectsCount >= price);
        if (stack == null)
        {
            error = "Insufficient money (must be in a single stack)";
            return false;
        }

        var loc = ((StashItemClass)_controller.RootItem).Grid.FindLocationForItem(stack);
        if (loc == null)
        {
            error = "Fake stash is full";
            return false;
        }

        var ic = player.InventoryController;
        if (stack.StackObjectsCount == price)
        {
            ic.TryRunNetworkTransaction(
                InteractionsHandlerClass.Move(stack, loc, ic, true));
        }
        else
        {
            ic.TryRunNetworkTransaction(
                InteractionsHandlerClass.SplitExact(stack, price, loc, ic, ic, true));
        }

        return true;
    }
}