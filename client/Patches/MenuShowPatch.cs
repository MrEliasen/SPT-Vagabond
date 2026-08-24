using System;
using System.Reflection;
using EFT;
using EFT.UI;
using EFT.UI.Matchmaker;
using HarmonyLib;
using SPT.Reflection.Patching;
using Vagabond.Client.Services;
using Vagabond.Common.Data;

namespace Vagabond.Client.Patches;

internal class MenuShowPatch : ModulePatch
{
    private static bool _headlessUpdated;

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(
            typeof(MenuScreen),
            "Show",
            new[]
            {
                typeof(Profile),
                typeof(MatchmakerPlayersController),
                typeof(ESessionMode)
            });
    }

    [PatchPostfix]
    private static void Postfix()
    {
        ForcedSpawnService.Clear();

        if (Vagabond.IsHeadless())
        {
            if (!_headlessUpdated)
            {
                _headlessUpdated = true;
                _ = CommunicationService.RefreshVagabondState();
            }

            return;
        }

        if (!Vagabond.State.HasShownWarningMessage && Vagabond.State.NewCharacter)
        {
            var message = Messages.FirstWarning(Vagabond.State.WipeFirstRaid, Vagabond.State.ResetOnDeath);
            if (message != "")
            {
                UIMessageService.Instance.ShowMessage(message);
                Vagabond.State.HasShownWarningMessage = true;
            }
        }

        if (Vagabond.State.IsRefreshing || (DateTime.UtcNow - Vagabond.State.LastRefreshUtc).TotalSeconds < 30)
        {
            return;
        }

        Vagabond.State.IsRefreshing = true;
        _ = CommunicationService.RefreshVagabondState();
    }
}