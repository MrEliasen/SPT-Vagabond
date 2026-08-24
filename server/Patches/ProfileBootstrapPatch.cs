using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Vagabond.Server.Services;
using Vagabond.Common.Definitions;

namespace Vagabond.Server.Patches;

public sealed class ProfileBootstrapPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProfileHelper).GetMethod(nameof(ProfileHelper.GetPmcProfile))!;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId, ref PmcData __result)
    {
        BootstrapProfile(sessionId, __result);
    }

    public static void BootstrapProfile(MongoId sessionId, PmcData pmc)
    {
        try
        {
            if (!VagabondService.ShouldApplyVagabondRules(sessionId))
            {
                return;
            }

            var state = StateService.GetState(sessionId);
            if (!state.VagabondModeEnabled)
            {
                return;
            }

            MigrationService.MigrateProfile(sessionId, pmc, state);

            if (state.ResetProfile)
            {
                state.ResetProfile = false;
                StateService.SaveState(sessionId, state);
                VirtualStashService.ClearAllTraderStashes(sessionId);
                VagabondService.ResetProfile(sessionId, pmc);
                VagabondService.PersistProfileIfPossible(sessionId);
                return;
            }

            HideoutService.UpdateTraderAccess(pmc, state);
            ApplyRaidFirItems(pmc, state);
            VagabondService.PersistProfileIfPossible(sessionId);
        }
        catch (Exception ex)
        {
            VagabondLogger.Error($"Profile updating failed: {ex}");
        }
    }

    private static void ApplyRaidFirItems(PmcData pmc, VagabondSessionState state)
    {
        if (state.RaidFirItems is not { Count: > 0 })
        {
            return;
        }

        var items = pmc.Inventory?.Items;
        if (items == null)
        {
            return;
        }

        var firIds = new HashSet<string>(state.RaidFirItems);
        foreach (var item in items)
        {
            if (!firIds.Contains(item.Id))
            {
                continue;
            }

            item.Upd ??= new Upd();
            item.Upd.SpawnedInSession = true;
        }
    }
}