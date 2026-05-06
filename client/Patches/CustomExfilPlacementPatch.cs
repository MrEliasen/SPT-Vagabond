using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.Interactive;
using EFT.Interactive.SecretExfiltrations;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using Vagabond.Client.Services;
using Vagabond.Common.Data;
using Vagabond.Common.Definitions;
using Vagabond.Common.Enums;

namespace Vagabond.Client.Patches;

internal class CustomExfilPlacementPatch : ModulePatch
{
    public static bool ExtractsAppliedThisRaid;
    public static bool TransitsAppliedThisRaid;
    public static bool LootToastShownThisRaid;
    public static readonly Dictionary<int, CustomExfil> CustomTransitDefinitions = new();
    public static Dictionary<string, ExfiltrationPoint> ExfilPointTemplateCache = new();

    private static readonly FieldInfo TransitPointLookupField =
        AccessTools.Field(typeof(TransitControllerAbstractClass), "Dictionary_0");

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ExfiltrationControllerClass),
            nameof(ExfiltrationControllerClass.InitAllExfiltrationPoints));
    }

    [PatchPostfix]
    public static void Postfix(ExfiltrationControllerClass __instance)
    {
        if (ExtractsAppliedThisRaid && TransitsAppliedThisRaid)
        {
            return;
        }

        var gameWorld = Singleton<GameWorld>.Instance;
        var locationId = gameWorld?.LocationId;
        if (string.IsNullOrWhiteSpace(locationId))
        {
            Vagabond.Log($"null locations");
            return;
        }

        if (!VagabondLocations.LookupTable.ContainsKey(locationId))
        {
            Vagabond.Log($"Unknown location => {locationId}");
            return;
        }

        var raid = VagabondLocations.NormaliseMapName(locationId);
        if (raid == RaidLocation.Nil)
        {
            Vagabond.Log($"Unknown Raid => {locationId}");
            return;
        }

        Vagabond.Log($"RefreshVagabondStateBlocking");
        CommunicationService.RefreshVagabondStateBlocking();
        Vagabond.Log($"RefreshExfilStateBlocking");
        CommunicationService.RefreshExfilStateBlocking();

        ShowLootStreakToast(locationId);

        Vagabond.State.CustomExfils.TryGetValue(raid, out var exfils);
        if (exfils == null)
        {
            return;
        }

        exfils.TryGetValue(locationId, out var definitions);
        ApplyCustomExtracts(__instance, raid, definitions?.Where(x => !x.IsTransit).ToList());
        ApplyCustomTransits(gameWorld.TransitController, raid, definitions?.Where(x => x.IsTransit).ToList());
        FilterExtractions(__instance);
    }

    private static void ShowLootStreakToast(string locationId)
    {
        if (LootToastShownThisRaid)
        {
            return;
        }

        if (Vagabond.IsHeadless())
        {
            return;
        }

        if (!Vagabond.State.LootStreakEnabled)
        {
            return;
        }

        var multiplier = Vagabond.State.LootStreakMultiplier;
        var pct = (int)Math.Round(multiplier * 100);
        var mapName = VagabondLocations.ToHumanName(VagabondLocations.NormaliseMapName(locationId));

        var text = Vagabond.State.LootStreakCount <= 0
            ? $"First raid on {mapName} - Loot spawn is at normal/100%."
            : $"Raid #{Vagabond.State.LootStreakCount + 1} on {mapName} - Loot reduced to {pct}%";

        NotificationManagerClass.DisplayMessageNotification(
            text,
            ENotificationDurationType.Long);

        LootToastShownThisRaid = true;
    }

    public static List<ExfiltrationPoint> ApplyCustomExtracts(ExfiltrationControllerClass controller, RaidLocation raid,
        List<CustomExfil> definitions, bool force = false)
    {
        var addedPoints = new List<ExfiltrationPoint>();

        if (ExtractsAppliedThisRaid && !force)
        {
            return addedPoints;
        }

        if (definitions == null || definitions.Count == 0)
        {
            return addedPoints;
        }

        var pmcExfils = controller?.ExfiltrationPoints?.Where(x => x != null).ToList();
        if (pmcExfils == null || pmcExfils.Count == 0)
        {
            return addedPoints;
        }

        foreach (var definition in definitions)
        {
            if (pmcExfils.Any(x =>
                    string.Equals(x.Settings?.Name, definition.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var template = FindTemplateExfil(pmcExfils, definition, definitions);
            if (template == null)
            {
                Vagabond.LogError($"No template exfil found for '{definition.Identifier}' on {raid}.");
                continue;
            }

            if (definition.HijackExfil)
            {
                template.gameObject.name = definition.Identifier;
                ConfigureHijackedClone(template, definition);
            }
            else
            {
                var cloneObject = LocationScene.Instantiate(template.gameObject);
                cloneObject.name = definition.Identifier;

                var clone = cloneObject.GetComponent<ExfiltrationPoint>();
                if (clone == null)
                {
                    Vagabond.LogError(
                        $"Cloned object for '{definition.Identifier}' does not contain ExfiltrationPoint.");
                    UnityEngine.Object.Destroy(cloneObject);
                    continue;
                }

                cloneObject.transform.SetParent(template.transform.parent, true);
                cloneObject.transform.position = new Vector3(definition.X, definition.Y, definition.Z);
                cloneObject.transform.rotation = Quaternion.Euler(0f, definition.RotationY, 0f);
                cloneObject.SetActive(true);
                ConfigureExtractClone(clone, template, definition, pmcExfils.Count + 1);

                // other mods's patches which hits the ExfiltrationPoint.Awake (found with AmandsSense
                // throwing on missing Exfil.png for example) seems to leave it enabled=false.
                // This should force set the state as a backup, and warn if it happens.
                if (!clone.enabled || !cloneObject.activeInHierarchy)
                {
                    Vagabond.LogError(
                        $"Clone '{definition.Identifier}' state after configure: component.enabled={clone.enabled}, activeInHierarchy={cloneObject.activeInHierarchy}. " +
                        "Another mods's patch likely threw an error. Forcing object to be enabled and active as a failover.");
                    cloneObject.SetActive(true);
                    clone.enabled = true;
                }

                if (!force)
                {
                    var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
                    if (mainPlayer != null && ExfilService.IsPlayerInsidePointTrigger(mainPlayer, clone))
                    {
                        ExfilService.SuppressedCustomExtractPointIds.Add(clone.GetInstanceID());
                    }
                }

                pmcExfils.Add(clone);
                addedPoints.Add(clone);
            }

            Vagabond.Log(
                $"Added custom extract '{definition.DisplayName}' (identifier '{definition.Identifier}') using template '{template.Settings?.Name}'.");
        }

        controller.ExfiltrationPoints = pmcExfils.ToArray();
        ExtractsAppliedThisRaid = true;
        return addedPoints;
    }

    private static ExfiltrationPoint FindTemplateExfil(
        List<ExfiltrationPoint> pmcExfils,
        CustomExfil definition,
        List<CustomExfil> definitions)
    {
        // Hit cache first
        if (!string.IsNullOrWhiteSpace(definition.TemplateExitName))
        {
            if (ExfilPointTemplateCache.TryGetValue(definition.TemplateExitName, out ExfiltrationPoint cachedSpecific))
            {
                return cachedSpecific;
            }
        }

        if (ExfilPointTemplateCache.TryGetValue("preferred", out ExfiltrationPoint cachedPreferred))
        {
            return cachedPreferred;
        }

        if (ExfilPointTemplateCache.TryGetValue("fallback", out ExfiltrationPoint cachedFallback))
        {
            return cachedFallback;
        }
        // cache miss

        var currentEntry = Singleton<GameWorld>.Instance?.MainPlayer?.Profile?.Info?.EntryPoint;

        bool MatchesExplicitTemplate(ExfiltrationPoint x) =>
            string.Equals(x.Settings?.Name, definition.TemplateExitName, StringComparison.OrdinalIgnoreCase);

        bool IsGoodTemplate(ExfiltrationPoint x, bool requireActiveStatus)
        {
            if (x == null || x.Settings == null)
            {
                return false;
            }

            if (IsCustomExtract(x, definitions))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(x.Settings.Name))
            {
                return false;
            }

            if (x is SharedExfiltrationPoint)
            {
                return false;
            }

            if (x.Switch != null)
            {
                return false;
            }

            if (x.Settings.ExfiltrationType != EExfiltrationType.Individual)
            {
                return false;
            }

            if (x.Requirements?.Length > 0)
            {
                return false;
            }

            if (ExfilService.IsVehicleTemplate(x))
            {
                return false;
            }

            if (x.Settings.Chance < 100f)
            {
                return false;
            }

            if (x.Settings.MinTime > 0f || x.Settings.MaxTime > 0f)
            {
                return false;
            }

            if (x.Settings.EventAvailable)
            {
                return false;
            }

            if (requireActiveStatus && x.Status == EExfiltrationStatus.NotPresent)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(currentEntry))
            {
                if (x.EligibleEntryPoints == null || x.EligibleEntryPoints.Length == 0)
                {
                    return false;
                }

                if (!x.EligibleEntryPoints.Any(ep =>
                        string.Equals(ep, currentEntry, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            return true;
        }

        if (!string.IsNullOrWhiteSpace(definition.TemplateExitName))
        {
            var explicitTemplate = pmcExfils.FirstOrDefault(MatchesExplicitTemplate);
            if (explicitTemplate != null)
            {
                ExfilPointTemplateCache.Add(definition.TemplateExitName, explicitTemplate);
                return explicitTemplate;
            }
        }

        var preferred = pmcExfils.FirstOrDefault(x => IsGoodTemplate(x, requireActiveStatus: true));
        if (preferred != null)
        {
            ExfilPointTemplateCache.Add("preferred", preferred);
            return preferred;
        }

        var fallback = pmcExfils.FirstOrDefault(x => IsGoodTemplate(x, requireActiveStatus: false));
        if (fallback != null)
        {
            ExfilPointTemplateCache.Add("fallback", fallback);
            return fallback;
        }

        return null;
    }

    public static void ApplyCustomTransits(TransitControllerAbstractClass transitController, RaidLocation raid,
        List<CustomExfil> definitions, bool force = false)
    {
        if (TransitsAppliedThisRaid && !force)
        {
            return;
        }

        if (definitions.Count == 0)
        {
            return;
        }

        if (transitController == null)
        {
            Vagabond.Log($"Transit controller is null on {raid}; cannot place custom transit points, retrying...");
            return;
        }

        var lookup = GetTransitLookup(transitController);
        if (lookup == null)
        {
            Vagabond.LogError("Unable to resolve transit point lookup on TransitControllerAbstractClass.");
            return;
        }

        var existingTransitPoints = LocationScene.GetAllObjects<TransitPoint>().Where(x => x != null).ToList();
        if (existingTransitPoints.Count == 0)
        {
            Vagabond.LogError($"No TransitPoint template exists in the {raid} scene.");
            return;
        }

        foreach (var definition in definitions)
        {
            if (!definition.TransitPointId.HasValue)
            {
                Vagabond.LogError($"Custom transit '{definition.Identifier}' is missing TransitPointId.");
                continue;
            }

            if (lookup.ContainsKey(definition.TransitPointId.Value)
                || existingTransitPoints.Any(x =>
                    x.parameters != null && x.parameters.id == definition.TransitPointId.Value))
            {
                continue;
            }

            var template = existingTransitPoints.FirstOrDefault(x =>
                               definition.TemplateTransitId.HasValue && x.parameters != null &&
                               x.parameters.id == definition.TemplateTransitId.Value)
                           ?? existingTransitPoints.FirstOrDefault();

            if (template == null)
            {
                Vagabond.LogError($"No transit template found for '{definition.Identifier}' on {raid}.");
                continue;
            }

            var cloneObject = LocationScene.Instantiate(template.gameObject);
            cloneObject.name = definition.Identifier;
            cloneObject.SetActive(true);
            cloneObject.transform.SetParent(template.transform.parent, true);
            cloneObject.transform.position = new Vector3(definition.X, definition.Y, definition.Z);
            cloneObject.transform.rotation = Quaternion.Euler(0f, definition.RotationY, 0f);

            var clone = cloneObject.GetComponent<TransitPoint>();
            if (clone == null)
            {
                Vagabond.LogError($"Cloned object for '{definition.Identifier}' does not contain TransitPoint.");
                UnityEngine.Object.Destroy(cloneObject);
                continue;
            }

            ConfigureTransitClone(clone, transitController, template, definition);
            lookup[definition.TransitPointId.Value] = clone;
            CustomTransitDefinitions[definition.TransitPointId.Value] = definition;

            Vagabond.Log(
                $"Added custom transit '{raid}' (identifier '{definition.Identifier}') to '{definition.DestinationLocation}'.");
        }

        TransitsAppliedThisRaid = true;
    }

    private static void ConfigureTransitClone(TransitPoint clone, TransitControllerAbstractClass controller,
        TransitPoint template, CustomExfil definition)
    {
        clone.Controller = controller;
        clone.Enabled = true;
        clone.IsActive = definition.IsActive;
        clone.parameters = new LocationSettingsClass.Location.TransitParameters
        {
            id = definition.TransitPointId!.Value,
            active = definition.IsActive,
            name = definition.Identifier,
            description = string.IsNullOrWhiteSpace(definition.Description)
                ? definition.DisplayName
                : definition.Description,
            conditions = BuildTransitConditionsString(definition),
            activateAfterSec = definition.ActivateAfterSeconds,
            time = (ushort)Mathf.Clamp(Mathf.RoundToInt(definition.ExfiltrationTime), 1, ushort.MaxValue),
            target = string.IsNullOrWhiteSpace(definition.AccessKeysSourceLocation)
                ? definition.DestinationLocation
                : definition.AccessKeysSourceLocation,
            location = definition.DestinationLocation,
            events = definition.Events,
            hideIfNoKey = definition.HideIfNoKey,
            eventsEnable = definition.Events
        };

        var collider = clone.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = true;
            collider.isTrigger = true;
        }

        var templateCollider = template.GetComponent<Collider>();
        if (collider is BoxCollider box && templateCollider is BoxCollider templateBox)
        {
            box.center = templateBox.center;
            //box.size = templateBox.size;
            // I hope this is not too small now. 
            // Going for 5x5m, and 2m high
            box.size = new Vector3(5f, templateBox.size.y, 5f);
        }
    }

    private static void ConfigureHijackedClone(ExfiltrationPoint template, CustomExfil definition)
    {
        template.Settings.Name = definition.DisplayName;
        template.Settings.Chance = 100f;

        if (template.Switch != null)
        {
            var sw = template.Switch;
            while (sw != null)
            {
                sw.Operatable = true;
                sw = sw.PreviousSwitch;
            }
        }

        if (definition.Requirements.Count > 0)
        {
            var added = BuildRequirements(definition, template);
            var existing = template.Requirements ?? Array.Empty<ExfiltrationRequirement>();

            var existingTransfer = existing.OfType<TransferItemRequirement>().FirstOrDefault();
            var addedTransfer = added.OfType<TransferItemRequirement>().FirstOrDefault();

            if (existingTransfer != null && addedTransfer != null)
            {
                existingTransfer.Count = addedTransfer.Count;
                existingTransfer.Id = addedTransfer.Id;
                if (!string.IsNullOrWhiteSpace(addedTransfer.RequirementTip))
                {
                    existingTransfer.RequirementTip = addedTransfer.RequirementTip;
                }

                added = added.Where(r => r is not TransferItemRequirement).ToArray();
            }

            if (added.Length > 0)
            {
                var merged = new ExfiltrationRequirement[existing.Length + added.Length];
                Array.Copy(existing, 0, merged, 0, existing.Length);
                Array.Copy(added, 0, merged, existing.Length, added.Length);
                template.Requirements = merged;
            }
        }
    }

    private static void ConfigureExtractClone(ExfiltrationPoint clone, ExfiltrationPoint template,
        CustomExfil definition, int idOffset)
    {
        var eligibleEntryPoints = BuildEligibleEntryPoints(definition, template);
        var settings = new LocationExitClass
        {
            Name = definition.DisplayName,
            EntryPoints = string.Join(",", eligibleEntryPoints),
            ExfiltrationTime = definition.ExfiltrationTime,
            ExfiltrationType = EExfiltrationType.Individual,
            PassageRequirement = ERequirementState.None,
            PlayersCount = 0,
            Id = string.Empty,
            Count = 0,
            RequiredSlot = EFT.InventoryLogic.EquipmentSlot.FirstPrimaryWeapon,
            RequirementTip = string.Empty,
            MinTime = 0f,
            MaxTime = 0f,
            Chance = 100f,
            EventAvailable = false
        };

        clone.QueuedPlayers.Clear();
        clone.LoadSettings(template.Id.Add(idOffset + 1000), settings, true);
        clone.Requirements = BuildRequirements(definition, clone);

        clone.Settings.Name = definition.DisplayName;
        clone.Settings.EntryPoints = settings.EntryPoints;
        clone.Settings.Chance = 100f;
        clone.Settings.MinTime = 0f;
        clone.Settings.MaxTime = 0f;
        clone.Settings.EventAvailable = false;
        clone.EligibleEntryPoints = eligibleEntryPoints;
        clone.Reusable = true;
        clone.Switch = null;

        ExfilService.NormalizeExtractColliders(clone, template);

        clone.Enable();
        clone.EnableInteraction();
        clone.SetStatusLogged(EExfiltrationStatus.RegularMode, "Vagabond.CustomExfilPlacementPatch");

        var playerEntryDiag = Singleton<GameWorld>.Instance?.MainPlayer?.Profile?.Info?.EntryPoint ?? "<null>";
        Vagabond.Log(
            $"Configured extract clone '{definition.Identifier}' (player entry='{playerEntryDiag}', enabled={clone.enabled}, activeInHierarchy={clone.gameObject.activeInHierarchy}, isActiveAndEnabled={clone.isActiveAndEnabled}) with EligibleEntryPoints=[{string.Join(", ", eligibleEntryPoints)}]");
    }

    private static string[] BuildEligibleEntryPoints(CustomExfil definition, ExfiltrationPoint template)
    {
        var eligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddEntryPoints(eligible, definition.EntryPoints);

        var syncedDefinition = TryGetSyncedDefinition(definition);
        if (syncedDefinition != null)
        {
            AddEntryPoints(eligible, syncedDefinition.EntryPoints);
        }

        if (eligible.Count == 0)
        {
            foreach (var entry in GetAllMapPmcEntryPoints())
            {
                eligible.Add(entry);
            }
        }

        if (eligible.Count == 0 && template?.EligibleEntryPoints != null)
        {
            foreach (var entryPoint in template.EligibleEntryPoints)
            {
                if (!string.IsNullOrWhiteSpace(entryPoint))
                {
                    eligible.Add(entryPoint.Trim().ToLowerInvariant());
                }
            }
        }

        var currentEntry = Singleton<GameWorld>.Instance?.MainPlayer?.Profile?.Info?.EntryPoint;
        if (!string.IsNullOrWhiteSpace(currentEntry))
        {
            eligible.Add(currentEntry.Trim().ToLowerInvariant());
        }

        if (eligible.Count == 0)
        {
            Vagabond.LogError(
                $"BuildEligibleEntryPoints: resolved no entry points for '{definition.Identifier}'.");
        }

        return eligible.ToArray();
    }

    private static void AddEntryPoints(HashSet<string> target, string entryPoints)
    {
        if (string.IsNullOrWhiteSpace(entryPoints))
        {
            return;
        }

        foreach (var entry in entryPoints.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.IsNullOrWhiteSpace(entry))
            {
                target.Add(entry.Trim().ToLowerInvariant());
            }
        }
    }

    private static CustomExfil TryGetSyncedDefinition(CustomExfil definition)
    {
        var locationId = Singleton<GameWorld>.Instance?.LocationId;
        if (string.IsNullOrWhiteSpace(locationId))
        {
            return null;
        }

        var raid = VagabondLocations.NormaliseMapName(locationId);
        if (raid == RaidLocation.Nil)
        {
            return null;
        }

        if (!Vagabond.State.CustomExfils.TryGetValue(raid, out var byMap) || byMap == null)
        {
            return null;
        }

        if (!byMap.TryGetValue(locationId, out var defs) || defs == null)
        {
            return null;
        }

        return defs.FirstOrDefault(x =>
            !x.IsTransit &&
            (
                string.Equals(x.Identifier, definition.Identifier, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.DisplayName, definition.DisplayName, StringComparison.OrdinalIgnoreCase)
            ));
    }

    private static IEnumerable<string> GetAllMapPmcEntryPoints()
    {
        var controller = Singleton<GameWorld>.Instance?.ExfiltrationController;
        var points = controller?.ExfiltrationPoints;
        if (points == null)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var point in points)
        {
            if (point?.Settings == null)
            {
                continue;
            }

            if (point is SharedExfiltrationPoint)
            {
                continue;
            }

            if (point.Settings.ExfiltrationType != EExfiltrationType.Individual)
            {
                continue;
            }

            if (point.EligibleEntryPoints == null)
            {
                continue;
            }

            foreach (var entry in point.EligibleEntryPoints)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                var normalized = entry.Trim().ToLowerInvariant();
                if (seen.Add(normalized))
                {
                    yield return normalized;
                }
            }
        }
    }

    private static ExfiltrationRequirement[] BuildRequirements(CustomExfil definition, ExfiltrationPoint point)
    {
        if (definition.Requirements.Count == 0)
        {
            return Array.Empty<ExfiltrationRequirement>();
        }

        var built = new List<ExfiltrationRequirement>();

        foreach (var reqDef in definition.Requirements)
        {
            var eftType = MapRequirementType(reqDef.Type);
            if (eftType == ERequirementState.None)
            {
                continue;
            }

            var req = ExfiltrationRequirement.CreateRequirement(eftType) as ExfiltrationRequirement;
            if (req == null)
            {
                Vagabond.LogError($"Unsupported requirement '{reqDef.Type}' on '{definition.Identifier}'.");
                continue;
            }

            var reqId = reqDef.Id;
            if (eftType == ERequirementState.TransferItem && string.IsNullOrWhiteSpace(reqId))
            {
                reqId = Currencies.Ruble;
            }

            req.Requirement = eftType;
            req.Id = reqId;
            req.Count = reqDef.Count;
            req.RequirementTip = reqDef.RequirementTip;

            if (!string.IsNullOrWhiteSpace(reqDef.RequiredSlot)
                && Enum.TryParse<EFT.InventoryLogic.EquipmentSlot>(reqDef.RequiredSlot, true, out var slot))
            {
                req.RequiredSlot = slot;
            }

            req.Start(point);
            built.Add(req);
        }

        return built.ToArray();
    }

    private static Dictionary<int, TransitPoint> GetTransitLookup(TransitControllerAbstractClass controller)
    {
        return TransitPointLookupField?.GetValue(controller) as Dictionary<int, TransitPoint>;
    }

    private static string BuildTransitConditionsString(CustomExfil definition)
    {
        if (definition.Requirements == null || definition.Requirements.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var req in definition.Requirements)
        {
            if (req.Type != CustomExfilRequirementType.Cost)
            {
                continue;
            }

            var id = string.IsNullOrWhiteSpace(req.Id) ? Currencies.Ruble : req.Id;
            var shortName = (id + " ShortName").Localized();
            parts.Add(string.Format("EXFIL_Transfer".Localized(), shortName, req.Count));
        }

        return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
    }

    private static bool IsCustomExtract(ExfiltrationPoint exfil, List<CustomExfil> definitions)
    {
        return !string.IsNullOrWhiteSpace(exfil?.Settings?.Name)
               && definitions
                   .Where(x => !x.IsTransit)
                   .Any(x => string.Equals(x.DisplayName, exfil.Settings.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static ERequirementState MapRequirementType(CustomExfilRequirementType type)
    {
        return type switch
        {
            CustomExfilRequirementType.HasItem => ERequirementState.HasItem,
            CustomExfilRequirementType.EmptySlot => ERequirementState.Empty,
            CustomExfilRequirementType.Cost => ERequirementState.TransferItem,
            _ => ERequirementState.None
        };
    }

    public static void FilterExtractions(ExfiltrationControllerClass __instance)
    {
        if (__instance == null)
        {
            return;
        }

        if (__instance.ExfiltrationPoints != null)
        {
            foreach (var exfil in __instance.ExfiltrationPoints)
            {
                if (exfil == null)
                {
                    continue;
                }

                if (exfil.Settings != null && (ExfilService.IsCustomExfil(exfil.Settings) ||
                                               ExfilService.IsQuestNativeExfil(exfil.Settings)))
                {
                    continue;
                }

                HideExfil(exfil);
            }
        }

        if (__instance.SecretExfiltrationPoints != null)
        {
            var kept = new List<SecretExfiltrationPoint>();
            foreach (var secret in __instance.SecretExfiltrationPoints)
            {
                if (secret == null)
                {
                    continue;
                }

                if (secret.Settings != null && ExfilService.IsQuestNativeExfil(secret.Settings))
                {
                    kept.Add(secret);
                    continue;
                }

                HideExfil(secret);
            }

            __instance.SecretExfiltrationPoints = kept.ToArray();
        }
    }

    private static void HideExfil(ExfiltrationPoint exfil)
    {
        if (exfil?.Settings == null)
        {
            return;
        }

        if (exfil is SharedExfiltrationPoint shared)
        {
            shared.EligibleEntryPoints = Array.Empty<string>();
            shared.Settings.EntryPoints = string.Empty;
            return;
        }

        if (exfil is SecretExfiltrationPoint secret)
        {
            secret.EligibleForPmc = false;
            secret.EligibleForScav = false;
            secret.SetStatusLogged(EExfiltrationStatus.Hidden, "Vagabond.HideExfil");
            DisableColliders(secret);
            return;
        }

        exfil.Settings.Chance = 0f;
        exfil.Settings.EntryPoints = string.Empty;
        exfil.EligibleEntryPoints = Array.Empty<string>();
        DisableColliders(exfil);

        if (MonoBehaviourSingleton<GameUI>.Instance?.TimerPanel != null)
        {
            exfil.DisableInteraction();
        }
    }

    private static void DisableColliders(ExfiltrationPoint exfil)
    {
        foreach (var collider in exfil.GetComponentsInChildren<Collider>(true))
        {
            collider.enabled = false;
        }
    }
}

internal class CustomExfilCleanupPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnDestroy));
    }

    [PatchPrefix]
    private static void Prefix()
    {
        CustomExfilPlacementPatch.TransitsAppliedThisRaid = false;
        CustomExfilPlacementPatch.ExtractsAppliedThisRaid = false;
        CustomExfilPlacementPatch.LootToastShownThisRaid = false;
        CustomExfilPlacementPatch.CustomTransitDefinitions.Clear();
        CustomExfilPlacementPatch.ExfilPointTemplateCache.Clear();
        ExfilService.SuppressedCustomExtractPointIds.Clear();
        TransitCostService.Cleanup();
        TransitInteractionLabelPatch.ClearCache();
        Vagabond.State.LastRaidStateSyncLocationId = string.Empty;
    }
}

internal class CustomTransitRetryPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.Update));
    }

    [PatchPostfix]
    private static void Postfix(GameWorld __instance)
    {
        if (CustomExfilPlacementPatch.ExtractsAppliedThisRaid && CustomExfilPlacementPatch.TransitsAppliedThisRaid)
        {
            return;
        }

        var locationId = __instance?.LocationId;
        if (string.IsNullOrWhiteSpace(locationId))
        {
            return;
        }

        if (!VagabondLocations.LookupTable.ContainsKey(locationId))
        {
            return;
        }

        var raid = VagabondLocations.NormaliseMapName(locationId);
        if (raid == RaidLocation.Nil)
        {
            return;
        }

        Vagabond.State.CustomExfils.TryGetValue(raid, out var exfils);

        if (exfils == null)
        {
            return;
        }

        exfils.TryGetValue(locationId, out var definitions);

        CustomExfilPlacementPatch.ApplyCustomExtracts(__instance.ExfiltrationController, raid,
            definitions?.Where(x => !x.IsTransit).ToList());
        CustomExfilPlacementPatch.ApplyCustomTransits(__instance.TransitController, raid,
            definitions?.Where(x => x.IsTransit).ToList());
        CustomExfilPlacementPatch.FilterExtractions(__instance.ExfiltrationController);
    }
}