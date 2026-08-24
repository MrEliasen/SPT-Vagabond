using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.Interactive;
using EFT.UI;
using EFT.UI.BattleTimer;
using HarmonyLib;
using UnityEngine;
using Vagabond.Client.Patches;
using Vagabond.Common.Data;
using Vagabond.Common.Enums;

namespace Vagabond.Client.Services;

public static class RaidService
{
    private const long ExfilPollIntervalMs = 5000;
    private static bool _exfilPollInFlight;
    private static long _nextExfilPollAtMs;
    private static bool _wasInRaid;

    public static bool IsInRaid()
    {
        var gameWorld = Singleton<GameWorld>.Instance;
        return gameWorld?.ExfiltrationController != null && !string.IsNullOrWhiteSpace(gameWorld.LocationId);
    }

    public static void HandleExfilUpdatePolling()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs < _nextExfilPollAtMs)
        {
            return;
        }

        _nextExfilPollAtMs = ((nowMs / ExfilPollIntervalMs) + 1) * ExfilPollIntervalMs;

        var isInRaid = IsInRaid();
        if (isInRaid && !_wasInRaid)
        {
            StartPolling();
        }
        else if (!isInRaid && _wasInRaid)
        {
            StopPolling();
        }

        _wasInRaid = isInRaid;
        if (!isInRaid || _exfilPollInFlight)
        {
            return;
        }

        PollExfilStateAsync();
    }

    private static void StartPolling()
    {
        _exfilPollInFlight = false;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _nextExfilPollAtMs = ((nowMs / ExfilPollIntervalMs) + 1) * ExfilPollIntervalMs;
    }

    private static void StopPolling()
    {
        _exfilPollInFlight = false;
        _nextExfilPollAtMs = 0;
    }

    private static async Task PollExfilStateAsync()
    {
        if (_exfilPollInFlight)
        {
            return;
        }

        _exfilPollInFlight = true;
        try
        {
            await CommunicationService.RefreshExfilState();
        }
        catch (Exception ex)
        {
            Vagabond.Log($"Exfil polling failed: {ex}");
        }
        finally
        {
            _exfilPollInFlight = false;
        }
    }

    public static void UpdateCurrentRaidExfils()
    {
        if (!IsInRaid())
        {
            return;
        }

        var gameWorld = Singleton<GameWorld>.Instance;
        var game = Singleton<AbstractGame>.Instance;
        var controller = gameWorld?.ExfiltrationController;
        var timerPanel = game?.GameUi?.TimerPanel;
        var locationId = gameWorld?.LocationId;

        if (gameWorld == null || controller == null || gameWorld.MainPlayer == null ||
            string.IsNullOrWhiteSpace(locationId))
        {
            return;
        }

        var raid = VagabondLocations.NormaliseMapName(locationId);
        if (raid == RaidLocation.Nil)
        {
            return;
        }

        if (!Vagabond.State.CustomExfils.TryGetValue(raid, out var mapExfils) || mapExfils == null)
        {
            return;
        }

        if (!mapExfils.TryGetValue(locationId, out var definitions) || definitions == null || definitions.Count == 0)
        {
            return;
        }

        var livePoints = CustomExfilPlacementPatch.ApplyCustomExtracts(
            controller,
            raid,
            definitions.Where(x => !x.IsTransit).ToList(),
            true);

        CustomExfilPlacementPatch.ApplyCustomTransits(
            gameWorld.TransitController,
            raid,
            definitions.Where(x => x.IsTransit).ToList(),
            true);

        CustomExfilPlacementPatch.FilterExtractions(controller);

        if (game == null || timerPanel == null || livePoints == null || livePoints.Count == 0)
        {
            return;
        }

        foreach (var point in livePoints)
        {
            RegisterLiveExfilPoint(gameWorld, game, timerPanel, controller, point);
        }

        RefreshPlayerInteractions(gameWorld.MainPlayer);
        timerPanel.ShowTimer(true, true);
    }

    private static void RegisterLiveExfilPoint(
        GameWorld gameWorld,
        AbstractGame game,
        ExtractionTimersPanel timerPanel,
        ExfiltrationController controller,
        ExfiltrationPoint point)
    {
        if (point?.Settings?.Name == null)
        {
            return;
        }

        EnsureTimerPanelRow(gameWorld, timerPanel, controller, point);

        if (!BindPointToCurrentGame(game, point))
        {
            return;
        }

        game.UpdateExfiltrationUi(point, false, true);
        ReprocessPlayerIfAlreadyInside(gameWorld.MainPlayer, point);
    }

    private static void EnsureTimerPanelRow(
        GameWorld gameWorld,
        ExtractionTimersPanel timerPanel,
        ExfiltrationController controller,
        ExfiltrationPoint point)
    {
        var timersField = AccessTools.Field(typeof(ExtractionTimersPanel), "_timers");
        var templateField = AccessTools.Field(typeof(ExtractionTimersPanel), "_timerPanelTemplate");
        var containerField = AccessTools.Field(typeof(ExtractionTimersPanel), "_container");
        var sideField = AccessTools.Field(typeof(ExtractionTimersPanel), "_side");
        var stringBuilderField = AccessTools.Field(typeof(ExtractionTimersPanel), "_stringBuilder");

        var timers = timersField.GetValue(timerPanel) as Dictionary<string, ExitTimerPanel>;
        var template = templateField.GetValue(timerPanel) as ExitTimerPanel;
        var container = containerField.GetValue(timerPanel) as RectTransform;
        var side = sideField.GetValue(timerPanel) is EPlayerSide s ? s : EPlayerSide.Bear;
        var stringBuilder = stringBuilderField.GetValue(timerPanel) as StringBuilder;

        if (timers == null || template == null || container == null || stringBuilder == null)
        {
            return;
        }

        if (timers.ContainsKey(point.Settings.Name))
        {
            return;
        }

        var mainPlayer = gameWorld.MainPlayer;
        if (mainPlayer == null)
        {
            return;
        }

        var eligible = controller.EligiblePoints(mainPlayer.Profile);
        var index = Array.FindIndex(
                        eligible,
                        x => string.Equals(x.Settings?.Name, point.Settings?.Name, StringComparison.OrdinalIgnoreCase))
                    + 1;

        if (index <= 0)
        {
            index = timers.Count + 1;
        }

        var row = UnityEngine.Object.Instantiate(template, container);
        row.Show(
            DateTimeExtensions.UtcNow.AddSeconds(point.Settings.MaxTime * 60f),
            side,
            index,
            stringBuilder,
            true,
            timerPanel.ProfileId,
            point);

        timers.Add(point.Settings.Name, row);
    }

    private static bool BindPointToCurrentGame(AbstractGame game, ExfiltrationPoint point)
    {
        if (TryBindPointToFikaGame(game, point))
        {
            return true;
        }

        if (TryBindPointToEndByExitScenario(game, point))
        {
            return true;
        }

        Vagabond.LogError(
            $"Could not register live exfil '{point.Settings?.Name}' for running game type '{game.GetType().FullName}'.");
        return false;
    }

    private static bool TryBindPointToFikaGame(AbstractGame game, ExfiltrationPoint point)
    {
        var updateMethod = game.GetType().GetMethod(
            "UpdateExfilPointFromServer",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(ExfiltrationPoint), typeof(bool) },
            null);

        if (updateMethod == null)
        {
            return false;
        }

        try
        {
            updateMethod.Invoke(game, new object[] { point, true });
        }
        catch (Exception ex)
        {
            Vagabond.LogError(
                $"Failed to register live exfil '{point.Settings?.Name}' through CoopGame.UpdateExfilPointFromServer: {ex}");
            return false;
        }

        if (GetHandlerCount(point.OnStartExtraction) == 0 && GetHandlerCount(point.OnCancelExtraction) == 0)
        {
            TryBindPointToFikaManagerComponent(game, point);
        }

        BindStatusUiHandler(game, point);
        AppendPointToFikaManager(game, point);
        return true;
    }

    private static bool TryBindPointToEndByExitScenario(AbstractGame game, ExfiltrationPoint point)
    {
        var scenario = ResolveEndByExitScenario(game);
        if (scenario == null)
        {
            return false;
        }

        point.OnStartExtraction = (Action<ExfiltrationPoint, Player>)Delegate.Remove(
            point.OnStartExtraction,
            new Action<ExfiltrationPoint, Player>(scenario.StartExtraction));
        point.OnStartExtraction = (Action<ExfiltrationPoint, Player>)Delegate.Combine(
            point.OnStartExtraction,
            new Action<ExfiltrationPoint, Player>(scenario.StartExtraction));

        point.OnCancelExtraction = (Action<ExfiltrationPoint, Player>)Delegate.Remove(
            point.OnCancelExtraction,
            new Action<ExfiltrationPoint, Player>(scenario.CancelExtraction));
        point.OnCancelExtraction = (Action<ExfiltrationPoint, Player>)Delegate.Combine(
            point.OnCancelExtraction,
            new Action<ExfiltrationPoint, Player>(scenario.CancelExtraction));

        point.OnStatusChanged -= scenario.OnStatusChangedHandler;
        point.OnStatusChanged += scenario.OnStatusChangedHandler;

        AppendPointToScenario(scenario, point);
        BindStatusUiHandler(game, point);
        return true;
    }

    private static EndByExitTrigerScenario ResolveEndByExitScenario(AbstractGame game)
    {
        var field = AccessTools.Field(game.GetType(), "_endByExitTrigerScenario");
        if (field?.GetValue(game) is EndByExitTrigerScenario scenarioFromField)
        {
            return scenarioFromField;
        }

        var scenarioFromComponent = game.GetComponent<EndByExitTrigerScenario>();
        if (scenarioFromComponent != null)
        {
            return scenarioFromComponent;
        }

        return game.GetComponents<MonoBehaviour>().OfType<EndByExitTrigerScenario>().FirstOrDefault();
    }

    private static void AppendPointToScenario(EndByExitTrigerScenario scenario, ExfiltrationPoint point)
    {
        var field = AccessTools.Field(typeof(EndByExitTrigerScenario), "_exitTriggers");
        if (field == null)
        {
            return;
        }

        var current = field.GetValue(scenario) as ExfiltrationPoint[];
        if (current == null)
        {
            field.SetValue(scenario, new[] { point });
            return;
        }

        if (current.Contains(point))
        {
            return;
        }

        field.SetValue(scenario, current.Concat(new[] { point }).ToArray());
    }

    private static void TryBindPointToFikaManagerComponent(AbstractGame game, ExfiltrationPoint point)
    {
        var manager = FindFikaExfilManager(game);
        if (manager == null)
        {
            return;
        }

        var method = manager.GetType().GetMethod(
            "UpdateExfilPointFromServer",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(ExfiltrationPoint), typeof(bool) },
            null);

        if (method == null)
        {
            return;
        }

        try
        {
            method.Invoke(manager, new object[] { point, true });
        }
        catch (Exception ex)
        {
            Vagabond.LogError(
                $"Failed to register live exfil '{point.Settings?.Name}' through FikaExfilManager.UpdateExfilPointFromServer: {ex}");
        }
    }

    private static MonoBehaviour FindFikaExfilManager(AbstractGame game)
    {
        return game.GetComponents<MonoBehaviour>()
            .FirstOrDefault(x => x != null && x.GetType().FullName == "Fika.Core.Main.Components.FikaExfilManager");
    }

    private static void AppendPointToFikaManager(AbstractGame game, ExfiltrationPoint point)
    {
        var manager = FindFikaExfilManager(game);
        if (manager == null)
        {
            return;
        }

        var field = AccessTools.Field(manager.GetType(), "_exfiltrationPoints");
        if (field == null)
        {
            return;
        }

        var current = field.GetValue(manager) as ExfiltrationPoint[];
        if (current == null)
        {
            field.SetValue(manager, new[] { point });
            return;
        }

        if (current.Contains(point))
        {
            return;
        }

        field.SetValue(manager, current.Concat(new[] { point }).ToArray());
    }

    private static void BindStatusUiHandler(AbstractGame game, ExfiltrationPoint point)
    {
        var handlerMethod = game.GetType().GetMethod(
                                "OnStatusChangedHandler",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                null,
                                new[] { typeof(ExfiltrationPoint), typeof(EExfiltrationStatus) },
                                null)
                            ??
                            game.GetType().GetMethod(
                                "method_10",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                null,
                                new[] { typeof(ExfiltrationPoint), typeof(EExfiltrationStatus) },
                                null)
                            ??
                            game.GetType().GetMethod(
                                "method_9",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                null,
                                new[] { typeof(ExfiltrationPoint), typeof(EExfiltrationStatus) },
                                null);

        if (handlerMethod == null)
        {
            Vagabond.LogError($"Could not find exfil status UI handler on {game.GetType().FullName}");
            return;
        }

        var handler = Delegate.CreateDelegate(
            typeof(Action<ExfiltrationPoint, EExfiltrationStatus>),
            game,
            handlerMethod,
            false) as Action<ExfiltrationPoint, EExfiltrationStatus>;

        if (handler == null)
        {
            Vagabond.LogError($"Could not bind exfil status UI handler on {game.GetType().FullName}");
            return;
        }

        point.OnStatusChanged -= handler;
        point.OnStatusChanged += handler;
    }

    private static void ReprocessPlayerIfAlreadyInside(Player player, ExfiltrationPoint point)
    {
        if (player == null || point == null)
        {
            return;
        }

        if (!point.Entered.Contains(player))
        {
            return;
        }

        point.Proceed(player);
    }

    private static int GetHandlerCount(Delegate handlers)
    {
        return handlers?.GetInvocationList().Length ?? 0;
    }

    private static void RefreshPlayerInteractions(Player player)
    {
        if (player == null)
        {
            return;
        }

        var owner = player.GetComponent<GamePlayerOwner>();
        if (owner == null)
        {
            return;
        }

        owner.ClearInteractionState();
        try
        {
            owner.InteractionsChangedHandler();
        }
        catch (Exception ex)
        {
            Vagabond.LogError(ex.Message);
        }
    }
}