using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vagabond.Common.Definitions;
using Vagabond.Common.Enums;
using Vagabond.Common.Models;

namespace Vagabond.Client.Services;

public static class CommunicationService
{
    public static void RefreshExfilStateBlocking()
    {
        try
        {
            var resp = Networking.ApiClient.SyncExfilDataBlocking(new GetExfilDataRequest
            {
                Version = Vagabond.State.CustomExfilsCacheVersion,
            });

            ApplyExfilState(resp, applyToRaid: false);
        }
        catch (Exception ex)
        {
            Vagabond.LogError($"Failed to synchronously sync Vagabond state: {ex}");
        }
    }

    public static async Task RefreshExfilState()
    {
        try
        {
            var resp = await Networking.ApiClient.SyncExfilData(new GetExfilDataRequest
            {
                Version = Vagabond.State.CustomExfilsCacheVersion,
            });

            ApplyExfilState(resp, applyToRaid: true);
        }
        catch (Exception ex)
        {
            Vagabond.LogError($"Failed to sync Vagabond state: {ex}");
        }
        finally
        {
            Vagabond.State.IsRefreshing = false;
        }
    }

    private static void ApplyExfilState(SyncExfilResponse resp, bool applyToRaid)
    {
        if (resp == null)
        {
            return;
        }

        //Vagabond.Log($"RefreshExfilState: version={resp.Version}");
        Vagabond.State.CustomExfilsCacheVersion = resp.Version;

        if (resp.CustomExfils != null)
        {
            //Vagabond.Log($"RefreshExfilState: updating exfils");
            Vagabond.State.CustomExfils = WithMapKeysCaseInsensitive(resp.CustomExfils);

            if (applyToRaid)
            {
                RaidService.UpdateCurrentRaidExfils();
            }
        }
    }

    public static async Task RefreshVagabondState()
    {
        try
        {
            var request = BuildSyncStateRequest();
            var resp = await Networking.ApiClient.SyncVagabondState(request);
            _lastSentInRaid = request.InRaid;
            ApplyVagabondState(resp);
        }
        catch (Exception ex)
        {
            Vagabond.LogError($"Failed to sync Vagabond state: {ex}");
        }
        finally
        {
            Vagabond.State.IsRefreshing = false;
        }
    }

    public static void RefreshVagabondStateBlocking()
    {
        try
        {
            var request = BuildSyncStateRequest();
            var resp = Networking.ApiClient.SyncVagabondStateBlocking(request);
            _lastSentInRaid = request.InRaid;
            ApplyVagabondState(resp);
        }
        catch (Exception ex)
        {
            Vagabond.LogError($"Failed to synchronously sync Vagabond state: {ex}");
        }
    }

    private static void ApplyVagabondState(SyncStateResponse resp)
    {
        if (resp == null)
        {
            return;
        }

        // foreach (var raid in Vagabond.State.CustomExfils)
        // {
        //     Vagabond.Log($" === {raid.Key} ===");
        //     foreach (var map in raid.Value)
        //     {
        //         foreach (var exfil in map.Value)
        //         {
        //             var kind = exfil.IsTransit ? "Transit" : "Extract";
        //             var desc = exfil.IsTransit
        //                 ? $"{map.Key} To {exfil.DestinationLocation}"
        //                 : $" {exfil.DisplayName}";
        //             Vagabond.Log($" => [{kind}] {exfil.Identifier} :: {desc}");
        //         }
        //     }
        // }

        if (resp.CustomExfils != null)
        {
            Vagabond.State.CustomExfils = WithMapKeysCaseInsensitive(resp.CustomExfils);
        }

        if (resp.QuestExfils != null)
        {
            Vagabond.State.QuestExfils =
                new Dictionary<string, List<string>>(resp.QuestExfils, StringComparer.OrdinalIgnoreCase);
        }

        if (resp.RaidFirItems != null)
        {
            Vagabond.State.RaidFirItems = resp.RaidFirItems;
        }

        if (!Vagabond.IsHeadless())
        {
            Vagabond.State.ResetOnDeath = resp.ResetOnDeath;
            Vagabond.State.WipeFirstRaid = resp.WipeFirstRaid;
            Vagabond.State.VirtualStashes = resp.VirtualStashes;
            Vagabond.State.CurrentMap = resp.CurrentMap;
            Vagabond.State.LastRefreshUtc = DateTime.UtcNow;
            Vagabond.State.NewCharacter = resp.NewCharacter;
            Vagabond.State.AllowPostRaidHealing = resp.AllowPostRaidHealing;
            Vagabond.State.LimitTraderMailAccess = resp.LimitTraderMailAccess;
            Vagabond.State.LootStreakEnabled = resp.LootStreakEnabled;
            Vagabond.State.LootStreakMultiplier = resp.LootStreakMultiplier;
            Vagabond.State.LootStreakCount = resp.LootStreakCount;
        }
    }

    private static bool? _lastSentInRaid;

    public static bool HasUnsentInRaidStatus()
    {
        return _lastSentInRaid != RaidService.IsInRaid();
    }

    private static SyncStateRequest BuildSyncStateRequest()
    {
        return new SyncStateRequest
        {
            InRaid = RaidService.IsInRaid(),
        };
    }

    private static Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>> WithMapKeysCaseInsensitive(
        Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>> source)
    {
        var result = new Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>>(source.Count);
        foreach (var raid in source)
        {
            result[raid.Key] = raid.Value == null
                ? new Dictionary<string, List<CustomExfil>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<CustomExfil>>(raid.Value, StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }
}