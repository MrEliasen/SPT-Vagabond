using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services.Commerce;

namespace Vagabond.Server.Services;

public class MailerService
{
    public static void SendMail(MongoId sessionId, string body)
    {
        var mail = ReflectionUtil.GetService<MailSendService>();
        if (mail == null)
        {
            VagabondLogger.Error("MailSendService not found");
            return;
        }

        mail.SendSystemMessageToPlayer(sessionId, body, null);
    }
}