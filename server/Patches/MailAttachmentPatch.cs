using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Dialog;
using SPTarkov.Server.Core.Services.Commerce;
using Vagabond.Server.Config;
using Vagabond.Server.Services;

namespace Vagabond.Server.Patches;

public sealed class MailAttachmentsPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(MailSendService).GetMethod(nameof(MailSendService.SendMessageToPlayer))!;
    }

    [PatchPrefix]
    public static bool Prefix(SendMessageDetails messageDetails)
    {
        if (VagabondConfig.Config.MailAttachmentLimit == "" || VagabondConfig.Config.MailAttachmentLimit == "anywhere")
        {
            return true;
        }

        var sender = messageDetails.Sender;
        if (sender != MessageType.UserMessage && sender != MessageType.GroupChatMessage)
        {
            return true;
        }

        if (!ShouldAllowPlayerAttachments(messageDetails))
        {
            messageDetails.Items = [];
            messageDetails.ItemsMaxStorageLifetimeSeconds = null;
        }

        return true;
    }

    private static bool ShouldAllowPlayerAttachments(SendMessageDetails messageDetails)
    {
        var senderDetails = messageDetails.SenderDetails;
        if (senderDetails == null || senderDetails.Id.IsEmpty)
        {
            return true;
        }

        var (senderMap, senderExit) = StateService.WithState(senderDetails.Id,
            s => (s.CurrentMap, s.LastExit));
        var (recipientMap, recipientExit) = StateService.WithState(messageDetails.RecipientId,
            s => (s.CurrentMap, s.LastExit));

        if (string.IsNullOrEmpty(senderMap))
        {
            return false;
        }

        if (senderMap != recipientMap)
        {
            return false;
        }

        if (VagabondConfig.Config.MailAttachmentLimit == "same-map")
        {
            return true;
        }

        return !string.IsNullOrEmpty(senderExit) && senderExit == recipientExit;
    }
}