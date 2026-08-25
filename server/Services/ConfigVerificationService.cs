using SPTarkov.Server.Core.Models.Spt.Tables;
using Vagabond.Common.Data;
using Vagabond.Common.Enums;
using Vagabond.Server.Config;

namespace Vagabond.Server.Services;

internal static class ConfigVerificationService
{
    private static readonly Lock ProblemsLock = new();
    private static readonly List<string> Problems = new();

    private static int _skippedRows;
    private static int _droppedItems;

    public static void ReportSkippedRow(string file, int index, string reason)
    {
        Record($"{file} row {index}: {reason}", skippedRow: true);
    }

    public static void ReportDroppedItem(string file, string subject, string reason)
    {
        Record($"{file} ({subject}): {reason}", skippedRow: false);
    }

    private static void Record(string message, bool skippedRow)
    {
        lock (ProblemsLock)
        {
            Problems.Add(message);
            if (skippedRow)
            {
                _skippedRows++;
            }
            else
            {
                _droppedItems++;
            }
        }

        VagabondLogger.Error($"CONFIG: {message}");
    }

    public static void VerifyAgainstDatabase(TradersTable tradersTable)
    {
        VerifyTraderLocations(tradersTable);
        VerifyStartLocation();
    }

    private static void VerifyTraderLocations(TradersTable tradersTable)
    {
        var kept = new List<Common.Definitions.TraderLocation>(HideoutService.TraderLocations.Count);

        foreach (var row in HideoutService.TraderLocations)
        {
            var subject = $"trader {row.TraderId} / {row.ExfilIdentifier}";

            if (row.Raid == RaidLocation.Nil)
            {
                ReportDroppedItem("trader_locations.json", subject, "raid is not a supported raid name.");
                continue;
            }

            if (!tradersTable.ContainsKey(row.TraderId))
            {
                ReportDroppedItem("trader_locations.json", subject,
                    $"traderId '{row.TraderId}' does not exist in the trader database.");
                continue;
            }

            if (!ExfilsConfig.Maps.TryGetValue(row.Raid, out var entry) ||
                !entry.Extracts.Exists(x =>
                    string.Equals(x.Identifier, row.ExfilIdentifier, StringComparison.OrdinalIgnoreCase)))
            {
                ReportDroppedItem("trader_locations.json", subject,
                    $"exfilIdentifier '{row.ExfilIdentifier}' is not defined in the {row.Raid} exfil config.");
                continue;
            }

            kept.Add(row);
        }

        if (kept.Count != HideoutService.TraderLocations.Count)
        {
            HideoutService.LoadTraderLocations(kept);
        }
    }

    private static void VerifyStartLocation()
    {
        var (raid, exfil) = VagabondConfig.GetStartLocation();
        if (string.Equals(raid, VagabondConfig.Config.StartRaid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(exfil, VagabondConfig.Config.StartExfilIdentifier, StringComparison.Ordinal))
        {
            return;
        }

        ReportDroppedItem("vagabond.json", "StartRaid / StartExfilIdentifier",
            $"'{VagabondConfig.Config.StartRaid}' / '{VagabondConfig.Config.StartExfilIdentifier}' is not a " +
            $"valid raid + exfil pair; new profiles will start at {raid} / {exfil}.");
    }

    public static void LogSummary()
    {
        int skipped;
        int dropped;
        lock (ProblemsLock)
        {
            skipped = _skippedRows;
            dropped = _droppedItems;
        }

        if (skipped == 0 && dropped == 0)
        {
            return;
        }

        VagabondLogger.Critical(
            $"CONFIG PROBLEMS: {skipped} config row(s) skipped, {dropped} entr(y/ies) dropped. " +
            "Vagabond is running with the remaining valid entries. See the CONFIG lines above for the " +
            "file, row and value at fault.");
    }
}