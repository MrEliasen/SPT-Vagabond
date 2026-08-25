using System;
using System.Collections.Generic;
using Vagabond.Common.Definitions;
using Vagabond.Common.Enums;

namespace Vagabond.Client.State;

public sealed class VagabondState
{
    public bool IsRefreshing { get; set; }
    public DateTime LastRefreshUtc { get; set; }
    public bool HasShownWarningMessage { get; set; }
    public bool ResetOnDeath { get; set; }
    public string CurrentMap { get; set; } = string.Empty;
    public bool WipeFirstRaid { get; set; }
    public bool VirtualStashes { get; set; }
    public bool NewCharacter { get; set; }
    public bool AllowPostRaidHealing { get; set; }
    public int CustomExfilsCacheVersion = 0;
    public Dictionary<string, List<string>> QuestExfils { get; set; } = new();
    public Dictionary<RaidLocation, Dictionary<string, List<CustomExfil>>> CustomExfils { get; set; } = new();
    public HashSet<string> RaidFirItems { get; set; } = new();
    public bool LimitTraderMailAccess { get; set; } = true;
    public bool LootStreakEnabled { get; set; }
    public double LootStreakMultiplier { get; set; } = 1.0;
    public int LootStreakCount { get; set; }
}