using Vagabond.Common.Definitions;
using Vagabond.Common.Enums;

namespace Vagabond.Common.Models;

public class SyncStateResponse
{
    public bool ResetOnDeath { get; set; }
    public bool WipeFirstRaid { get; set; }
    public bool VirtualStashes { get; set; }
    public string CurrentMap { get; set; } = "";
    public bool NewCharacter { get; set; }
    public bool AllowPostRaidHealing { get; set; }
    public Dictionary<string, List<string>> QuestExfils { get; set; } = new();
    public Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>>? CustomExfils { get; set; }
    public HashSet<string> RaidFirItems { get; set; } = new();
    public bool LimitTraderMailAccess { get; set; }
    public bool HideoutAccessible { get; set; }
    public bool LimitHideoutAccess { get; set; }
    public bool LootStreakEnabled { get; set; }
    public double LootStreakMultiplier { get; set; } = 1.0;
    public int LootStreakCount { get; set; }
}