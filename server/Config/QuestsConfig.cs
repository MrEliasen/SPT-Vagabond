using System.Reflection;
using System.Text.Json;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Utils.Json;
using Vagabond.Server.Services;
using Path = System.IO.Path;

namespace Vagabond.Server.Config;

public sealed class QuestConfigEntry
{
    // custom vagabond quest completion effect ("joinHideout", "relocateHideout")
    public string Effect { get; set; } = "joinHideout";
    public Quest NewQuest { get; set; } = null!;
    public Dictionary<string, Dictionary<string, string>> Locales { get; set; } = new();
    public PlayerSide? LockedToSide { get; set; }
}

public static class QuestsConfig
{
    public static List<NewQuestDetails> Quests = new();

    public static string? RelocationQuestId;
    public static Dictionary<string, string> HideoutTraderByQuestId = new();

    public static void Initialize()
    {
        Quests = new List<NewQuestDetails>();
        RelocationQuestId = null;
        HideoutTraderByQuestId = new Dictionary<string, string>();

        foreach (var entry in LoadAll())
        {
            var questId = entry.NewQuest.Id.ToString();
            switch (entry.Effect)
            {
                case "relocateHideout":
                    RelocationQuestId = questId;
                    break;
                case "joinHideout":
                    HideoutTraderByQuestId[questId] = entry.NewQuest.TraderId.ToString();
                    break;
            }

            Quests.Add(new NewQuestDetails
            {
                NewQuest = entry.NewQuest,
                Locales = entry.Locales,
                LockedToSide = entry.LockedToSide
            });
        }
    }

    private static List<QuestConfigEntry> LoadAll()
    {
        var result = new List<QuestConfigEntry>();

        try
        {
            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ??
                              AppContext.BaseDirectory;
            var localDir = Path.Combine(assemblyDir, "config", "quests");
            var siblingDir = Path.Combine(Directory.GetParent(assemblyDir)?.FullName ?? assemblyDir, "config",
                "quests");

            var chosenDir = Directory.Exists(localDir) ? localDir : siblingDir;
            if (!Directory.Exists(chosenDir))
            {
                throw new Exception($"quests config dir not found, tried {localDir} and {siblingDir}");
            }

            var options = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true
            };
            foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters())
            {
                options.Converters.Add(converter);
            }

            foreach (var path in Directory.EnumerateFiles(chosenDir, "*.json"))
            {
                QuestConfigEntry? entry;
                try
                {
                    var json = File.ReadAllText(path);
                    entry = JsonSerializer.Deserialize<QuestConfigEntry>(json, options);
                }
                catch (Exception ex)
                {
                    VagabondLogger.Error($"quests config: failed to parse {path}: {ex}");
                    continue;
                }

                if (entry?.NewQuest == null)
                {
                    VagabondLogger.Warning($"quests config: {Path.GetFileName(path)} has no newQuest, skipping.");
                    continue;
                }

                result.Add(entry);
            }
        }
        catch (Exception ex)
        {
            VagabondLogger.Error($"quests config error, will use empty. Error: {ex}");
        }

        return result;
    }
}
