using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Achievements;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using EFT.Quests;
using EFT.Trading;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Vagabond.Client.Patches;

public class HideUnavailableTraderCardsPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Constructor(
            typeof(TraderScreensGroup.TraderScreenController),
            new[]
            {
                typeof(Trader),
                typeof(IEnumerable<Trader>),
                typeof(Profile),
                typeof(InventoryController),
                typeof(IHealthController),
                typeof(QuestController),
                typeof(AchievementsController),
                typeof(IEftSession)
            });
    }

    [PatchPrefix]
    public static void Prefix(ref Trader trader, ref IEnumerable<Trader> tradersList)
    {
        if (tradersList == null)
        {
            return;
        }

        var filtered = tradersList.Where(x => x != null && x.Info != null && x.Info.Available).ToArray();

        if (filtered.Length == 0)
        {
            return;
        }

        tradersList = filtered;

        if (trader == null || trader.Info == null || !trader.Info.Available || !filtered.Contains(trader))
        {
            trader = filtered[0];
        }
    }
}