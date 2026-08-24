using System.Reflection;
using ChatShared;
using EFT;
using EFT.Communications;
using EFT.UI.Chat;
using HarmonyLib;
using SPT.Reflection.Patching;
using SPT.Reflection.Utils;

namespace Vagabond.Client.Patches;

public class BlockTraderMailClaimAllPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ChatScreen), nameof(ChatScreen.TransferAll),
            new[] { typeof(UpdatableChatDialogue) });
    }

    [PatchPrefix]
    public static bool Prefix(UpdatableChatDialogue dialog)
    {
        if (!Vagabond.State.LimitTraderMailAccess)
        {
            return true;
        }

        if (dialog == null || dialog.Type != EMessageType.NpcTraderMessage)
        {
            return true;
        }

        // whitelist BTR driver
        if (dialog._id == "656f0f98d80a697f855d34b1")
        {
            return true;
        }

        var profile = ClientAppUtils.GetClientApp()?.Session?.Profile;
        if (profile?.TradersInfo == null)
        {
            return true;
        }

        if (!profile.TradersInfo.TryGetValue(dialog._id, out var info) || info == null || info.Available)
        {
            return true;
        }

        var name = info.Settings?.Nickname?.Localized() ?? "Trader";
        NotificationManager.DisplayWarningNotification($"{name} is not available at your current location.");
        return false;
    }
}