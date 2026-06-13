using System;
using System.Collections;
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

    // Thread-safe re-entrancy guard (0 = not applying, 1 = applying)
    private static int _isApplyingSharedUpgrade = 0;

    // Additional per-type guard: prevents the rare duplicate where PunManager's
    // network sync re-triggers ItemUpgrade.PlayerUpgrade for the same upgrade type
    // on the same client within a single frame, before _isApplyingSharedUpgrade is set.
    private static readonly HashSet<string> _inFlightUpgrades = new HashSet<string>();
    private static readonly object _inFlightLock = new object();

    private static readonly object _randomLock = new object();
    private static readonly System.Random _random = new System.Random();

    // ── QoL: per-instance cooldown ───────────────────────────────────────────
    // Keyed by the GameObject's instance ID, NOT the upgrade type.
    // This means two different Health upgrades picked up quickly always both roll —
    // only the exact same item firing twice (physics jitter / double-trigger) is suppressed.
    private static readonly Dictionary<int, float> _lastTriggerTime = new Dictionary<int, float>();
    private static readonly object _cooldownLock = new object();

    // ── QoL: streak tracking ─────────────────────────────────────────────────
    private static int _currentStreak = 0;   // positive = win streak, negative = lose streak
    private static readonly object _streakLock = new object();

    // ── QoL: session upgrade counter ─────────────────────────────────────────
    // How many upgrades this run were shared TO this player (won rolls only)
    private static int _sessionUpgradesReceived = 0;
    private static int _sessionRollsTotal = 0;
    private static readonly object _sessionLock = new object();

    // THREAD-SAFETY FIX: Make locks INTERNAL so UpgradeReapplyRunner can access them
    internal static readonly object SharedUpgradesLock = new object();
    internal static readonly object ModdedUpgradeRegistryLock = new object();
    internal static readonly object MySteamIDLock = new object();

    internal static string _mySteamID = null;

    // Tracks shared upgrades for reapplication on level transition
    internal static readonly Dictionary<string, int> _sharedUpgrades = new Dictionary<string, int>();

    // Registry for modded upgrades registered by other mods.
    // getChance is a live delegate so it always reads the current config value,
    // never a stale snapshot baked in at registration time.
    internal static readonly Dictionary<string, (Action<string, int> apply, Func<int> getChance)> _moddedUpgradeRegistry
        = new Dictionary<string, (Action<string, int>, Func<int>)>();

    private Harmony _harmony;

    private void Awake()
    {
        Instance = this;
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

        UpgradeConfiguration = new UpgradeConfig(Config);

        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        // Discover and patch the upgrade-trigger method on ItemUpgrade by reflection.
        // The method name has changed across game versions, so instead of hardcoding it
        // we: (1) try known names, (2) fall back to scanning for a public void no-arg method
        // that isn't inherited from MonoBehaviour/Component/Object/Behaviour.
        var postfix = new HarmonyMethod(typeof(Plugin),
            nameof(ItemUpgrade_PlayUpgrade_Postfix));
        bool patched = false;

        // Unity lifecycle / toggle methods that must never be patched.
        var skipMethods = new HashSet<string>
        {
            "Start", "Awake", "Update", "FixedUpdate", "LateUpdate",
            "OnEnable", "OnDisable", "OnDestroy", "Reset",
            "ButtonToggle", "ButtonToggleLogic", "ButtonToggleRPC"
        };

        // 1. Try known names first (fastest path, no false positives).
        //    "PlayerUpgrade" is the confirmed name in v0.4.4.3.
        foreach (var methodName in new[] { "PlayerUpgrade", "PlayUpgrade", "Use", "Upgrade", "Activate", "OnUse", "UseUpgrade", "Apply" })
        {
            var target = AccessTools.DeclaredMethod(typeof(ItemUpgrade), methodName, new System.Type[0]);
            if (target != null)
            {
                _harmony.Patch(target, postfix: postfix);
                Logger.LogInfo($"[LuckyUpgrades] Patched ItemUpgrade.{methodName} ✓");
                patched = true;
                break;
            }
        }

        // 2. Fallback: scan for a void no-arg method that isn't a Unity lifecycle or toggle method.
        if (!patched)
        {
            foreach (var m in typeof(ItemUpgrade).GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (m.ReturnType == typeof(void) && m.GetParameters().Length == 0
                    && !m.IsAbstract && !skipMethods.Contains(m.Name))
                {
                    _harmony.Patch(m, postfix: postfix);
                    Logger.LogWarning($"[LuckyUpgrades] Fallback-patched ItemUpgrade.{m.Name} — verify this is the upgrade trigger!");
                    patched = true;
                    break;
                }
            }
        }

        if (!patched)
            Logger.LogError("[LuckyUpgrades] Could not find any suitable method on ItemUpgrade to patch — sharing will not work! Check the method list above.");

        var updateRunner = new GameObject("LuckyUpgrades_UpdateRunner");
        updateRunner.AddComponent<UpgradeReapplyRunner>();
        UnityEngine.Object.DontDestroyOnLoad(updateRunner);
        updateRunner.hideFlags = HideFlags.HideAndDontSave;

        // Create the Minecraft-style toast notification system
        var toastObj = new GameObject("LuckyUpgrades_ToastUI");
        toastObj.AddComponent<UpgradeToastUI>();
        UnityEngine.Object.DontDestroyOnLoad(toastObj);
        toastObj.hideFlags = HideFlags.HideAndDontSave;

        // Eagerly bind config entries for every upgrade already registered in REPOLib
        // so they appear in the .cfg file from the very first launch and survive restarts.
        PreBindREPOLibUpgrades();

        Logger.LogInfo("Harmony patches applied!");
    }

    // =========================================================================
    // Public API for other mods
    // =========================================================================

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

        lock (ModdedUpgradeRegistryLock)
        {
            if (_moddedUpgradeRegistry.ContainsKey(upgradeId))
                Logger?.LogWarning($"[LuckyUpgrades] Upgrade '{upgradeId}' already registered — overwriting.");

            var configEntry = UpgradeConfiguration?.BindModdedUpgrade(upgradeId, shareChance);
            // Store a live delegate so every roll reads the current config value,
            // not the value that happened to be set at registration time.
            Func<int> getChance = configEntry != null
                ? (Func<int>)(() => configEntry.Value)
                : (Func<int>)(() => shareChance);

            _moddedUpgradeRegistry[upgradeId] = (applyAction, getChance);
            Logger?.LogInfo($"[LuckyUpgrades] ✓ REGISTERED modded upgrade: '{upgradeId}' ({getChance()}% share chance)");
        }
    }

    /// <summary>
    /// Call this from your mod when a player picks up your custom upgrade.
    ///
    /// Example:
    ///   Plugin.TriggerModdedUpgradeShare("MyMod_NightVision", pickerSteamID, amount: 1);
    /// </summary>
    public static void TriggerModdedUpgradeShare(string upgradeId, string sourceSteamID, int amount = 1)
    {
        // Capture the delegates inside the lock so a concurrent RegisterModdedUpgrade()
        // call cannot swap the entry out from under us between the TryGetValue and the
        // actual invocation (TOCTOU race on the registry dictionary).
        Action<string, int> applyDelegate;
        int chance;

        lock (ModdedUpgradeRegistryLock)
        {
            if (!_moddedUpgradeRegistry.TryGetValue(upgradeId, out var entry))
            {
                Logger?.LogError($"[LuckyUpgrades] ✗ TriggerModdedUpgradeShare: '{upgradeId}' NOT REGISTERED! Did you call RegisterModdedUpgrade()?");
                return;
            }
            applyDelegate = entry.apply;
            chance        = entry.getChance();
        }

        Logger?.LogInfo($"[LuckyUpgrades] → TriggerModdedUpgradeShare called: '{upgradeId}' from {sourceSteamID}");

        // Resolve display name for the picker
        string sourcePlayerName = GetPlayerName(sourceSteamID);

        ApplySharedUpgradeToSelf(upgradeId, sourceSteamID, sourcePlayerName, amount,
            applyToSelf: (amt) =>
            {
                string myID = GetMySteamID();
                if (!string.IsNullOrEmpty(myID))
                {
                    Logger?.LogInfo($"[LuckyUpgrades] → Applying modded upgrade '{upgradeId}' to player {myID} (+{amt})");
                    applyDelegate(myID, amt);
                }
                else
                {
                    Logger?.LogWarning($"[LuckyUpgrades] ✗ Cannot apply '{upgradeId}': SteamID is null/empty");
                }
            },
            chanceOverride: chance);
    }

    // =========================================================================
    // Internal helpers
    // =========================================================================

    internal static string GetMySteamID()
    {
        lock (MySteamIDLock)
        {
            if (string.IsNullOrEmpty(_mySteamID))
            {
                var localPlayer = SemiFunc.PlayerAvatarLocal();
                if (localPlayer != null)
                {
                    _mySteamID = SemiFunc.PlayerGetSteamID(localPlayer);
                    if (!string.IsNullOrEmpty(_mySteamID))
                        Logger?.LogDebug($"[LuckyUpgrades] Player SteamID cached: {_mySteamID}");
                    else
                        Logger?.LogWarning("[LuckyUpgrades] Failed to get player SteamID from local player");
                }
                else
                {
                    Logger?.LogDebug("[LuckyUpgrades] Local player not found yet");
                }
            }
            return _mySteamID;
        }
    }

    internal static void ReapplySharedUpgrades()
    {
        Dictionary<string, int> upgradesToReapply;

        lock (SharedUpgradesLock)
        {
            if (_sharedUpgrades.Count == 0) return;
            // Snapshot and immediately clear so the counts start fresh from this level.
            // Without clearing, each level transition would compound the totals:
            // e.g. Investor tracked as 1 after level 1, re-applied as +1 on level 2,
            // then if another Investor is shared it becomes tracked as 2, re-applied
            // as +2 on level 3 (on top of the +1 already applied), and so on.
            upgradesToReapply = new Dictionary<string, int>(_sharedUpgrades);
            _sharedUpgrades.Clear();
        }

        string myID = GetMySteamID();
        if (string.IsNullOrEmpty(myID)) return;

        Logger.LogInfo($"[LuckyUpgrades] Reapplying {upgradesToReapply.Count} upgrade type(s)...");

        try
        {
            Interlocked.Exchange(ref _isApplyingSharedUpgrade, 1);

            foreach (var upgrade in upgradesToReapply)
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

    private static bool ReapplySingleUpgrade(string myID, string upgradeType, int amount)
    {
        switch (upgradeType)
        {
            case "Health":
                for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerHealth(myID, 1);
                return true;
            case "Energy":
                for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerEnergy(myID, 1);
                return true;
            case "ExtraJump":
                for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerExtraJump(myID, 1);
                return true;
            case "GrabRange":
                for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerGrabRange(myID, 1);
                return true;
            case "GrabStrength":
                for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerGrabStrength(myID, 1);
                return true;
            case "GrabThrow":
                for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerThrowStrength(myID, 1);
                return true;
            case "SprintSpeed":
                for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerSprintSpeed(myID, 1);
                return true;
            case "TumbleLaunch":
                for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerTumbleLaunch(myID, 1);
                return true;
            case "MapPlayerCount":
                for (int i = 0; i < amount; i++) PunManager.instance.UpgradeMapPlayerCount(myID, 1);
                return true;
            case "TumbleClimb":
                for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerTumbleClimb(myID, 1);
                return true;
            case "TumbleWings":
                for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerTumbleWings(myID, 1);
                return true;
            case "CrouchRest":
                for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerCrouchRest(myID, 1);
                return true;
            case "DeathHeadBattery":
                for (int i = 0; i < amount; i++) PunManager.instance.UpgradeDeathHeadBattery(myID, 1);
                return true;
            default:
                lock (ModdedUpgradeRegistryLock)
                {
                    if (_moddedUpgradeRegistry.TryGetValue(upgradeType, out var moddedEntry))
                    {
                        moddedEntry.apply(myID, amount);
                        return true;
                    }
                }
                return false;
        }
    }

    private static void TrackSharedUpgrade(string upgradeType, int amount)
    {
        lock (SharedUpgradesLock)
        {
            if (!_sharedUpgrades.TryGetValue(upgradeType, out int current))
                current = 0;
            _sharedUpgrades[upgradeType] = current + amount;
            Logger.LogInfo($"[LuckyUpgrades] Tracked: {upgradeType} (total: {_sharedUpgrades[upgradeType]})");
        }
    }

    // =========================================================================
    // Harmony patch — single patch on ItemUpgrade.PlayUpgrade()
    //
    // WHY: PunManager.Upgrade* only runs on the host. ItemUpgrade.PlayUpgrade()
    // fires on EVERY client when an upgrade is used, so we intercept here instead.
    // We map the component type on the GameObject to our internal upgrade key.
    // =========================================================================

    // NOTE: The method was renamed in a game update. We patch manually in Awake()
    // to try "PlayUpgrade" first and fall back to "Use" so the patch never silently no-ops.
    // This method is registered as the postfix target via _harmony.Patch() below.
    public static void ItemUpgrade_PlayUpgrade_Postfix(ItemUpgrade __instance)
    {
        try
        {
            // Re-entrancy guard: skip if the upgrade we're about to fire was
            // triggered by LuckyUpgrades itself (shared-upgrade apply or reapply).
            if (Interlocked.CompareExchange(ref _isApplyingSharedUpgrade, 0, 0) == 1)
                return;

            string mySteamID = GetMySteamID();

            // Identify upgrade type
            string upgradeType = GetUpgradeType(__instance);
            if (string.IsNullOrEmpty(upgradeType)) return;

            // Get source SteamID and resolve their display name
            string sourceSteamID = GetSteamIDFromItem(__instance);
            if (string.IsNullOrEmpty(sourceSteamID)) return;
            if (string.IsNullOrEmpty(mySteamID)) return;

            // ── QoL: per-instance cooldown ───────────────────────────────────
            // Key = the specific item's instance ID, so picking up two Health upgrades
            // back-to-back always fires two independent rolls. Only the exact same
            // GameObject re-triggering within the cooldown window is suppressed.
            float cooldown = UpgradeConfiguration?.UpgradeCooldownSeconds.Value ?? 1.5f;
            if (cooldown > 0f)
            {
                int instanceId = __instance.gameObject.GetInstanceID();
                float now = Time.realtimeSinceStartup;
                lock (_cooldownLock)
                {
                    if (_lastTriggerTime.TryGetValue(instanceId, out float last) && (now - last) < cooldown)
                    {
                        Logger.LogInfo($"[LuckyUpgrades] Instance cooldown active for '{upgradeType}' (id:{instanceId}) — skipping ({now - last:F2}s < {cooldown}s)");
                        return;
                    }
                    _lastTriggerTime[instanceId] = now;
                }
            }

            // ── QoL: picker toast ─────────────────────────────────────────────
            // Show a toast to the player who picked up the upgrade so they know sharing is happening
            if (mySteamID == sourceSteamID)
            {
                if (UpgradeConfiguration?.ShowPickerToast.Value ?? true)
                    UpgradeToastUI.ShowPickerToast(upgradeType);
                return; // pickers never roll for themselves
            }

            // Resolve source player display name for the roll toast
            string sourcePlayerName = GetPlayerName(sourceSteamID);

            // Look up the chance from the registry for REPOLib/modded upgrades
            int? chanceOverride = null;
            lock (ModdedUpgradeRegistryLock)
            {
                if (_moddedUpgradeRegistry.TryGetValue(upgradeType, out var registryEntry))
                    chanceOverride = registryEntry.getChance();
            }

            ApplySharedUpgradeToSelf(upgradeType, sourceSteamID, sourcePlayerName, 1,
                applyToSelf: (_) =>
                {
                    string myID = GetMySteamID();
                    if (!string.IsNullOrEmpty(myID))
                        ApplyUpgradeByType(upgradeType, myID);
                },
                chanceOverride: chanceOverride);
        }
        catch (Exception ex)
        {
            Logger.LogError($"[LuckyUpgrades] Error in ItemUpgrade_PlayUpgrade_Postfix: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Attempts to get the in-game display name for a player by their SteamID.
    /// Falls back to a shortened SteamID if the name cannot be resolved.
    /// </summary>
    private static string GetPlayerName(string steamID)
    {
        if (string.IsNullOrEmpty(steamID)) return "???";
        try
        {
            // Iterate all PlayerAvatar instances and match by SteamID
            foreach (var avatar in UnityEngine.Object.FindObjectsOfType<PlayerAvatar>())
            {
                try
                {
                    string id = SemiFunc.PlayerGetSteamID(avatar);
                    if (id == steamID)
                    {
                        string name = SemiFunc.PlayerGetName(avatar);
                        if (!string.IsNullOrEmpty(name)) return name;
                    }
                }
                catch { /* avatar may not be fully initialised */ }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[LuckyUpgrades] GetPlayerName failed: {ex.Message}");
        }
        // Fallback: last 6 chars of SteamID so it's still useful
        return steamID.Length > 6 ? "..." + steamID.Substring(steamID.Length - 6) : steamID;
    }

    // Cache for REPOLib type detection.
    // null  = not yet searched (or search should be retried)
    // false = searched and not found
    // true  = found (type is in _repoLibItemUpgradeType)
    private static bool? _repoLibTypeSearched = null;
    private static System.Type _repoLibItemUpgradeType = null;
    private static System.Reflection.FieldInfo _repoLibUpgradeIdField = null;

    /// <summary>
    /// Maps the concrete component type on the GameObject to our internal upgrade key string.
    /// Returns null if the type is not a recognised player upgrade.
    /// </summary>
    private static string GetUpgradeType(ItemUpgrade item)
    {
        var go = item.gameObject;
        if      (go.GetComponent<ItemUpgradePlayerHealth>()      != null) return "Health";
        else if (go.GetComponent<ItemUpgradePlayerEnergy>()      != null) return "Energy";
        else if (go.GetComponent<ItemUpgradePlayerExtraJump>()   != null) return "ExtraJump";
        else if (go.GetComponent<ItemUpgradePlayerGrabRange>()   != null) return "GrabRange";
        else if (go.GetComponent<ItemUpgradePlayerGrabStrength>()!= null) return "GrabStrength";
        else if (go.GetComponent<ItemUpgradePlayerGrabThrow>()   != null) return "GrabThrow";
        else if (go.GetComponent<ItemUpgradePlayerSprintSpeed>() != null) return "SprintSpeed";
        else if (go.GetComponent<ItemUpgradePlayerTumbleLaunch>()!= null) return "TumbleLaunch";
        else if (go.GetComponent<ItemUpgradePlayerTumbleClimb>() != null) return "TumbleClimb";
        else if (go.GetComponent<ItemUpgradePlayerTumbleWings>() != null) return "TumbleWings";
        else if (go.GetComponent<ItemUpgradePlayerCrouchRest>()  != null) return "CrouchRest";
        else if (go.GetComponent<ItemUpgradeDeathHeadBattery>()  != null) return "DeathHeadBattery";
        else if (go.GetComponent<ItemUpgradeMapPlayerCount>()    != null) return "MapPlayerCount";

        // --- REPOLib detection ---
        return TryGetREPOLibUpgradeId(go);
    }

    private static string TryGetREPOLibUpgradeId(GameObject go)
    {
        try
        {
            // Retry the type search if we haven't found it yet.
            // REPOLib may load after LuckyUpgrades, so we keep trying until found.
            if (_repoLibTypeSearched != true)
            {
                _repoLibTypeSearched = false; // mark as "searched this attempt"

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name == "REPOLibItemUpgrade")
                            {
                                _repoLibItemUpgradeType = t;
                                break;
                            }
                        }
                    }
                    catch { /* some assemblies throw on GetTypes() */ }
                    if (_repoLibItemUpgradeType != null) break;
                }

                if (_repoLibItemUpgradeType != null)
                {
                    Logger.LogInfo($"[LuckyUpgrades] Found REPOLibItemUpgrade — resolving upgradeId field...");
                    // Find the upgradeId field
                    _repoLibUpgradeIdField =
                        _repoLibItemUpgradeType.GetField("upgradeId",  System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) ??
                        _repoLibItemUpgradeType.GetField("_upgradeId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) ??
                        _repoLibItemUpgradeType.GetField("UpgradeId",  System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    // Type found — stop retrying on every call
                    _repoLibTypeSearched = true;
                }
            }

            if (_repoLibItemUpgradeType == null) return null;

            var comp = go.GetComponent(_repoLibItemUpgradeType);
            if (comp == null) return null;

            if (_repoLibUpgradeIdField == null)
            {
                Logger.LogWarning("[LuckyUpgrades] upgradeId field not resolved on REPOLibItemUpgrade — cannot share this upgrade type.");
                return null;
            }

            string upgradeId = _repoLibUpgradeIdField.GetValue(comp) as string;

            if (string.IsNullOrEmpty(upgradeId)) return null;

            // Auto-register on first encounter
            lock (ModdedUpgradeRegistryLock)
            {
                if (!_moddedUpgradeRegistry.ContainsKey(upgradeId))
                {
                    // Use DefaultModdedUpgradeChance as the default so changing it in the
                    // config actually affects auto-registered REPOLib upgrades.
                    int defaultChance = UpgradeConfiguration?.DefaultModdedUpgradeChance.Value ?? 25;
                    var configEntry = UpgradeConfiguration?.BindModdedUpgrade(upgradeId, defaultChance);
                    // Live delegate — reads the config value on every roll, never stale
                    Func<int> getChance = configEntry != null
                        ? (Func<int>)(() => configEntry.Value)
                        : (Func<int>)(() => UpgradeConfiguration?.DefaultModdedUpgradeChance.Value ?? 25);
                    string capturedId = upgradeId;
                    _moddedUpgradeRegistry[capturedId] = (
                        (steamID, amount) => ApplyREPOLibUpgrade(capturedId, steamID, amount),
                        getChance
                    );
                    Logger.LogInfo($"[LuckyUpgrades] Auto-registered REPOLib upgrade: '{capturedId}' ({getChance()}%)");
                }
            }
            return upgradeId;
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[LuckyUpgrades] TryGetREPOLibUpgradeId failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Scans REPOLib.Modules.Upgrades at startup and pre-binds a config entry for every
    /// registered upgrade. This ensures entries appear in the .cfg immediately on first
    /// launch and are correctly restored on subsequent launches.
    /// </summary>
    private static void PreBindREPOLibUpgrades()
    {
        // IMPORTANT: We must extract IDs using the _upgradeId FIELD on each PlayerUpgrade object,
        // NOT the dictionary keys. TryGetREPOLibUpgradeId reads _upgradeId at runtime, so both
        // code paths must use the same source to produce matching registry keys.
        try
        {
            // Find REPOLib.Modules.Upgrades type
            System.Type upgradesType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { upgradesType = asm.GetType("REPOLib.Modules.Upgrades"); }
                catch { }
                if (upgradesType != null) break;
            }

            if (upgradesType == null)
            {
                Logger.LogInfo("[LuckyUpgrades] REPOLib.Modules.Upgrades not found at startup — will bind on first encounter instead.");
                return;
            }

            // Collect all PlayerUpgrade objects from REPOLib (via GetAll, GetUpgrades, or dict values)
            var upgradeObjects = new List<object>();

            foreach (var methodName in new[] { "GetAll", "GetUpgrades", "GetRegistered" })
            {
                var m = upgradesType.GetMethod(methodName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (m == null) continue;
                var result = m.Invoke(null, null);
                if (result is System.Collections.IEnumerable objEnum)
                {
                    foreach (var obj in objEnum)
                        if (obj != null) upgradeObjects.Add(obj);
                }
                if (upgradeObjects.Count > 0) break;
            }

            // Fallback: dictionary values
            if (upgradeObjects.Count == 0)
            {
                foreach (var field in upgradesType.GetFields(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
                {
                    var val = field.GetValue(null);
                    if (val is System.Collections.IDictionary dict)
                    {
                        foreach (var v in dict.Values)
                            if (v != null) upgradeObjects.Add(v);
                        if (upgradeObjects.Count > 0) break;
                    }
                }
            }

            if (upgradeObjects.Count == 0)
            {
                Logger.LogInfo("[LuckyUpgrades] Could not enumerate REPOLib upgrades at startup — will bind on first encounter.");
                return;
            }

            // Extract ID using the same field that TryGetREPOLibUpgradeId uses at runtime:
            // _upgradeId (private backing field) or UpgradeId (public property).
            // This guarantees the registry key matches what the postfix produces.
            int defaultChance = UpgradeConfiguration?.DefaultModdedUpgradeChance.Value ?? 25;
            int count = 0;
            foreach (var obj in upgradeObjects)
            {
                System.Type objType = obj.GetType();

                var idField =
                    objType.GetField("_upgradeId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) ??
                    objType.GetField("upgradeId",  System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) ??
                    objType.GetField("UpgradeId",  System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                var idProp =
                    objType.GetProperty("UpgradeId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) ??
                    objType.GetProperty("upgradeId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                // Field takes priority (matches TryGetREPOLibUpgradeId which reads _upgradeId field)
                string id = (idField?.GetValue(obj) ?? idProp?.GetValue(obj)) as string;

                Logger.LogInfo($"[LuckyUpgrades] Pre-bind: found REPOLib upgrade id='{id ?? "NULL"}'");
                if (string.IsNullOrEmpty(id)) continue;

                lock (ModdedUpgradeRegistryLock)
                {
                    if (!_moddedUpgradeRegistry.ContainsKey(id))
                    {
                        var configEntry = UpgradeConfiguration?.BindModdedUpgrade(id, defaultChance);
                        // Live delegate — reads config on every roll, never a stale snapshot
                        Func<int> getChance = configEntry != null
                            ? (Func<int>)(() => configEntry.Value)
                            : (Func<int>)(() => UpgradeConfiguration?.DefaultModdedUpgradeChance.Value ?? 25);
                        string capturedId = id;
                        _moddedUpgradeRegistry[capturedId] = (
                            (steamID, amount) => ApplyREPOLibUpgrade(capturedId, steamID, amount),
                            getChance
                        );
                        count++;
                        Logger.LogInfo($"[LuckyUpgrades] Pre-bound '{capturedId}' ({getChance()}%) to config");
                    }
                }
            }
            Logger.LogInfo($"[LuckyUpgrades] Pre-bound {count} REPOLib upgrade(s) to config ✓");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[LuckyUpgrades] PreBindREPOLibUpgrades failed: {ex.Message}");
        }
    }

    // Cached reflection results for ApplyREPOLibUpgrade — resolved once, reused forever.
    private static System.Type _repoLibUpgradesModuleType = null;
    private static System.Reflection.MethodInfo _repoLibGetUpgradeMethod = null;

    // FIX: Use AddLevel(PlayerAvatar, int) as the primary apply method for REPOLib upgrades.
    private static void ApplyREPOLibUpgrade(string upgradeId, string steamID, int amount)
    {
        try
        {
            PlayerAvatar target = null;
            foreach (var p in SemiFunc.PlayerGetAll())
            {
                if (SemiFunc.PlayerGetSteamID(p) == steamID) { target = p; break; }
            }

            if (target == null)
            {
                Logger.LogWarning($"[LuckyUpgrades] ApplyREPOLibUpgrade: no PlayerAvatar for steamID '{steamID}'");
                return;
            }

            // Resolve and cache on first call — previously did a full assembly scan on
            // every single apply, causing hitches when reapplying multiple upgrades on load.
            if (_repoLibUpgradesModuleType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { _repoLibUpgradesModuleType = asm.GetType("REPOLib.Modules.Upgrades"); }
                    catch { }
                    if (_repoLibUpgradesModuleType != null) break;
                }
            }

            if (_repoLibUpgradesModuleType == null)
            {
                Logger.LogError("[LuckyUpgrades] Cannot find REPOLib.Modules.Upgrades");
                return;
            }

            if (_repoLibGetUpgradeMethod == null)
            {
                _repoLibGetUpgradeMethod = _repoLibUpgradesModuleType.GetMethod("GetUpgrade",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(string) }, null);
            }

            if (_repoLibGetUpgradeMethod == null)
            {
                Logger.LogError("[LuckyUpgrades] REPOLib.Modules.Upgrades.GetUpgrade(string) not found");
                return;
            }

            var getUpgrade = _repoLibGetUpgradeMethod;

            var playerUpgrade = getUpgrade.Invoke(null, new object[] { upgradeId });
            if (playerUpgrade == null)
            {
                Logger.LogWarning($"[LuckyUpgrades] REPOLib upgrade '{upgradeId}' not in registry");
                return;
            }

            // FIX: Try AddLevel(PlayerAvatar, int) first — this is the actual method on
            // REPOLib's PlayerUpgrade class. Fall back to Upgrade(PlayerAvatar) for any
            // future API changes, then AddLevel(PlayerAvatar) as a last resort.
            var upgradeType = playerUpgrade.GetType();

            var addLevelWithAmount = upgradeType.GetMethod("AddLevel",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(PlayerAvatar), typeof(int) }, null);

            var upgradeMethod = upgradeType.GetMethod("Upgrade",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(PlayerAvatar) }, null);

            var addLevelNoAmount = upgradeType.GetMethod("AddLevel",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(PlayerAvatar) }, null);

            if (addLevelWithAmount != null)
            {
                // Most efficient: pass amount directly
                addLevelWithAmount.Invoke(playerUpgrade, new object[] { target, amount });
                Logger.LogInfo($"[LuckyUpgrades] Applied REPOLib upgrade '{upgradeId}' to {steamID} x{amount} via AddLevel(PlayerAvatar, int) ✓");
            }
            else if (upgradeMethod != null)
            {
                for (int i = 0; i < amount; i++)
                    upgradeMethod.Invoke(playerUpgrade, new object[] { target });
                Logger.LogInfo($"[LuckyUpgrades] Applied REPOLib upgrade '{upgradeId}' to {steamID} x{amount} via Upgrade(PlayerAvatar) ✓");
            }
            else if (addLevelNoAmount != null)
            {
                for (int i = 0; i < amount; i++)
                    addLevelNoAmount.Invoke(playerUpgrade, new object[] { target });
                Logger.LogInfo($"[LuckyUpgrades] Applied REPOLib upgrade '{upgradeId}' to {steamID} x{amount} via AddLevel(PlayerAvatar) ✓");
            }
            else
            {
                // Nothing worked — log all available methods so this is easy to diagnose next time
                Logger.LogWarning($"[LuckyUpgrades] No applicable upgrade method found on PlayerUpgrade for '{upgradeId}'. Methods available:");
                foreach (var m in upgradeType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    Logger.LogWarning($"[LuckyUpgrades]   {m.Name}({string.Join(", ", System.Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name))})");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[LuckyUpgrades] ApplyREPOLibUpgrade failed: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Applies the named upgrade to the given player via PunManager, respecting amount.
    /// </summary>
    private static void ApplyUpgradeByType(string upgradeType, string steamID, int amount = 1)
    {
        switch (upgradeType)
        {
            case "Health":         for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerHealth(steamID, 1);       break;
            case "Energy":         for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerEnergy(steamID, 1);       break;
            case "ExtraJump":      for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerExtraJump(steamID, 1);    break;
            case "GrabRange":      for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerGrabRange(steamID, 1);    break;
            case "GrabStrength":   for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerGrabStrength(steamID, 1); break;
            case "GrabThrow":      for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerThrowStrength(steamID, 1);break;
            case "SprintSpeed":    for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerSprintSpeed(steamID, 1);  break;
            case "TumbleLaunch":   for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerTumbleLaunch(steamID, 1); break;
            case "MapPlayerCount": for (int i = 0; i < amount; i++) PunManager.instance.UpgradeMapPlayerCount(steamID, 1);     break;
            case "TumbleClimb":    for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerTumbleClimb(steamID, 1);  break;
            case "TumbleWings":    for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerTumbleWings(steamID, 1);  break;
            case "CrouchRest":     for (int i = 0; i < amount; i++) PunManager.instance.UpgradePlayerCrouchRest(steamID, 1);   break;
            case "DeathHeadBattery": for (int i = 0; i < amount; i++) PunManager.instance.UpgradeDeathHeadBattery(steamID, 1); break;
            default:
                lock (ModdedUpgradeRegistryLock)
                {
                    if (_moddedUpgradeRegistry.TryGetValue(upgradeType, out var entry))
                        entry.apply(steamID, amount);
                    else
                        Logger.LogWarning($"[LuckyUpgrades] ApplyUpgradeByType: no handler for '{upgradeType}'");
                }
                break;
        }
    }

    /// <summary>
    /// Tries to get the SteamID of the player who used the upgrade item.
    /// Checks direct playerAvatar field first, then falls back to grabbing player.
    /// </summary>
    private static string GetSteamIDFromItem(ItemUpgrade item)
    {
        if (item == null) return null;

        try
        {
            // Try direct playerAvatar field via reflection
            var avatarField = item.GetType().GetField("playerAvatar",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);

            if (avatarField != null)
            {
                var avatar = avatarField.GetValue(item) as PlayerAvatar;
                if (avatar != null)
                    return SemiFunc.PlayerGetSteamID(avatar);
            }

            // Fallback: find who is physically grabbing the item
            var physObj = item.GetComponent<PhysGrabObject>();
            if (physObj != null)
            {
                var grabbers = SemiFunc.PhysGrabObjectGetPlayerAvatarsGrabbing(physObj);
                if (grabbers != null && grabbers.Count > 0)
                    return SemiFunc.PlayerGetSteamID(grabbers[0]);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[LuckyUpgrades] GetSteamIDFromItem failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Rolls the share chance and applies the upgrade to the local player if successful.
    /// </summary>
    private static void ApplySharedUpgradeToSelf(
        string upgradeType,
        string sourceSteamID,
        string sourcePlayerName,
        int amount,
        Action<int> applyToSelf,
        int? chanceOverride = null)
    {
        try
        {
            int shareChance = chanceOverride ?? UpgradeConfiguration.GetShareChance(upgradeType);

            if (shareChance <= 0)
            {
                Logger.LogInfo($"[LuckyUpgrades] Shared upgrade skipped: {upgradeType} (0% chance) ✗");
                return;
            }

            // If chance is 100%, always apply without RNG
            if (shareChance >= 100)
            {
                bool applied = false;
                try
                {
                    lock (_inFlightLock)
                    {
                        if (_inFlightUpgrades.Contains(upgradeType))
                        {
                            Logger.LogWarning($"[LuckyUpgrades] Duplicate apply blocked for '{upgradeType}' (in-flight guard) ✗");
                            return;
                        }
                        _inFlightUpgrades.Add(upgradeType);
                    }
                    Interlocked.Exchange(ref _isApplyingSharedUpgrade, 1);
                    applyToSelf(amount);
                    applied = true;
                }
                finally
                {
                    Interlocked.Exchange(ref _isApplyingSharedUpgrade, 0);
                    lock (_inFlightLock) { _inFlightUpgrades.Remove(upgradeType); }
                }
                if (applied)
                {
                    TrackSharedUpgrade(upgradeType, amount);
                    UpdateStreakAndSession(won: true);
                    Logger.LogInfo($"[LuckyUpgrades] Shared upgrade applied: {upgradeType} +{amount} (100% guaranteed) ✓");
                    int streak = GetStreak();
                    UpgradeToastUI.ShowToast(upgradeType, won: true, sourcePlayerName, streak);
                }
                return;
            }

            // Probabilistic roll
            int roll;
            lock (_randomLock) { roll = _random.Next(100); }

            if (roll < shareChance)
            {
                bool applied = false;
                try
                {
                    lock (_inFlightLock)
                    {
                        if (_inFlightUpgrades.Contains(upgradeType))
                        {
                            Logger.LogWarning($"[LuckyUpgrades] Duplicate apply blocked for '{upgradeType}' (in-flight guard) ✗");
                            return;
                        }
                        _inFlightUpgrades.Add(upgradeType);
                    }
                    Interlocked.Exchange(ref _isApplyingSharedUpgrade, 1);
                    applyToSelf(amount);
                    applied = true;
                }
                finally
                {
                    Interlocked.Exchange(ref _isApplyingSharedUpgrade, 0);
                    lock (_inFlightLock) { _inFlightUpgrades.Remove(upgradeType); }
                }
                if (applied)
                {
                    TrackSharedUpgrade(upgradeType, amount);
                    UpdateStreakAndSession(won: true);
                    Logger.LogInfo($"[LuckyUpgrades] Shared upgrade applied: {upgradeType} +{amount} (rolled: {roll} | chance: {shareChance}%) ✓");
                    int streak = GetStreak();
                    UpgradeToastUI.ShowToast(upgradeType, won: true, sourcePlayerName, streak);
                }
            }
            else
            {
                UpdateStreakAndSession(won: false);
                Logger.LogInfo($"[LuckyUpgrades] Shared upgrade missed: {upgradeType} (rolled: {roll} | chance: {shareChance}%) ✗");
                int streak = GetStreak();
                UpgradeToastUI.ShowToast(upgradeType, won: false, sourcePlayerName, streak);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[LuckyUpgrades] Error in ApplySharedUpgradeToSelf: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ── Streak + session helpers ──────────────────────────────────────────────

    private static void UpdateStreakAndSession(bool won)
    {
        lock (_streakLock)
        {
            if (won)
                _currentStreak = _currentStreak >= 0 ? _currentStreak + 1 : 1;
            else
                _currentStreak = _currentStreak <= 0 ? _currentStreak - 1 : -1;
        }
        lock (_sessionLock)
        {
            _sessionRollsTotal++;
            if (won) _sessionUpgradesReceived++;
        }
    }

    private static int GetStreak()
    {
        lock (_streakLock) { return _currentStreak; }
    }

    internal static void ResetSessionStats()
    {
        lock (_streakLock)   { _currentStreak = 0; }
        lock (_sessionLock)  { _sessionUpgradesReceived = 0; _sessionRollsTotal = 0; }
        lock (_cooldownLock) { _lastTriggerTime.Clear(); }
    }

    internal static (int received, int total) GetSessionStats()
    {
        lock (_sessionLock) { return (_sessionUpgradesReceived, _sessionRollsTotal); }
    }
}

// =============================================================================

/// <summary>
/// Separate MonoBehaviour to detect level changes and reapply upgrades.
/// </summary>
public class UpgradeReapplyRunner : MonoBehaviour
{
    // All level names that signal the end of an active run.
    // State is cleared whenever ANY of these are entered so that _sharedUpgrades
    // never leaks into a new lobby even if REPO renames a scene between patches.
    // The additional "Lobby" / "Main" / "Menu" substrings are matched separately
    // via IsSessionEndLevel() below so a simple rename can't slip through.
    private static readonly HashSet<string> SESSION_END_LEVELS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Level - Main Menu",
        "Level - Lobby Menu",
        "Level - MainMenu",      // alternate casing seen in some builds
        "Level - LobbyMenu",
        "Main Menu",
        "Lobby",
        "Lobby Menu",
    };

    /// <summary>
    /// Returns true if <paramref name="levelName"/> is a lobby / menu scene where
    /// run state should be wiped.  Checks the explicit set first, then falls back
    /// to a substring match so future renames don't silently break session cleanup.
    /// </summary>
    private static bool IsSessionEndLevel(string levelName)
    {
        if (SESSION_END_LEVELS.Contains(levelName)) return true;
        // Substring guard: any scene whose name contains "menu" or "lobby"
        // (case-insensitive) is treated as a non-run level.
        return levelName.IndexOf("menu",  StringComparison.OrdinalIgnoreCase) >= 0
            || levelName.IndexOf("lobby", StringComparison.OrdinalIgnoreCase) >= 0;
    }

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

            if (IsSessionEndLevel(currentLevel))
            {
                // ── QoL: session summary toast ────────────────────────────────
                var (received, total) = Plugin.GetSessionStats();
                if (total > 0 && (Plugin.UpgradeConfiguration?.ShowSessionSummary.Value ?? true))
                    UpgradeToastUI.ShowSessionSummary(received, total);

                lock (Plugin.SharedUpgradesLock)
                {
                    Plugin._sharedUpgrades.Clear();
                }
                lock (Plugin.MySteamIDLock)
                {
                    Plugin._mySteamID = null;
                }
                Plugin.ResetSessionStats();
                Plugin.Logger.LogInfo("[LuckyUpgrades] Session ended. All tracked data cleared.");
                return;
            }

            // Evaluate the host guard at scheduling time, not after the delay.
            // ReapplySharedUpgrades() clears _sharedUpgrades immediately on entry, so
            // checking the count inside the delay callback would always see 0.
            //
            // The host (MasterClient) never needs a reapply: REPO persists upgrade levels
            // natively for the host, so re-granting them would produce duplicates.
            // Non-host clients lose their stat overrides on level load and DO need the reapply.
            lock (Plugin.SharedUpgradesLock)
            {
                if (!PhotonNetwork.IsMasterClient && Plugin._sharedUpgrades.Count > 0)
                {
                    _pendingReapply = true;
                    _reapplyDelay = REAPPLY_DELAY_SECONDS;
                    Plugin.Logger.LogInfo($"[LuckyUpgrades] Scheduled reapply in {REAPPLY_DELAY_SECONDS}s...");
                }
                else if (PhotonNetwork.IsMasterClient && Plugin._sharedUpgrades.Count > 0)
                {
                    Plugin.Logger.LogInfo("[LuckyUpgrades] Host detected — skipping reapply (upgrades persist natively).");
                }
            }
        }

        if (_pendingReapply)
        {
            _reapplyDelay -= Time.deltaTime;
            if (_reapplyDelay <= 0f)
            {
                // BUG FIX: don't clear _pendingReapply until we actually succeed.
                // Previously a null player or missing SteamID caused a silent `return`
                // after the flag was already cleared, dropping the reapply permanently.
                var localPlayer = SemiFunc.PlayerAvatarLocal();
                if (localPlayer == null)
                {
                    _reapplyDelay = 0.5f; // player not spawned yet — retry shortly
                    return;
                }

                string myID = Plugin.GetMySteamID();
                if (string.IsNullOrEmpty(myID))
                {
                    _reapplyDelay = 0.5f; // SteamID not ready yet — retry shortly
                    return;
                }

                _pendingReapply = false;
                Plugin.ReapplySharedUpgrades();
            }
        }
    }
}

// =============================================================================
// R.E.P.O. upgrade notification toast — bottom-right corner
// =============================================================================

/// <summary>
/// Renders R.E.P.O.-flavoured upgrade share notifications in the bottom-right corner.
///
/// Visual anatomy (per toast, 300 × 100 px):
///   ┌─[5px accent]──────────────────────────────────────────┐
///   │ LUCKY UPGRADES                              [WIN/MISS] │  ← 22px header strip
///   ├────────────────────────────────────────────────────────┤
///   │ [32×32 icon]  UPGRADE NAME                            │  ← upgrade body (54px)
///   │               + UPGRADE SHARED  /  - NOT THIS TIME    │
///   │ [████████████░░░░░░░░░]  progress bar                 │
///   └────────────────────────────────────────────────────────┘
///
/// Win accent = #D4A800 (amber-gold)   Miss accent = #8A1A1A (blood red)
/// Background = #0E0F12 (near-black)   Header font = teal monospace
/// Scanline overlay drawn as stacked 1px rects (every 4px) for the CRT look.
/// </summary>
public class UpgradeToastUI : MonoBehaviour
{
    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>Queue a share-result toast. Thread-safe.</summary>
    public static void ShowToast(string upgradeType, bool won, string sourcePlayerName = "", int streak = 0)
    {
        if (_instance == null) return;
        var cfg = Plugin.UpgradeConfiguration;
        if (cfg != null && !cfg.ShowUpgradeNotifications.Value) return;
        lock (_queue)
            _queue.Enqueue(new ToastData
            {
                upgradeType      = upgradeType,
                won              = won,
                sourcePlayerName = sourcePlayerName,
                streak           = streak,
                toastKind        = ToastKind.ShareResult,
            });
    }

    /// <summary>Show a toast to the player who just picked up the upgrade.</summary>
    public static void ShowPickerToast(string upgradeType)
    {
        if (_instance == null) return;
        var cfg = Plugin.UpgradeConfiguration;
        if (cfg != null && !cfg.ShowUpgradeNotifications.Value) return;
        lock (_queue)
            _queue.Enqueue(new ToastData
            {
                upgradeType = upgradeType,
                won         = true,
                toastKind   = ToastKind.Picker,
            });
    }

    /// <summary>Show the end-of-run session summary.</summary>
    public static void ShowSessionSummary(int received, int total)
    {
        if (_instance == null) return;
        var cfg = Plugin.UpgradeConfiguration;
        if (cfg != null && !cfg.ShowUpgradeNotifications.Value) return;
        lock (_queue)
            _queue.Enqueue(new ToastData
            {
                toastKind       = ToastKind.SessionSummary,
                sessionReceived = received,
                sessionTotal    = total,
                won             = received > 0,
            });
    }

    // -----------------------------------------------------------------------
    // Internal types
    // -----------------------------------------------------------------------

    private enum ToastKind { ShareResult, Picker, SessionSummary }

    private struct ToastData
    {
        public string    upgradeType;
        public bool      won;
        public string    sourcePlayerName;
        public int       streak;
        public ToastKind toastKind;
        public int       sessionReceived;
        public int       sessionTotal;
    }

    private class ActiveToast
    {
        public string    upgradeType;
        public bool      won;
        public string    sourcePlayerName;
        public int       streak;
        public ToastKind toastKind;
        public int       sessionReceived;
        public int       sessionTotal;
        public float     holdDuration;
        public float     elapsed;
    }

    // -----------------------------------------------------------------------
    // Design constants
    // -----------------------------------------------------------------------

    private const float W              = 300f;
    private const float H              = 115f;
    private const float HEADER_H       = 22f;
    private const float ACCENT_W       = 5f;
    private const float ICON_SIZE      = 32f;
    private const float ICON_MARGIN    = 10f;
    private const float BAR_H          = 3f;
    private const float MARGIN_RIGHT   = 20f;
    private const float MARGIN_BOTTOM  = 80f;
    private const float STACK_GAP      = 6f;
    private const float SLIDE_DUR      = 0.20f;
    private const float FADE_DUR       = 0.45f;

    // Accent colours — vivid enough to read clearly during gameplay
    private static readonly Color WIN_ACCENT  = new Color(1.00f, 0.82f, 0.00f, 1f); // bright gold
    private static readonly Color LOSE_ACCENT = new Color(0.90f, 0.18f, 0.18f, 1f); // vivid red
    private static readonly Color BG_DARK     = new Color(0.055f, 0.059f, 0.071f, 0.97f);
    private static readonly Color HEADER_BG   = new Color(1f, 1f, 1f, 0.03f);
    private static readonly Color TEAL_HEADER = new Color(0.20f, 0.78f, 0.68f, 1f); // bright teal
    private static readonly Color TEXT_MAIN   = new Color(0.96f, 0.96f, 0.92f, 1f); // near-white
    private static readonly Color TEXT_DIM    = new Color(1f, 1f, 1f, 0.30f);
    private static readonly Color BORDER_DIM  = new Color(1f, 1f, 1f, 0.08f);
    private static readonly Color WIN_TEXT    = new Color(1.00f, 0.90f, 0.20f, 1f); // bright amber
    private static readonly Color LOSE_TEXT   = new Color(1.00f, 0.38f, 0.38f, 1f); // bright red

    // -----------------------------------------------------------------------
    // Upgrade labels
    // -----------------------------------------------------------------------

    private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
    {
        { "Health",          "HEALTH"           },
        { "Energy",          "ENERGY"           },
        { "SprintSpeed",     "SPRINT SPEED"     },
        { "ExtraJump",       "EXTRA JUMP"       },
        { "TumbleLaunch",    "TUMBLE LAUNCH"    },
        { "TumbleClimb",     "TUMBLE CLIMB"     },
        { "TumbleWings",     "TUMBLE WINGS"     },
        { "CrouchRest",      "CROUCH REST"      },
        { "GrabRange",       "GRAB RANGE"       },
        { "GrabStrength",    "GRAB STRENGTH"    },
        { "GrabThrow",       "GRAB THROW"       },
        { "MapPlayerCount",  "MAP PLAYER COUNT" },
        { "DeathHeadBattery","DEATH HEAD BATT." },
    };

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    private static UpgradeToastUI       _instance;
    private static readonly Queue<ToastData>  _queue  = new Queue<ToastData>();
    private readonly        List<ActiveToast> _active = new List<ActiveToast>();

    // Solid-colour textures (all 1×1, tinted via GUI.color)
    private Texture2D _texWhite;
    // Scanline strip: 1×4 repeating, rows 0-2 transparent, row 3 8% dark
    private Texture2D _texScanline;

    // GUIStyles — initialised once on first OnGUI call
    private GUIStyle _stHeader;    // "LUCKY UPGRADES"   10px bold teal
    private GUIStyle _stBadge;     // "WIN" / "MISS"     9px bold
    private GUIStyle _stIcon;      // icon glyph inside box  16px bold centred
    private GUIStyle _stName;      // upgrade name       15px bold white
    private GUIStyle _stResult;    // result line        11px bold
    private GUIStyle _stFoot;      // bottom label       8px dim
    private bool     _assetsReady;

    // -----------------------------------------------------------------------
    // Unity lifecycle
    // -----------------------------------------------------------------------

    private void Awake()     { _instance = this; }
    private void OnDestroy() { if (_instance == this) _instance = null; }

    private void Update()
    {
        lock (_queue)
        {
            while (_queue.Count > 0)
            {
                var d    = _queue.Dequeue();
                float hd = d.toastKind == ToastKind.SessionSummary
                    ? (Plugin.UpgradeConfiguration?.NotificationDisplayTime.Value ?? 3f) + 1.5f  // summaries linger longer
                    : Plugin.UpgradeConfiguration?.NotificationDisplayTime.Value ?? 3f;
                _active.Add(new ActiveToast
                {
                    upgradeType      = d.upgradeType,
                    won              = d.won,
                    sourcePlayerName = d.sourcePlayerName ?? "",
                    streak           = d.streak,
                    toastKind        = d.toastKind,
                    sessionReceived  = d.sessionReceived,
                    sessionTotal     = d.sessionTotal,
                    holdDuration     = hd,
                    elapsed          = 0f,
                });
            }
        }

        float dt = Time.unscaledDeltaTime;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            _active[i].elapsed += dt;
            if (_active[i].elapsed >= _active[i].holdDuration + FADE_DUR)
                _active.RemoveAt(i);
        }
    }

    // -----------------------------------------------------------------------
    // Rendering
    // -----------------------------------------------------------------------

    private void OnGUI()
    {
        if (_active.Count == 0) return;
        EnsureAssets();

        float sw = Screen.width;
        float sh = Screen.height;

        for (int i = 0; i < _active.Count; i++)
        {
            var t = _active[i];

            // Newest (i=0) sits lowest; older toasts stack upward
            float baseX = sw - MARGIN_RIGHT - W;
            float baseY = sh - MARGIN_BOTTOM - H - i * (H + STACK_GAP);

            // Slide in from the right (ease-out cubic)
            float slideT  = Mathf.Clamp01(t.elapsed / SLIDE_DUR);
            float eased   = 1f - Mathf.Pow(1f - slideT, 3f);
            float slideX  = Mathf.Lerp(W + MARGIN_RIGHT, 0f, eased);

            float x = baseX + slideX;
            float y = baseY;

            // Fade out
            float alpha = 1f;
            if (t.elapsed > t.holdDuration)
                alpha = 1f - Mathf.Clamp01((t.elapsed - t.holdDuration) / FADE_DUR);

            DrawToast(x, y, t, alpha);
        }

        GUI.color = Color.white;
    }

    // -----------------------------------------------------------------------
    // DrawToast — every pixel placed explicitly
    // -----------------------------------------------------------------------

    private void DrawToast(float x, float y, ActiveToast t, float a)
    {
        switch (t.toastKind)
        {
            case ToastKind.Picker:          DrawPickerToast(x, y, t, a);   break;
            case ToastKind.SessionSummary:  DrawSummaryToast(x, y, t, a);  break;
            default:                        DrawShareResultToast(x, y, t, a); break;
        }
    }

    // ── Share result toast (win / miss) ───────────────────────────────────────
    private void DrawShareResultToast(float x, float y, ActiveToast t, float a)
    {
        Color accent = t.won ? WIN_ACCENT : LOSE_ACCENT;
        Color Aa(Color c) => new Color(c.r, c.g, c.b, c.a * a);

        // Panel + chrome
        DrawPanel(x, y, W, H, accent, a);

        // WIN/MISS badge
        DrawBadge(x, y, t.won ? "WIN" : "MISS", accent, a);

        // Header
        _stHeader.normal.textColor = Aa(TEAL_HEADER);
        GUI.Label(new Rect(x + ACCENT_W + 7f, y + 1f, W - ACCENT_W - 48f, HEADER_H), "LUCKY UPGRADES", _stHeader);

        // Icon box
        float iconX = x + ACCENT_W + ICON_MARGIN;
        float iconY = y + HEADER_H + 8f;
        DrawIconBox(iconX, iconY, t.won ? "+" : "x", accent, a);

        // Upgrade name
        float textX = iconX + ICON_SIZE + 8f;
        float textW = x + W - 8f - textX;
        string label = Labels.TryGetValue(t.upgradeType, out var lb) ? lb : t.upgradeType.ToUpperInvariant();
        _stName.normal.textColor = Aa(TEXT_MAIN);
        GUI.Label(new Rect(textX, iconY - 1f, textW, 22f), label, _stName);

        // Result line
        _stResult.normal.textColor = t.won ? Aa(WIN_TEXT) : Aa(LOSE_TEXT);
        GUI.Label(new Rect(textX, iconY + 18f, textW, 16f),
            t.won ? "+ YOU GOT IT!" : "- NOT THIS TIME", _stResult);

        // Source player name (dim, below result)
        bool showName = Plugin.UpgradeConfiguration?.ShowSourcePlayerName.Value ?? true;
        if (showName && !string.IsNullOrEmpty(t.sourcePlayerName))
        {
            _stFoot.normal.textColor = Aa(TEXT_DIM);
            GUI.Label(new Rect(textX, iconY + 36f, textW, 16f),
                $"from {t.sourcePlayerName}", _stFoot);
        }

        // Streak (only if ≥2 in a row, config-guarded)
        bool showStreak = Plugin.UpgradeConfiguration?.ShowStreakCounter.Value ?? true;
        int absStreak = Math.Abs(t.streak);
        if (showStreak && absStreak >= 2)
        {
            string streakLabel = t.streak > 0
                ? $"{absStreak} wins in a row!"
                : $"{absStreak} misses in a row";
            Color streakCol = t.streak > 0
                ? new Color(1f, 0.95f, 0.3f, 0.9f * a)
                : new Color(0.9f, 0.4f, 0.4f, 0.8f * a);
            _stFoot.normal.textColor = streakCol;
            float streakY = showName && !string.IsNullOrEmpty(t.sourcePlayerName) ? iconY + 52f : iconY + 36f;
            GUI.Label(new Rect(textX, streakY, textW, 16f), streakLabel, _stFoot);
        }

        // Progress bar
        DrawProgressBar(x, y, t.elapsed, t.holdDuration, accent, a);

        GUI.color = Color.white;
    }

    // ── Picker toast ("sharing your upgrade!") ────────────────────────────────
    private void DrawPickerToast(float x, float y, ActiveToast t, float a)
    {
        // Picker toast is shorter — use a dimmer teal accent
        Color accent = new Color(0.20f, 0.78f, 0.68f, 1f);
        Color Aa(Color c) => new Color(c.r, c.g, c.b, c.a * a);

        DrawPanel(x, y, W, H, accent, a);
        DrawBadge(x, y, "YOU", accent, a);

        _stHeader.normal.textColor = Aa(TEAL_HEADER);
        GUI.Label(new Rect(x + ACCENT_W + 7f, y + 1f, W - ACCENT_W - 48f, HEADER_H), "LUCKY UPGRADES", _stHeader);

        float iconX = x + ACCENT_W + ICON_MARGIN;
        float iconY = y + HEADER_H + 8f;
        DrawIconBox(iconX, iconY, ">", accent, a);

        float textX = iconX + ICON_SIZE + 8f;
        float textW = x + W - 8f - textX;

        string label = Labels.TryGetValue(t.upgradeType, out var lb) ? lb : t.upgradeType.ToUpperInvariant();
        _stName.normal.textColor = Aa(TEXT_MAIN);
        GUI.Label(new Rect(textX, iconY - 1f, textW, 22f), label, _stName);

        _stResult.normal.textColor = new Color(0.20f, 0.90f, 0.78f, a);
        GUI.Label(new Rect(textX, iconY + 18f, textW, 16f), "~ SHARING THIS UPGRADE", _stResult);

        _stFoot.normal.textColor = Aa(TEXT_DIM);
        GUI.Label(new Rect(textX, iconY + 36f, textW, 16f), "teammates are rolling now...", _stFoot);

        DrawProgressBar(x, y, t.elapsed, t.holdDuration, accent, a);
        GUI.color = Color.white;
    }

    // ── Session summary toast ─────────────────────────────────────────────────
    private void DrawSummaryToast(float x, float y, ActiveToast t, float a)
    {
        Color accent = t.sessionReceived > 0 ? WIN_ACCENT : LOSE_ACCENT;
        Color Aa(Color c) => new Color(c.r, c.g, c.b, c.a * a);

        DrawPanel(x, y, W, H, accent, a);
        DrawBadge(x, y, "RUN", accent, a);

        _stHeader.normal.textColor = Aa(TEAL_HEADER);
        GUI.Label(new Rect(x + ACCENT_W + 7f, y + 1f, W - ACCENT_W - 48f, HEADER_H), "LUCKY UPGRADES", _stHeader);

        float iconX = x + ACCENT_W + ICON_MARGIN;
        float iconY = y + HEADER_H + 8f;
        DrawIconBox(iconX, iconY, "#", accent, a);

        float textX = iconX + ICON_SIZE + 8f;
        float textW = x + W - 8f - textX;

        _stName.normal.textColor = Aa(TEXT_MAIN);
        GUI.Label(new Rect(textX, iconY - 1f, textW, 22f), "RUN COMPLETE", _stName);

        int pct = t.sessionTotal > 0 ? (t.sessionReceived * 100 / t.sessionTotal) : 0;
        _stResult.normal.textColor = t.sessionReceived > 0 ? Aa(WIN_TEXT) : Aa(LOSE_TEXT);
        GUI.Label(new Rect(textX, iconY + 18f, textW, 16f),
            $"{t.sessionReceived} / {t.sessionTotal} upgrades", _stResult);

        _stFoot.normal.textColor = Aa(TEXT_DIM);
        GUI.Label(new Rect(textX, iconY + 36f, textW, 16f),
            $"{pct}% share rate this run", _stFoot);

        DrawProgressBar(x, y, t.elapsed, t.holdDuration, accent, a);
        GUI.color = Color.white;
    }

    // -----------------------------------------------------------------------
    // Shared drawing helpers — used by all three toast types
    // -----------------------------------------------------------------------

    private void DrawPanel(float x, float y, float w, float h, Color accent, float a)
    {
        Color Aa(Color c) => new Color(c.r, c.g, c.b, c.a * a);
        Tint(Aa(BG_DARK));     Draw(x, y, w, h);
        Tint(Aa(accent));      Draw(x, y, ACCENT_W, h);
        Tint(Aa(HEADER_BG));   Draw(x + ACCENT_W, y, w - ACCENT_W, HEADER_H);
        Tint(new Color(accent.r, accent.g, accent.b, 0.55f * a));
        Draw(x + ACCENT_W, y, w - ACCENT_W, 1f);             // top edge
        Tint(new Color(accent.r, accent.g, accent.b, 0.22f * a));
        Draw(x + ACCENT_W, y + HEADER_H, w - ACCENT_W, 1f);  // separator
        Tint(Aa(BORDER_DIM));
        Draw(x + ACCENT_W, y + h - 1f, w - ACCENT_W, 1f);    // bottom
        Draw(x + w - 1f,   y,           1f, h);              // right
        Tint(Color.white);
        GUI.DrawTexture(new Rect(x, y, w, h), _texScanline, ScaleMode.ScaleAndCrop);
    }

    private void DrawBadge(float x, float y, string text, Color accent, float a)
    {
        float badgeW = 36f;
        float badgeX = x + W - badgeW - 6f;
        Tint(new Color(accent.r, accent.g, accent.b, 0.22f * a));
        Draw(badgeX, y + 4f, badgeW, 14f);
        DrawBorder(badgeX, y + 4f, badgeW, 14f, new Color(accent.r, accent.g, accent.b, 0.55f * a));
        _stBadge.normal.textColor = new Color(accent.r, accent.g, accent.b, a);
        GUI.Label(new Rect(badgeX, y + 4f, badgeW, 14f), text, _stBadge);
    }

    private void DrawIconBox(float iconX, float iconY, string glyph, Color accent, float a)
    {
        Tint(new Color(accent.r, accent.g, accent.b, 0.15f * a));
        Draw(iconX, iconY, ICON_SIZE, ICON_SIZE);
        DrawBorder(iconX, iconY, ICON_SIZE, ICON_SIZE, new Color(accent.r, accent.g, accent.b, 0.55f * a));
        _stIcon.normal.textColor = new Color(accent.r, accent.g, accent.b, a);
        GUI.Label(new Rect(iconX, iconY, ICON_SIZE, ICON_SIZE), glyph, _stIcon);
    }

    private void DrawProgressBar(float x, float y, float elapsed, float hold, Color accent, float a)
    {
        float barX   = x + ACCENT_W + ICON_MARGIN;
        float barY   = y + H - 16f;
        float barW   = W - ACCENT_W - ICON_MARGIN - 8f;
        float fillPct = Mathf.Clamp01(1f - (elapsed / hold));
        Tint(new Color(1f, 1f, 1f, 0.07f * a));
        Draw(barX, barY, barW, BAR_H);
        Tint(new Color(accent.r, accent.g, accent.b, 0.55f * a));
        Draw(barX, barY, barW * fillPct, BAR_H);
    }

    // -----------------------------------------------------------------------
    // Low-level drawing helpers (all use _texWhite, coloured via GUI.color)
    // -----------------------------------------------------------------------

    private void Tint(Color c)                          => GUI.color = c;
    private void Draw(float x, float y, float w, float h) => GUI.DrawTexture(new Rect(x, y, w, h), _texWhite);

    private void DrawBorder(float x, float y, float w, float h, Color c)
    {
        Tint(c);
        Draw(x, y,         w, 1f);       // top
        Draw(x, y + h - 1, w, 1f);       // bottom
        Draw(x, y,         1f, h);       // left
        Draw(x + w - 1, y, 1f, h);       // right
    }

    // -----------------------------------------------------------------------
    // Asset initialisation (lazy, first OnGUI)
    // -----------------------------------------------------------------------

    private void EnsureAssets()
    {
        if (_assetsReady) return;
        _assetsReady = true;

        _texWhite = MakeSolid(Color.white);

        // 4-row repeating scanline: rows 0-2 transparent, row 3 = 8% dark
        _texScanline = new Texture2D(1, 4, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Repeat,
        };
        _texScanline.SetPixel(0, 0, Color.clear);
        _texScanline.SetPixel(0, 1, Color.clear);
        _texScanline.SetPixel(0, 2, Color.clear);
        _texScanline.SetPixel(0, 3, new Color(0f, 0f, 0f, 0.08f));
        _texScanline.Apply();

        var lbl = GUI.skin.label;

        _stHeader = new GUIStyle(lbl)
        {
            fontSize  = 10,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            wordWrap  = false,
        };
        _stBadge = new GUIStyle(lbl)
        {
            fontSize  = 9,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            wordWrap  = false,
        };
        _stIcon = new GUIStyle(lbl)
        {
            fontSize  = 20,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            wordWrap  = false,
        };
        _stName = new GUIStyle(lbl)
        {
            fontSize  = 14,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            wordWrap  = false,
        };
        _stResult = new GUIStyle(lbl)
        {
            fontSize  = 11,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            wordWrap  = false,
        };
        _stFoot = new GUIStyle(lbl)
        {
            fontSize  = 10,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            wordWrap  = false,
        };
    }

    private static Texture2D MakeSolid(Color col)
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp,
        };
        tex.SetPixel(0, 0, col);
        tex.Apply();
        return tex;
    }
}