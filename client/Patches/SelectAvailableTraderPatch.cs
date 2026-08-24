using System.Collections;
using System.Linq;
using System.Reflection;
using EFT.Communications;
using EFT.UI;
using EFT.UI.Screens;
using HarmonyLib;
using SPT.Reflection.Patching;
using Vagabond.Client.Services;

namespace Vagabond.Client.Patches;

public class SelectAvailableTraderPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(TraderScreensGroup), nameof(TraderScreensGroup.Show));
    }

    [PatchPostfix]
    public static void Postfix(TraderScreensGroup __instance)
    {
        var available = __instance.TradersList?
            .Where(x => x != null && x.Info != null && x.Info.Available)
            .ToList();

        if (available == null || available.Count == 0)
        {
            NotificationManager.DisplayWarningNotification("No traders available at this location.");

            if (UIMessageService.Instance != null)
            {
                UIMessageService.Instance.StartCoroutine(CloseNextFrame());
            }

            return;
        }

        if (!(__instance.Trader != null && available.Any(x => x.Id == __instance.Trader.Id)))
        {
            __instance.SelectTrader(available[0]);
        }
    }

    private static IEnumerator CloseNextFrame()
    {
        yield return null;
        _ = EftScreenManager.Instance.TryReturnToRootScreen();
    }
}