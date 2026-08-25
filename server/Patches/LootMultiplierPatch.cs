using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Generators.Loot;
using Vagabond.Server.Services;

namespace Vagabond.Server.Patches;

public sealed class LooseLootMultiplierPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationLootGenerator).GetMethod(
            "GetLooseLootMultiplierForLocation",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

    [PatchPostfix]
    public static void Postfix(ref double __result)
    {
        __result *= LootStreakService.CurrentMultiplier;
    }
}

public sealed class StaticLootMultiplierPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationLootGenerator).GetMethod(
            "GetStaticLootMultiplierForLocation",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

    [PatchPostfix]
    public static void Postfix(ref double __result)
    {
        __result *= LootStreakService.CurrentMultiplier;
    }
}