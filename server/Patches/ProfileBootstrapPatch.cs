using System.Reflection;
using System.Runtime.CompilerServices;
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
    private static readonly ConditionalWeakTable<PmcData, AppliedMemo> AppliedMemos = new();

    private sealed class AppliedMemo
    {
        public long StateVersion = -1;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProfileHelper).GetMethod(nameof(ProfileHelper.GetPmcProfile))!;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId, ref PmcData? __result)
    {
        BootstrapProfile(sessionId, __result);
    }

    public static void BootstrapProfile(MongoId sessionId, PmcData? pmc)
    {
        try
        {
            if (pmc == null)
            {
                return;
            }

            if (AppliedMemos.TryGetValue(pmc, out var applied)
                && Volatile.Read(ref applied.StateVersion) == StateService.GetStateVersion(sessionId))
            {
                return;
            }

            if (!VagabondService.ShouldApplyVagabondRules(sessionId))
            {
                return;
            }

            var entryVersion = StateService.GetStateVersion(sessionId);

            var resetPending = false;
            var enabled = StateService.WithState(sessionId, state =>
            {
                if (!state.VagabondModeEnabled)
                {
                    return false;
                }

                MigrationService.MigrateProfile(sessionId, pmc, state);

                if (state.ResetProfile && !VirtualStashService.CurrentFlowOwnsForeignGate(sessionId))
                {
                    resetPending = true;
                    return true;
                }

                HideoutService.UpdateTraderAccess(pmc, state);
                ApplyRaidFirItems(pmc, state);

                if (!state.ResetProfile)
                {
                    Volatile.Write(ref AppliedMemos.GetOrCreateValue(pmc).StateVersion, entryVersion);
                }

                return true;
            });

            if (!enabled || !resetPending)
            {
                return;
            }

            var gate = VirtualStashService.TryAcquireGateScope(sessionId);
            if (gate == null)
            {
                if (VirtualStashService.ShouldLogResetDeferral(sessionId))
                {
                    VagabondLogger.Warning(
                        $"Deferring profile reset for {sessionId}: session gate still held; will retry.");
                }

                return;
            }

            try
            {
                var resetClaimed = StateService.WithState(sessionId, state =>
                {
                    if (!state.ResetProfile)
                    {
                        return false;
                    }

                    state.ResetProfile = false;
                    StateService.SaveState(sessionId, state);
                    return true;
                });

                if (!resetClaimed)
                {
                    return;
                }

                VirtualStashService.ClearAllTraderStashes(sessionId);
                VagabondService.ResetProfile(sessionId, pmc);
            }
            finally
            {
                gate.Dispose();
            }

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