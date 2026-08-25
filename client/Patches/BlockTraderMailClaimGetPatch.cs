using System.Reflection;
using ChatShared;
using EFT;
using EFT.Communications;
using EFT.UI.Chat;
using HarmonyLib;
using SPT.Reflection.Patching;
using SPT.Reflection.Utils;

namespace Vagabond.Client.Patches;

public class BlockTraderMailClaimGetPatch : ModulePatch
{
    private static readonly AccessTools.FieldRef<MessageView, DialogueChatMessage> _messageField =
        AccessTools.FieldRefAccess<MessageView, DialogueChatMessage>("DialogueChatMessage");

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(AttachmentMessageView), "CG_Awake");
    }

    [PatchPrefix]
    public static bool Prefix(AttachmentMessageView __instance)
    {
        if (!Vagabond.State.LimitTraderMailAccess)
        {
            return true;
        }

        var chatMessage = _messageField(__instance);
        if (chatMessage == null || chatMessage.Type != EMessageType.NpcTraderMessage)
        {
            return true;
        }

        var traderId = chatMessage.Member?.Id;
        if (string.IsNullOrEmpty(traderId))
        {
            return true;
        }

        // whitelist BTR driver
        if (traderId == "656f0f98d80a697f855d34b1")
        {
            return true;
        }

        if (!MongoIdGuard.IsMongoIdFormat(traderId))
        {
            return true;
        }

        var profile = ClientAppUtils.GetClientApp()?.Session?.Profile;
        if (profile?.TradersInfo == null)
        {
            return true;
        }

        if (!profile.TradersInfo.TryGetValue(traderId, out var info) || info == null || info.Available)
        {
            return true;
        }

        var name = info.Settings?.Nickname?.Localized() ?? "Trader";
        NotificationManager.DisplayWarningNotification($"{name} is not available at your current location.");
        return false;
    }
}