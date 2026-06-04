using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace LuckyUpgrades;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInProcess("REPO.exe")]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;

    public static Plugin Instance { get; private set; }
    public static UpgradeConfig UpgradeConfiguration { get; private set; }

    // Use int with Interlocked for thread-safe flag (0 = false, 1 = true)
    private static int _isApplyingSharedUpgrade = 0;

    // Lock object for thread-safe Random access
    private static readonly object _randomLock = new object();
    private static readonly System.Random _random = new System.Random();

    internal static string _mySteamID = null;

    // Dictionary to track shared upgrades for reapplication
    internal static readonly Dictionary<string, int> _sharedUpgrades = new Dictionary<string, int>();

    // Registry for modded upgrades registered by other mods
    // Key: upgradeId, Value: (applyAction, shareChance)
    internal static readonly Dictionary<string, (Action<string, int> apply, int chance)> _moddedUpgradeRegistry
        = new Dictionary<string, (Action<string, int>, int)>();

    private Harmony harmony;

    private void Awake()
    {
        Instance = this;
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

        UpgradeConfiguration = new UpgradeConfig(Config);

        harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll(typeof(Plugin));

        // Create a separate GameObject for the Update loop
        var updateRunner = new GameObject("LuckyUpgrades_UpdateRunner");
        updateRunner.AddComponent<UpgradeReapplyRunner>();
        UnityEngine.Object.DontDestroyOnLoad(updateRunner);
        updateRunner.hideFlags = HideFlags.HideAndDontSave;

        Logger.LogInfo("Harmony patches applied!");
    }

    // -------------------------------------------------------------------------
    // Public API for other mods
    // -------------------------------------------------------------------------

    /// <summary>
    /// Register a custom upgrade from another mod so LuckyUpgrades can share it.
    /// Call this from your mod's Awake() after LuckyUpgrades has loaded.
    ///
    /// Example:
    ///   Plugin.RegisterModdedUpgrade(
    ///       "MyMod_NightVision",
    ///       (steamID, amount) => PunManager.instance.MyCustomUpgrade(steamID, amount),
    ///       shareChance: 30
    ///   );
    /// </summary>
    /// <param name="upgradeId">Unique string ID for the upgrade (e.g. "MyMod_NightVision")</param>
    /// <param name="applyAction">Action that applies `amount` stacks of the upgrade to the given steamID</param>
    /// <param name="shareChance">0-100 chance to share. Defaults to 25.</param>
    public static void RegisterModdedUpgrade(
        string upgradeId,
        Action<string, int> applyAction,
        int shareChance = 25)
    {
        if (string.IsNullOrEmpty(upgradeId))
        {
            Logger?.LogError("[LuckyUpgrades] RegisterModdedUpgrade: upgradeId cannot be null or empty.");
            return;
        }
        if (applyAction == null)
        {
            Logger?.LogError($"[LuckyUpgrades] RegisterModdedUpgrade: applyAction cannot be null (upgradeId: {upgradeId}).");
            return;
        }
        shareChance = Math.Max(0, Math.Min(100, shareChance));

        if (_moddedUpgradeRegistry.ContainsKey(upgradeId))
        {
            Logger?.LogWarning($"[LuckyUpgrades] Upgrade '{upgradeId}' already registered — overwriting.");
        }

        // Create a config entry so players can adjust this upgrade's chance in the .cfg file.
        // The entry lives under [ModdedUpgrades] with the upgradeId as the key.
        var configEntry = UpgradeConfiguration?.BindModdedUpgrade(upgradeId, shareChance);
        int resolvedChance = configEntry?.Value ?? shareChance;

        _moddedUpgradeRegistry[upgradeId] = (applyAction, resolvedChance);
        Logger?.LogInfo($"[LuckyUpgrades] Registered modded upgrade: '{upgradeId}' ({resolvedChance}% share chance)");
    }

    /// <summary>
    /// Call this from your mod when a player picks up your custom upgrade,
    /// so LuckyUpgrades can roll to share it with other players.
    ///
    /// Example:
    ///   Plugin.TriggerModdedUpgradeShare("MyMod_NightVision", pickerSteamID, amount: 1);
    /// </summary>
    /// <param name="upgradeId">The ID you used in RegisterModdedUpgrade</param>
    /// <param name="sourceSteamID">SteamID of the player who picked up the upgrade</param>
    /// <param name="amount">How many stacks were applied (usually 1)</param>
    public static void TriggerModdedUpgradeShare(string upgradeId, string sourceSteamID, int amount = 1)
    {
        if (!_moddedUpgradeRegistry.TryGetValue(upgradeId, out var entry))
        {
            Logger?.LogWarning($"[LuckyUpgrades] TriggerModdedUpgradeShare: '{upgradeId}' is not registered. Call RegisterModdedUpgrade first.");
            return;
        }

        ApplySharedUpgradeToSelf(upgradeId, sourceSteamID, amount,
            applyToSelf: (amt) =>
            {
                string myID = GetMySteamID();
                if (!string.IsNullOrEmpty(myID))
                    entry.apply(myID, amt);
            },
            chanceOverride: entry.chance
        );
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Gets the local player's SteamID. Cached after first successful lookup.
    /// </summary>
    internal static string GetMySteamID()
    {
        if (string.IsNullOrEmpty(_mySteamID))
        {
            var localPlayer = SemiFunc.PlayerAvatarLocal();
            if (localPlayer != null)
            {
                _mySteamID = SemiFunc.PlayerGetSteamID(localPlayer);
            }
        }
        return _mySteamID;
    }

    /// <summary>
    /// Reapplies all tracked shared upgrades (both built-in and modded).
    /// Called after a level transition for non-host players.
    /// </summary>
    internal static void ReapplySharedUpgrades()
    {
        if (_sharedUpgrades.Count == 0) return;

        string myID = GetMySteamID();
        if (string.IsNullOrEmpty(myID)) return;

        Logger.LogInfo($"[LuckyUpgrades] Reapplying {_sharedUpgrades.Count} upgrade type(s)...");

        try
        {
            Interlocked.Exchange(ref _isApplyingSharedUpgrade, 1);

            foreach (var upgrade in _sharedUpgrades)
            {
                string upgradeType = upgrade.Key;
                int amount = upgrade.Value;
                if (amount <= 0) continue;

                try
                {
                    bool applied = ReapplySingleUpgrade(myID, upgradeType, amount);
                    if (applied)
                        Logger.LogInfo($"[LuckyUpgrades] Reapplied: {upgradeType} +{amount}");
                    else
                        Logger.LogWarning($"[LuckyUpgrades] Unknown upgrade type during reapply — skipped: '{upgradeType}'");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[LuckyUpgrades] Error reapplying '{upgradeType}': {ex.Message}");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isApplyingSharedUpgrade, 0);
        }
    }

    /// <summary>
    /// Applies a single upgrade type to the given player.
    /// Returns true if the upgrade type was recognised and applied.
    /// </summary>
    private static bool ReapplySingleUpgrade(string myID, string upgradeType, int amount)
    {
        switch (upgradeType)
        {
            case "Health":
                for (int i = 0; i < amount; i++)
                    PunManager.instance.UpgradePlayerHealth(myID, 1);
                return true;
            case "Energy":
                for (int i = 0; i < amount; i++)
                    PunManager.instance.UpgradePlayerEnergy(myID, 1);
                return true;
            case "ExtraJump":
                for (int i = 0; i < amount; i++)
                    PunManager.instance.UpgradePlayerExtraJump(myID, 1);
                return true;
            case "GrabRange":
                for (int i = 0; i < amount; i++)
                    PunManager.instance.UpgradePlayerGrabRange(myID, 1);
                return true;
            case "GrabStrength":
                for (int i = 0; i < amount; i++)
                    PunManager.instance.UpgradePlayerGrabStrength(myID, 1);
                return true;
            case "GrabThrow":
                for (int i = 0; i < amount; i++)
                    PunManager.instance.UpgradePlayerThrowStrength(myID, 1);
                return true;
            case "SprintSpeed":
                for (int i = 0; i < amount; i++)
                    PunManager.instance.UpgradePlayerSprintSpeed(myID, 1);
                return true;
            case "TumbleLaunch":
                for (int i = 0; i < amount; i++)
                    PunManager.instance.UpgradePlayerTumbleLaunch(myID, 1);
                return true;
            case "MapPlayerCount":
                for (int i = 0; i < amount; i++)
                    PunManager.instance.UpgradeMapPlayerCount(myID, 1);
                return true;
            case "TumbleClimb":
                for (int i = 0; i < amount; i++)
                    PunManager.instance.UpgradePlayerTumbleClimb(myID, 1);
                return true;
            case "TumbleWings":
                for (int i = 0; i < amount; i++)
                    PunManager.instance.UpgradePlayerTumbleWings(myID, 1);
                return true;
            case "CrouchRest":
                for (int i = 0; i < amount; i++)
                    PunManager.instance.UpgradePlayerCrouchRest(myID, 1);
                return true;
            case "DeathHeadBattery":
                for (int i = 0; i < amount; i++)
                    PunManager.instance.UpgradeDeathHeadBattery(myID, 1);
                return true;

            default:
                // Try modded upgrade registry
                if (_moddedUpgradeRegistry.TryGetValue(upgradeType, out var moddedEntry))
                {
                    moddedEntry.apply(myID, amount);
                    return true;
                }
                return false;
        }
    }

    /// <summary>
    /// Tracks a shared upgrade for later reapplication on level transition.
    /// </summary>
    private static void TrackSharedUpgrade(string upgradeType, int amount)
    {
        if (!_sharedUpgrades.TryGetValue(upgradeType, out int current))
            current = 0;
        _sharedUpgrades[upgradeType] = current + amount;
        Logger.LogInfo($"[LuckyUpgrades] Tracked: {upgradeType} (total: {_sharedUpgrades[upgradeType]})");
    }

    // -------------------------------------------------------------------------
    // Harmony patches — built-in upgrades
    // -------------------------------------------------------------------------

    [HarmonyPatch(typeof(PunManager), "UpgradePlayerHealth")]
    [HarmonyPostfix]
    public static void UpgradePlayerHealth_Postfix(string _steamID, int value)
    {
        ApplySharedUpgradeToSelf("Health", _steamID, value,
            (amount) => PunManager.instance.UpgradePlayerHealth(GetMySteamID(), amount));
    }

    [HarmonyPatch(typeof(PunManager), "UpgradePlayerEnergy")]
    [HarmonyPostfix]
    public static void UpgradePlayerEnergy_Postfix(string _steamID, int value)
    {
        ApplySharedUpgradeToSelf("Energy", _steamID, value,
            (amount) => PunManager.instance.UpgradePlayerEnergy(GetMySteamID(), amount));
    }

    [HarmonyPatch(typeof(PunManager), "UpgradePlayerExtraJump")]
    [HarmonyPostfix]
    public static void UpgradePlayerExtraJump_Postfix(string _steamID, int value)
    {
        ApplySharedUpgradeToSelf("ExtraJump", _steamID, value,
            (amount) => PunManager.instance.UpgradePlayerExtraJump(GetMySteamID(), amount));
    }

    [HarmonyPatch(typeof(PunManager), "UpgradePlayerGrabRange")]
    [HarmonyPostfix]
    public static void UpgradePlayerGrabRange_Postfix(string _steamID, int value)
    {
        ApplySharedUpgradeToSelf("GrabRange", _steamID, value,
            (amount) => PunManager.instance.UpgradePlayerGrabRange(GetMySteamID(), amount));
    }

    [HarmonyPatch(typeof(PunManager), "UpgradePlayerGrabStrength")]
    [HarmonyPostfix]
    public static void UpgradePlayerGrabStrength_Postfix(string _steamID, int value)
    {
        ApplySharedUpgradeToSelf("GrabStrength", _steamID, value,
            (amount) => PunManager.instance.UpgradePlayerGrabStrength(GetMySteamID(), amount));
    }

    [HarmonyPatch(typeof(PunManager), "UpgradePlayerThrowStrength")]
    [HarmonyPostfix]
    public static void UpgradePlayerThrowStrength_Postfix(string _steamID, int value)
    {
        ApplySharedUpgradeToSelf("GrabThrow", _steamID, value,
            (amount) => PunManager.instance.UpgradePlayerThrowStrength(GetMySteamID(), amount));
    }

    [HarmonyPatch(typeof(PunManager), "UpgradePlayerSprintSpeed")]
    [HarmonyPostfix]
    public static void UpgradePlayerSprintSpeed_Postfix(string _steamID, int value)
    {
        ApplySharedUpgradeToSelf("SprintSpeed", _steamID, value,
            (amount) => PunManager.instance.UpgradePlayerSprintSpeed(GetMySteamID(), amount));
    }

    [HarmonyPatch(typeof(PunManager), "UpgradePlayerTumbleLaunch")]
    [HarmonyPostfix]
    public static void UpgradePlayerTumbleLaunch_Postfix(string _steamID, int value)
    {
        ApplySharedUpgradeToSelf("TumbleLaunch", _steamID, value,
            (amount) => PunManager.instance.UpgradePlayerTumbleLaunch(GetMySteamID(), amount));
    }

    [HarmonyPatch(typeof(PunManager), "UpgradeMapPlayerCount")]
    [HarmonyPostfix]
    public static void UpgradeMapPlayerCount_Postfix(string _steamID, int value)
    {
        ApplySharedUpgradeToSelf("MapPlayerCount", _steamID, value,
            (amount) => PunManager.instance.UpgradeMapPlayerCount(GetMySteamID(), amount));
    }

    [HarmonyPatch(typeof(PunManager), "UpgradePlayerTumbleClimb")]
    [HarmonyPostfix]
    public static void UpgradePlayerTumbleClimb_Postfix(string _steamID, int value)
    {
        ApplySharedUpgradeToSelf("TumbleClimb", _steamID, value,
            (amount) => PunManager.instance.UpgradePlayerTumbleClimb(GetMySteamID(), amount));
    }

    [HarmonyPatch(typeof(PunManager), "UpgradePlayerTumbleWings")]
    [HarmonyPostfix]
    public static void UpgradePlayerTumbleWings_Postfix(string _steamID, int value)
    {
        ApplySharedUpgradeToSelf("TumbleWings", _steamID, value,
            (amount) => PunManager.instance.UpgradePlayerTumbleWings(GetMySteamID(), amount));
    }

    [HarmonyPatch(typeof(PunManager), "UpgradePlayerCrouchRest")]
    [HarmonyPostfix]
    public static void UpgradePlayerCrouchRest_Postfix(string _steamID, int value)
    {
        ApplySharedUpgradeToSelf("CrouchRest", _steamID, value,
            (amount) => PunManager.instance.UpgradePlayerCrouchRest(GetMySteamID(), amount));
    }

    [HarmonyPatch(typeof(PunManager), "UpgradeDeathHeadBattery")]
    [HarmonyPostfix]
    public static void UpgradeDeathHeadBattery_Postfix(string _steamID, int value)
    {
        ApplySharedUpgradeToSelf("DeathHeadBattery", _steamID, value,
            (amount) => PunManager.instance.UpgradeDeathHeadBattery(GetMySteamID(), amount));
    }

    // -------------------------------------------------------------------------
    // Core sharing logic
    // -------------------------------------------------------------------------

    /// <summary>
    /// When another player gets an upgrade, roll to apply it to ourselves.
    /// chanceOverride: if provided, skips the config lookup (used for modded upgrades).
    /// </summary>
    private static void ApplySharedUpgradeToSelf(
        string upgradeType,
        string sourceSteamID,
        int amount,
        Action<int> applyToSelf,
        int? chanceOverride = null)
    {
        try
        {
            // Re-entrant guard: skip if we triggered this call ourselves
            if (Interlocked.CompareExchange(ref _isApplyingSharedUpgrade, 0, 0) == 1) return;

            string mySteamID = GetMySteamID();
            if (string.IsNullOrEmpty(mySteamID)) return;

            // Only react to OTHER players' upgrades
            if (mySteamID == sourceSteamID) return;

            int shareChance = chanceOverride ?? UpgradeConfiguration.GetShareChance(upgradeType);

            int roll;
            lock (_randomLock)
            {
                roll = _random.Next(100);
            }

            if (roll < shareChance)
            {
                try
                {
                    Interlocked.Exchange(ref _isApplyingSharedUpgrade, 1);
                    applyToSelf(amount);
                    TrackSharedUpgrade(upgradeType, amount);
                    Logger.LogInfo($"[LuckyUpgrades] Shared upgrade applied: {upgradeType} +{amount} (rolled: {roll} | chance: {shareChance}%) ✓");
                }
                finally
                {
                    Interlocked.Exchange(ref _isApplyingSharedUpgrade, 0);
                }
            }
            else
            {
                Logger.LogInfo($"[LuckyUpgrades] Shared upgrade missed: {upgradeType} (rolled: {roll} | chance: {shareChance}%) ✗");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[LuckyUpgrades] Error in ApplySharedUpgradeToSelf: {ex.Message}");
        }
    }
}

// =============================================================================

/// <summary>
/// Separate MonoBehaviour to detect level changes and reapply upgrades.
/// </summary>
public class UpgradeReapplyRunner : MonoBehaviour
{
    private static readonly HashSet<string> SESSION_END_LEVELS = new HashSet<string>
    {
        "Level - Main Menu",
        "Level - Lobby Menu"
    };

    private string _lastLevelName = "";
    private float _reapplyDelay = 0f;
    private bool _pendingReapply = false;
    private const float REAPPLY_DELAY_SECONDS = 3f;

    private void Update()
    {
        string currentLevel = "";
        try
        {
            if (RunManager.instance != null && RunManager.instance.levelCurrent != null)
                currentLevel = RunManager.instance.levelCurrent.name;
        }
        catch
        {
            // RunManager not available yet — ignore
        }

        if (!string.IsNullOrEmpty(currentLevel) && currentLevel != _lastLevelName)
        {
            Plugin.Logger.LogInfo($"[LuckyUpgrades] Level changed: {_lastLevelName} -> {currentLevel}");
            _lastLevelName = currentLevel;

            // Returning to main menu or lobby — clear all session data
            if (SESSION_END_LEVELS.Contains(currentLevel))
            {
                Plugin._sharedUpgrades.Clear();
                Plugin._mySteamID = null;
                Plugin.Logger.LogInfo("[LuckyUpgrades] Session ended. All tracked data cleared.");
                return;
            }

            // Entering a game level — schedule reapplication for non-host players
            if (Plugin._sharedUpgrades.Count > 0)
            {
                _pendingReapply = true;
                _reapplyDelay = REAPPLY_DELAY_SECONDS;
                Plugin.Logger.LogInfo($"[LuckyUpgrades] Scheduled reapply in {REAPPLY_DELAY_SECONDS}s...");
            }
        }

        if (_pendingReapply)
        {
            _reapplyDelay -= Time.deltaTime;
            if (_reapplyDelay <= 0f)
            {
                _pendingReapply = false;

                // Host: built-in upgrades persist automatically via Photon, but any
                // upgrades the host RECEIVED as a share still need to be reapplied.
                // We skip only if the host has no tracked shared upgrades (i.e. they
                // were always the source, never the recipient).
                if (PhotonNetwork.IsMasterClient && Plugin._sharedUpgrades.Count == 0)
                {
                    Plugin.Logger.LogInfo("[LuckyUpgrades] Host with no received upgrades — skipping reapply.");
                    return;
                }

                var localPlayer = SemiFunc.PlayerAvatarLocal();
                if (localPlayer == null) return;

                string myID = Plugin.GetMySteamID();
                if (string.IsNullOrEmpty(myID)) return;

                Plugin.ReapplySharedUpgrades();
            }
        }
    }
}