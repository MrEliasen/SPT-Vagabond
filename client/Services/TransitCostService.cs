using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;

namespace Vagabond.Client.Services;

internal static class TransitCostService
{
    private static ItemController _controller;
    private static Stash _fakeStash;

    private static void EnsureSink()
    {
        if (_controller != null)
        {
            return;
        }

        _fakeStash = Singleton<ItemFactory>.Instance.CreateFakeStash(MongoID.Generate());
        _controller = new ItemController(
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

        var loc = ((Stash)_controller.RootItem).Grid.FindLocationForItem(stack);
        if (loc == null)
        {
            error = "Fake stash is full";
            return false;
        }

        var ic = player.InventoryController;
        if (stack.StackObjectsCount == price)
        {
            ic.TryRunNetworkTransaction(
                ItemManipulator.Move(stack, loc, ic, true));
        }
        else
        {
            ic.TryRunNetworkTransaction(
                ItemManipulator.SplitExact(stack, price, loc, ic, ic, true));
        }

        return true;
    }
}