using System;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using Vagabond.Client.Services;
using Vagabond.Common.Data;
using Vagabond.Common.Definitions;

namespace Vagabond.Client.Patches;

internal class TransitInteractionPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(TransitInteractionControllerAbstractClass),
            nameof(TransitInteractionControllerAbstractClass.method_14));
    }

    [PatchPrefix]
    private static bool Prefix(int pointId, Player player)
    {
        if (!CustomExfilPlacementPatch.CustomTransitDefinitions.TryGetValue(pointId, out var definition))
        {
            return true;
        }

        if (MeetsTransitRequirements(player, definition, out var failReason))
        {
            return true;
        }

        NotificationManagerClass.DisplayWarningNotification(string.IsNullOrWhiteSpace(failReason)
            ? "Requirements not met"
            : failReason);
        return false;
    }

    private static bool MeetsTransitRequirements(Player player, CustomExfil definition, out string failReason)
    {
        failReason = string.Empty;

        if (definition.Requirements.Count == 0)
        {
            return true;
        }

        var c = 0;
        foreach (var req in definition.Requirements)
        {
            switch (req.Type)
            {
                case CustomExfilRequirementType.HasItem:
                {
                    var items = player.InventoryController.Inventory.GetPlayerItems(EPlayerItems.Equipment |
                        EPlayerItems.Stash);
                    var count = items.ToList()?.Count(x => x.TemplateId == req.Id);
                    if (count < req.Count)
                    {
                        c++;
                        if (failReason == string.Empty)
                        {
                            failReason = "Missing required item:";
                        }

                        if (!string.IsNullOrWhiteSpace(req.RequirementTip))
                        {
                            failReason += $"{(c > 0 ? " & " : " ")}{req.RequirementTip}";
                        }
                    }

                    break;
                }

                case CustomExfilRequirementType.EmptySlot:
                {
                    if (!Enum.TryParse<EquipmentSlot>(req.RequiredSlot, true, out var slot))
                    {
                        failReason = $"Invalid '{req.RequiredSlot}' slot";
                        return false;
                    }

                    var item = player.Equipment.GetSlot(slot).ContainedItem;
                    if (item != null)
                    {
                        failReason = string.IsNullOrWhiteSpace(req.RequirementTip)
                            ? $"{req.RequiredSlot} slot must be empty"
                            : req.RequirementTip;
                        return false;
                    }

                    break;
                }

                case CustomExfilRequirementType.Cost:
                {
                    var currencyId = string.IsNullOrWhiteSpace(req.Id) ? Currencies.Ruble : req.Id;
                    var price = req.Count;
                    if (req.ApplyDiscount)
                    {
                        player.HasMarkOfUnknown(out var moU);
                        price = Mathf.Max(1, player.Profile.GetExfiltrationPrice(req.Count, moU));
                    }

                    var qualifyingStack = player.Profile.Inventory.GetAllItemByTemplate(currencyId)
                        .FirstOrDefault(it => it.StackObjectsCount >= price);
                    if (qualifyingStack == null)
                    {
                        c++;
                        if (failReason == string.Empty)
                        {
                            failReason = "Insufficient funds:";
                        }

                        var tip = string.IsNullOrWhiteSpace(req.RequirementTip)
                            ? $"need a single stack of {price} {CurrencyShortName(currencyId)}"
                            : $"{req.RequirementTip} ({price})";
                        failReason += $"{(c > 0 ? " & " : " ")}{tip}";
                    }

                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(failReason))
        {
            return false;
        }

        return true;
    }

    internal static string CurrencyShortName(string id)
    {
        if (id == Currencies.Ruble) return "₽";
        if (id == Currencies.Dollar) return "$";
        if (id == Currencies.Euro) return "€";
        return id;
    }
}

// fires after the 2s "plant" succeeds and before the equipment screen.
internal class TransitCommitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.FirstMethod(
            typeof(TransitInteractionControllerAbstractClass.Class1179),
            m => m.Name == "method_0");
    }

    [PatchPostfix]
    private static void Postfix(TransitInteractionControllerAbstractClass.Class1179 __instance, bool successful)
    {
        if (!successful)
        {
            return;
        }

        if (!CustomExfilPlacementPatch.CustomTransitDefinitions.TryGetValue(__instance.pointId, out var def))
        {
            return;
        }

        foreach (var req in def.Requirements)
        {
            if (req.Type != CustomExfilRequirementType.Cost)
            {
                continue;
            }

            var id = string.IsNullOrWhiteSpace(req.Id) ? Currencies.Ruble : req.Id;
            var price = req.Count;
            if (req.ApplyDiscount)
            {
                __instance.player.HasMarkOfUnknown(out var moU);
                price = Mathf.Max(1, __instance.player.Profile.GetExfiltrationPrice(req.Count, moU));
            }

            if (!TransitCostService.TryDeductCost(__instance.player, id, price, out var err))
            {
                NotificationManagerClass.DisplayWarningNotification($"Transit aborted: {err}");
                __instance.TransitInteractionControllerAbstractClass.method_18(__instance.player);
                return;
            }

            NotificationManagerClass.DisplayMessageNotification(
                $"Paid {price} {TransitInteractionPatch.CurrencyShortName(id)}");
        }
    }
}