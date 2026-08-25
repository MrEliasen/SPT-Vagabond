using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Services.Profile;
using Vagabond.Common.Data;
using Vagabond.Server.Config;
using Vagabond.Server.Services;
using Vagabond.Server.State;

namespace Vagabond.Server.Patches;

public sealed class ProfileCreatePatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(CreateProfileService).GetMethod(nameof(CreateProfileService.CreateProfile))!;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId, ref ValueTask<string> __result)
    {
        __result = RunAfterCreate(__result, sessionId);
    }

    private static async ValueTask<string> RunAfterCreate(ValueTask<string> original, MongoId sessionId)
    {
        string profileId = await original.ConfigureAwait(false);
        CreateProfile(sessionId);
        return profileId;
    }

    public static void CreateProfile(MongoId sessionId)
    {
        if (!VagabondService.ShouldApplyVagabondRules(sessionId))
        {
            return;
        }

        var pmc = VagabondService.GetPmcProfile(sessionId);
        if (pmc?.CharacterData?.PmcData == null)
        {
            VagabondLogger.Error($"BootstrapProfile did not modify profile for {sessionId}; PMC null {sessionId}");
            return;
        }

        StateService.WithState(sessionId, state =>
        {
            var (startRaid, startExfil) = VagabondConfig.GetStartLocation();
            state.CurrentMap = startRaid;
            state.LastExit = startExfil;
            state.VagabondModeEnabled = true;
            state.IsNewCharacter = true;
            StateService.SaveState(sessionId, state);
        });

        var changed = InitializeNewCharacter(sessionId, pmc);
        if (!changed)
        {
            StateService.WithState(sessionId, state =>
            {
                state.CurrentMap = "";
                state.LastExit = "";
                state.VagabondModeEnabled = false;
                StateService.SaveState(sessionId, state);
            });
            VagabondLogger.Error(
                $"BootstrapProfile did not modify profile for {sessionId}; InitializeNewCharacter did not complete.");
            return;
        }

        var pmcData = pmc.CharacterData!.PmcData!;
        StateService.WithState(sessionId,
            state => HideoutService.UpdateTraderAccess(pmcData, state));
        VagabondService.PersistProfileIfPossible(sessionId);
        MailerService.SendMail(sessionId, Messages.WelcomeOpenWorld());
        VagabondLogger.Success($"activated Vagabond profile for {sessionId}.");
    }

    private static bool InitializeNewCharacter(MongoId sessionId, SptProfile pmc)
    {
        if (pmc.CharacterData?.PmcData == null)
        {
            VagabondLogger.Error("InitializeNewCharacter: PmcData was null.");
            return false;
        }

        var inventory = pmc.CharacterData.PmcData.Inventory;
        if (inventory == null)
        {
            VagabondLogger.Error("InitializeNewCharacter: inventory was null.");
            return false;
        }

        var items = inventory.Items;
        if (items == null)
        {
            VagabondLogger.Error("InitializeNewCharacter: inventory items list was null.");
            return false;
        }

        RaidRuntimeState.Left(sessionId);
        VagabondService.WipeItems(sessionId, pmc.CharacterData.PmcData, true, true);
        VirtualStashService.ClearAllTraderStashes(sessionId);
        using var stashState = VirtualStashService.OpenStash(sessionId, pmc.CharacterData.PmcData);
        VagabondService.AddMoney(sessionId, pmc.CharacterData.PmcData);
        return true;
    }
}