using System.Text.Json;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Mod;
using SPTarkov.Server.Core.Utils.Json;
using Vagabond.Common.Data;
using Vagabond.Common.Definitions;
using Vagabond.Server.Config;

namespace Vagabond.Server.Services;

public static class QuestService
{
    public static Dictionary<string, List<string>> BuildExfilList(VagabondSessionState state)
    {
        Dictionary<string, List<string>> exfilList = new();

        if (state.QuestExfils.Count == 0)
        {
            return exfilList;
        }

        foreach (var quests in state.QuestExfils)
        {
            ExfilQuests.List.TryGetValue(quests, out var list);
            if (list == null)
            {
                continue;
            }

            foreach (var quest in list)
            {
                if (!exfilList.TryGetValue(quest.Key, out var exfils))
                {
                    exfils = new List<string>();
                    exfilList.Add(quest.Key, exfils);
                }

                foreach (var exfil in quest.Value)
                {
                    if (!exfils.Contains(exfil))
                    {
                        exfils.Add(exfil);
                    }
                }
            }
        }

        return exfilList;
    }

    public static void LoadQuests()
    {
        var quests = QuestsConfig.Quests;
        if (quests.Count == 0)
        {
            VagabondLogger.Warning("no quests loaded from config, skipping.");
            return;
        }

        var customQuestService = ReflectionUtil.GetService<CustomQuestService>();
        if (customQuestService == null)
        {
            VagabondLogger.Warning($"failed to load quests, customQuestService is null.");
            return;
        }

        var databaseService = ReflectionUtil.GetService<DatabaseService>();
        var supportedLocales = databaseService?.GetLocales().Global.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                               ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "en" };

        foreach (var quest in quests)
        {
            var defaultLocale = quest.Locales.TryGetValue("en", out var en)
                ? en
                : quest.Locales.First().Value;

            foreach (var lang in supportedLocales)
            {
                quest.Locales.TryAdd(lang, defaultLocale);
            }

            if (databaseService != null)
            {
                ExpandHandoverItems(quest.NewQuest, databaseService);
            }

            var result = customQuestService.CreateQuest(quest);
            if (!result.Success)
            {
                foreach (var err in result.Errors)
                {
                    VagabondLogger.Warning($"quest registration of quest id {quest.NewQuest.Id}, error: {err}");
                }
            }
        }
    }

    private static void ExpandHandoverItems(Quest quest, DatabaseService databaseService)
    {
        var conditions = quest.Conditions;
        if (conditions == null)
        {
            return;
        }

        foreach (var list in new[] { conditions.AvailableForStart, conditions.AvailableForFinish })
        {
            if (list == null)
            {
                continue;
            }

            foreach (var condition in list)
            {
                ExpandCondition(condition, databaseService);
            }
        }
    }

    private static void ExpandCondition(QuestCondition condition, DatabaseService databaseService)
    {
        var ext = condition.ExtensionData;
        if (ext == null)
        {
            return;
        }

        var hasItems = TryReadStringList(ext, "items", out var explicitItems);
        var hasCategories = TryReadStringList(ext, "categories", out var categories);
        if (!hasItems && !hasCategories)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (TryReadStringList(ext, "excludeItems", out var excludeList))
        {
            foreach (var tpl in excludeList)
            {
                if (!string.IsNullOrWhiteSpace(tpl))
                {
                    seen.Add(tpl);
                }
            }
        }

        var result = new List<string>();

        foreach (var tpl in explicitItems)
        {
            if (!string.IsNullOrWhiteSpace(tpl) && seen.Add(tpl))
            {
                result.Add(tpl);
            }
        }

        if (hasCategories && categories.Count > 0)
        {
            var caliber = TryReadString(ext, "caliber", out var c) ? c : null;
            var minBackpackSize = TryReadInt(ext, "minBackpackSize", out var size) ? size : (int?)null;
            var categorySet = new HashSet<string>(categories, StringComparer.Ordinal);
            var items = databaseService.GetItems();

            var matches = new List<string>();
            foreach (var item in items.Values)
            {
                if (item?.Properties == null || item.Type != "Item")
                {
                    continue;
                }

                if (item.Properties.QuestItem == true)
                {
                    continue;
                }

                if (!IsDescendantOfAny(item, categorySet, items))
                {
                    continue;
                }

                if (caliber != null && !MatchesCaliber(item.Properties, caliber))
                {
                    continue;
                }

                if (minBackpackSize.HasValue && TotalGridSlots(item.Properties) < minBackpackSize.Value)
                {
                    continue;
                }

                matches.Add(item.Id);
            }

            // keeps the list stable across loads
            matches.Sort(StringComparer.Ordinal);
            foreach (var id in matches)
            {
                if (seen.Add(id))
                {
                    result.Add(id);
                }
            }
        }

        condition.Target = new ListOrT<string>(result, null);

        // remove as its no longer needed, before we hand it over to the client
        ext.Remove("items");
        ext.Remove("categories");
        ext.Remove("caliber");
        ext.Remove("minBackpackSize");
        ext.Remove("excludeItems");
    }
    
    private static bool IsDescendantOfAny(TemplateItem item, HashSet<string> categories,
        Dictionary<MongoId, TemplateItem> items)
    {
        string current = item.Parent;
        var guard = 0;
        while (!string.IsNullOrEmpty(current) && guard++ < 32)
        {
            if (categories.Contains(current))
            {
                return true;
            }

            if (!items.TryGetValue(current, out var parent) || parent == null)
            {
                break;
            }

            current = parent.Parent;
        }

        return false;
    }

    private static bool MatchesCaliber(TemplateItemProperties props, string caliber)
    {
        return string.Equals(props.Caliber, caliber, StringComparison.Ordinal)
               || string.Equals(props.AmmoCaliber, caliber, StringComparison.Ordinal);
    }

    private static int TotalGridSlots(TemplateItemProperties props)
    {
        if (props.Grids == null)
        {
            return 0;
        }

        var total = 0;
        foreach (var grid in props.Grids)
        {
            total += (grid.Properties?.CellsH ?? 0) * (grid.Properties?.CellsV ?? 0);
        }

        return total;
    }

    private static bool TryReadStringList(IDictionary<string?, object?> ext, string key, out List<string> values)
    {
        values = new List<string>();
        if (!ext.TryGetValue(key, out var raw) || raw is not JsonElement el || el.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var element in el.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                values.Add(element.GetString()!);
            }
        }

        return true;
    }

    private static bool TryReadString(IDictionary<string?, object?> ext, string key, out string? value)
    {
        value = null;
        if (!ext.TryGetValue(key, out var raw) || raw is not JsonElement el || el.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = el.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadInt(IDictionary<string?, object?> ext, string key, out int value)
    {
        value = 0;
        return ext.TryGetValue(key, out var raw) && raw is JsonElement el
            && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
    }
}