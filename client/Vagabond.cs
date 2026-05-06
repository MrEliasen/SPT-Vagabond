using System;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;
using Vagabond.Client.Patches;
using Vagabond.Client.Services;
using Vagabond.Client.State;
using Vagabond.Common;
using Vagabond.Common.Models;

namespace Vagabond.Client;

[BepInPlugin(VagabondModInfo.Guid, VagabondModInfo.Name, VagabondModInfo.Version)]
[BepInDependency("com.acidphantasm.botplacementsystem", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.fika.headless", BepInDependency.DependencyFlags.SoftDependency)]
public class Vagabond : BaseUnityPlugin
{
    private static ManualLogSource _logger;
    public static VagabondState State { get; private set; } = new();

    private ConfigEntry<KeyboardShortcut> _dumpHotkey = null!;
    private ConfigEntry<KeyboardShortcut> _dumpCustomExtractHotkey = null!;
    private ConfigEntry<KeyboardShortcut> _dumpCustomTransitHotkey = null!;

    private string _locationDumpPath = null!;
    private string _customExtractDumpPath = null!;
    private string _customTransitDumpPath = null!;

    // hideout placement
    private ConfigEntry<KeyboardShortcut> _hideoutHotkey = null!;
    private bool _hideoutPlacementArmed;
    private float _hideoutPlacementArmExpiresAt;
    private bool _hideoutPlacementLoading;

    public static void Log(string message)
    {
        _logger.LogInfo($"[Vagabond] {message}");
    }

    public static void LogError(string message)
    {
        _logger.LogError($"[Vagabond] {message}");
    }

    private void Awake()
    {
        _logger = Logger;
        State = new VagabondState();
        new CustomExfilPlacementPatch().Enable();
        new CustomExfilCleanupPatch().Enable();
        new CustomTransitRetryPatch().Enable();
        new ExfiltrationPointOnTriggerEnterPatch().Enable();
        new ExfiltrationPointOnTriggerExitPatch().Enable();
        new SpawnSystemSelectSpawnPointPatch().Enable();
        new ActiveHealthControllerPatch().Enable();
        new KeepFirStatusPatch().Enable();

        TryEnableAbpsCompatibilityPatches();

        new LocalGameStopPatch().Enable();
        new MenuShowPatch().Enable();

        if (IsHeadless())
        {
            Log($"Loaded in headless mode");
            return;
        }

        new HealthTreatmentScreenShowPatch().Enable();
        new HealthTreatmentScreenAddTreatmentPatch().Enable();
        new MatchMakerSideSelectionScreenPatch().Enable();
        new HideUnavailableTraderCardsPatch().Enable();
        new SelectAvailableTraderPatch().Enable();
        new BlockTraderMailClaimAllPatch().Enable();
        new BlockTraderMailClaimGetPatch().Enable();
        new TransitInteractionPatch().Enable();
        new TransitInteractionLabelPatch().Enable();
        new TransitCommitPatch().Enable();
        new SkipInsuranceFlowPatch().Enable();
        UIMessageService.Create(transform);

        _hideoutHotkey = Config.Bind(
            "For Players",
            "Place Hideout Entrance",
            new KeyboardShortcut(KeyCode.P, KeyCode.LeftControl),
            "Press in raid to place the entrance to your hideout at your current location."
        );

        _dumpHotkey = Config.Bind(
            "For Modders",
            "Dump Current Location",
            new KeyboardShortcut(KeyCode.F8),
            "Press in raid to dump current map, position and yaw to file in the Vagabond mod dir."
        );

        _dumpCustomExtractHotkey = Config.Bind(
            "For Modders",
            "Dump Extraction From Current Location",
            new KeyboardShortcut(KeyCode.F9),
            "Press in raid to dump a copy/paste ready custom extract snippet using the current player position, to a file in the Vagabond mod dir"
        );

        _dumpCustomTransitHotkey = Config.Bind(
            "For Modders",
            "Dump Transit From Current Location",
            new KeyboardShortcut(KeyCode.F10),
            "Press in raid to dump a copy/paste ready custom transit snippet using the current player position, to a file in the Vagabond mod dir"
        );

        var pluginDir = Path.GetDirectoryName(Info.Location)!;
        _locationDumpPath = Path.Combine(pluginDir, "dumped_locations.txt");
        _customExtractDumpPath = Path.Combine(pluginDir, "dumped_custom_extracts.txt");
        _customTransitDumpPath = Path.Combine(pluginDir, "dumped_custom_transits.txt");

        Log("loaded");
    }

    private void Update()
    {
        RaidService.HandleExfilUpdatePolling();

        if (IsHeadless())
        {
            return;
        }

        if (_hideoutPlacementArmed && Time.realtimeSinceStartup > _hideoutPlacementArmExpiresAt)
        {
            _hideoutPlacementArmed = false;
        }

        if (_hideoutHotkey.Value.IsDown())
        {
            PromptCreateHideoutExtract();
        }

        if (_dumpHotkey.Value.IsDown())
        {
            DumpCurrentLocation();
        }

        if (_dumpCustomExtractHotkey.Value.IsDown())
        {
            DumpCustomExtractDefinition();
        }

        if (_dumpCustomTransitHotkey.Value.IsDown())
        {
            DumpCustomTransitDefinition();
        }
    }

    private void PromptCreateHideoutExtract()
    {
        if (_hideoutPlacementLoading)
        {
            NotificationManagerClass.DisplayWarningNotification("Hideout placement request already in progress.");
            return;
        }

        if (!_hideoutPlacementArmed)
        {
            if (!TryGetCurrentSnapshot(out _))
            {
                NotificationManagerClass.DisplayWarningNotification("You must be in raid.");
                return;
            }

            _hideoutPlacementArmed = true;
            _hideoutPlacementArmExpiresAt = Time.realtimeSinceStartup + 10f;

            NotificationManagerClass.DisplayWarningNotification(
                "Press the hotkey again within 10 seconds to place your hideout at your current position."
            );
            return;
        }

        _hideoutPlacementArmed = false;
        _ = TryCreateHideoutExtractAsync();
    }

    private async Task TryCreateHideoutExtractAsync()
    {
        try
        {
            if (!TryGetCurrentSnapshot(out var snapshot))
            {
                NotificationManagerClass.DisplayWarningNotification("You must be in raid.");
                return;
            }

            _hideoutPlacementLoading = true;

            var resp = await Networking.ApiClient.EstablishHideoutExtract(new PlaceHideoutRequest
            {
                X = snapshot.Position.x,
                Y = snapshot.Position.y,
                Z = snapshot.Position.z,
                R = snapshot.Yaw,
                LocationId = Singleton<GameWorld>.Instance?.LocationId,
            });

            if (!resp.Success)
            {
                NotificationManagerClass.DisplayWarningNotification(resp.Message);
                return;
            }

            NotificationManagerClass.DisplayMessageNotification(resp.Message);
        }
        catch (Exception ex)
        {
            LogError($"Establish hideout request failed: {ex}");
            NotificationManagerClass.DisplayWarningNotification("Establish hideout request failed.");
        }
        finally
        {
            _hideoutPlacementLoading = false;
        }
    }

    private void DumpCurrentLocation()
    {
        try
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            var player = gameWorld?.MainPlayer;

            if (player == null)
            {
                return;
            }

            var pos = player.Position;
            var yaw = player.Transform.eulerAngles.y;
            var csharpLine = string.Format(
                "{",
                $"    \"x\": {pos.x:0.###},",
                $"    \"y\": {pos.y:0.###},",
                $"    \"z\": {pos.z:0.###},",
                $"    \"rotationY\": {yaw:0.###},",
                "},"
            );
            File.AppendAllText(_locationDumpPath, csharpLine + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to dump location: {ex}");
        }
    }

    public static bool IsHeadless()
    {
        return Application.isBatchMode
               || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
    }

    private static void TryEnableAbpsCompatibilityPatches()
    {
        if (!Chainloader.PluginInfos.ContainsKey("com.acidphantasm.botplacementsystem"))
        {
            return;
        }

        try
        {
            new ABPSPmcDistancePatch().Enable();
            new ABPSScavDistancePatch().Enable();
        }
        catch (Exception ex)
        {
            LogError(
                $"Failed to enable ABPS compatibility patches, your ABPS version is not supported - Skipping - failed patch: {ex.Message}");
        }
    }

    private void DumpCustomExtractDefinition()
    {
        try
        {
            if (!TryGetCurrentSnapshot(out var snapshot))
            {
                return;
            }

            File.AppendAllText(_customExtractDumpPath, string.Join(Environment.NewLine, new[]
            {
                "{",
                "    \"identifier\": \"VGB_EXT_\",",
                "    \"displayName\": \"Human Readable Label\",",
                "    \"exfiltrationTime\": 20,",
                $"    \"x\": {snapshot.Position.x:0.###},",
                $"    \"y\": {snapshot.Position.y:0.###},",
                $"    \"z\": {snapshot.Position.z:0.###},",
                $"    \"rotationY\": {snapshot.Yaw:0.###}",
                "},"
            }) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to dump custom extract snippet: {ex}");
        }
    }

    private void DumpCustomTransitDefinition()
    {
        try
        {
            if (!TryGetCurrentSnapshot(out var snapshot))
            {
                return;
            }

            File.AppendAllText(_customTransitDumpPath, string.Join(Environment.NewLine, new[]
            {
                "{",
                "    \"identifier\": \"VGB_\",",
                "    \"destinationLocation\": \"DESTINATION_MAP_NAME\",",
                "    \"description\": \"Transit to ..\",",
                "    \"exfiltrationTime\": 15,",
                $"    \"x\": {snapshot.Position.x:0.###},",
                $"    \"y\": {snapshot.Position.y:0.###},",
                $"    \"z\": {snapshot.Position.z:0.###},",
                $"    \"rotationY\": {snapshot.Yaw:0.###},",
                "    \"connectedIdentifier\": \"VGB_\"",
                "},"
            }) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to dump custom transit snippet: {ex}");
        }
    }

    private static bool TryGetCurrentSnapshot(out LocationSnapshot snapshot)
    {
        snapshot = default;

        var gameWorld = Singleton<GameWorld>.Instance;
        var player = gameWorld?.MainPlayer;

        if (player == null)
        {
            return false;
        }

        snapshot = new LocationSnapshot(
            player.Position,
            player.Transform.eulerAngles.y
        );
        return true;
    }

    private struct LocationSnapshot
    {
        public Vector3 Position;
        public float Yaw;

        public LocationSnapshot(Vector3 position, float yaw)
        {
            Position = position;
            Yaw = yaw;
        }
    }
}