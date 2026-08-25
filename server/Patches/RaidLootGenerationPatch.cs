using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services.InRaid;
using Vagabond.Server.Services;

namespace Vagabond.Server.Patches;

public sealed class RaidLootGenerationPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.GenerateLocationAndLoot))!;
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, string name)
    {
        var ownerSessionId = FikaAdapter.GetRaidOwnerSessionId(sessionId);
        LootStreakService.CurrentMultiplier = LootStreakService.GetCurrentMultiplier(ownerSessionId, name);
    }

    [PatchPostfix]
    public static void Postfix() => LootStreakService.CurrentMultiplier = 0;
}