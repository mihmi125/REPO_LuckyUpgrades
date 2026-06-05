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

    // Thread-safe re-entrancy guard (0 = not applying, 1 = applying)
    private static int _isApplyingSharedUpgrade = 0;

    private static readonly object _randomLock = new object();
    private static readonly System.Random _random = new System.Random();

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
        (Action<string, int> apply, Func<int> getChance) entry;

        lock (ModdedUpgradeRegistryLock)
        {
            if (!_moddedUpgradeRegistry.TryGetValue(upgradeId, out entry))
            {
                Logger?.LogError($"[LuckyUpgrades] ✗ TriggerModdedUpgradeShare: '{upgradeId}' NOT REGISTERED! Did you call RegisterModdedUpgrade()?");
                return;
            }
        }

        Logger?.LogInfo($"[LuckyUpgrades] → TriggerModdedUpgradeShare called: '{upgradeId}' from {sourceSteamID}");

        ApplySharedUpgradeToSelf(upgradeId, sourceSteamID, amount,
            applyToSelf: (amt) =>
            {
                string myID = GetMySteamID();
                if (!string.IsNullOrEmpty(myID))
                {
                    Logger?.LogInfo($"[LuckyUpgrades] → Applying modded upgrade '{upgradeId}' to player {myID} (+{amt})");
                    entry.apply(myID, amt);
                }
                else
                {
                    Logger?.LogWarning($"[LuckyUpgrades] ✗ Cannot apply '{upgradeId}': SteamID is null/empty");
                }
            },
            chanceOverride: entry.getChance());
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
            // Create a snapshot to avoid concurrent modification
            upgradesToReapply = new Dictionary<string, int>(_sharedUpgrades);
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
            // Skip if we triggered this call ourselves
            if (Interlocked.CompareExchange(ref _isApplyingSharedUpgrade, 0, 0) == 1)
            {
                return;
            }

            string mySteamID = GetMySteamID();

            // Identify upgrade type
            string upgradeType = GetUpgradeType(__instance);
            if (string.IsNullOrEmpty(upgradeType)) return;

            // Get source SteamID
            string sourceSteamID = GetSteamIDFromItem(__instance);
            if (string.IsNullOrEmpty(sourceSteamID)) return;
            if (string.IsNullOrEmpty(mySteamID)) return;

            // Skip if this player picked up the upgrade themselves
            if (mySteamID == sourceSteamID) return;

            // Look up the chance from the registry for REPOLib/modded upgrades
            // so we use the current config value rather than falling through to
            // GetShareChance which only knows about the 13 built-in upgrade keys.
            int? chanceOverride = null;
            lock (ModdedUpgradeRegistryLock)
            {
                if (_moddedUpgradeRegistry.TryGetValue(upgradeType, out var registryEntry))
                    chanceOverride = registryEntry.getChance();
            }

            ApplySharedUpgradeToSelf(upgradeType, sourceSteamID, 1,
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

    // FIX: Use AddLevel(PlayerAvatar, int) as the primary apply method for REPOLib upgrades.
    // The old code tried Upgrade(PlayerAvatar) which does not exist on REPOLib's PlayerUpgrade
    // class — causing a silent no-op even when the share roll succeeded.
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

            System.Type upgradesType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { upgradesType = asm.GetType("REPOLib.Modules.Upgrades"); }
                catch { }
                if (upgradesType != null) break;
            }

            if (upgradesType == null)
            {
                Logger.LogError("[LuckyUpgrades] Cannot find REPOLib.Modules.Upgrades");
                return;
            }

            var getUpgrade = upgradesType.GetMethod("GetUpgrade",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                null, new[] { typeof(string) }, null);

            if (getUpgrade == null)
            {
                Logger.LogError("[LuckyUpgrades] REPOLib.Modules.Upgrades.GetUpgrade(string) not found");
                return;
            }

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
    /// Applies one stack of the named upgrade to the given player via PunManager.
    /// </summary>
    private static void ApplyUpgradeByType(string upgradeType, string steamID)
    {
        switch (upgradeType)
        {
            case "Health":         PunManager.instance.UpgradePlayerHealth(steamID, 1);       break;
            case "Energy":         PunManager.instance.UpgradePlayerEnergy(steamID, 1);       break;
            case "ExtraJump":      PunManager.instance.UpgradePlayerExtraJump(steamID, 1);    break;
            case "GrabRange":      PunManager.instance.UpgradePlayerGrabRange(steamID, 1);    break;
            case "GrabStrength":   PunManager.instance.UpgradePlayerGrabStrength(steamID, 1); break;
            case "GrabThrow":      PunManager.instance.UpgradePlayerThrowStrength(steamID, 1);break;
            case "SprintSpeed":    PunManager.instance.UpgradePlayerSprintSpeed(steamID, 1);  break;
            case "TumbleLaunch":   PunManager.instance.UpgradePlayerTumbleLaunch(steamID, 1); break;
            case "MapPlayerCount": PunManager.instance.UpgradeMapPlayerCount(steamID, 1);     break;
            case "TumbleClimb":    PunManager.instance.UpgradePlayerTumbleClimb(steamID, 1);  break;
            case "TumbleWings":    PunManager.instance.UpgradePlayerTumbleWings(steamID, 1);  break;
            case "CrouchRest":     PunManager.instance.UpgradePlayerCrouchRest(steamID, 1);   break;
            case "DeathHeadBattery": PunManager.instance.UpgradeDeathHeadBattery(steamID, 1); break;
            default:
                lock (ModdedUpgradeRegistryLock)
                {
                    if (_moddedUpgradeRegistry.TryGetValue(upgradeType, out var entry))
                        entry.apply(steamID, 1);
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
                try
                {
                    Interlocked.Exchange(ref _isApplyingSharedUpgrade, 1);
                    applyToSelf(amount);
                    TrackSharedUpgrade(upgradeType, amount);
                    Logger.LogInfo($"[LuckyUpgrades] Shared upgrade applied: {upgradeType} +{amount} (100% guaranteed) ✓");
                }
                finally
                {
                    Interlocked.Exchange(ref _isApplyingSharedUpgrade, 0);
                }
                return;
            }

            // For normal probabilistic roll (0-99% range)
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
            Logger.LogError($"[LuckyUpgrades] Error in ApplySharedUpgradeToSelf: {ex.Message}\n{ex.StackTrace}");
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

            if (SESSION_END_LEVELS.Contains(currentLevel))
            {
                lock (Plugin.SharedUpgradesLock)
                {
                    Plugin._sharedUpgrades.Clear();
                }
                Plugin._mySteamID = null;
                Plugin.Logger.LogInfo("[LuckyUpgrades] Session ended. All tracked data cleared.");
                return;
            }

            lock (Plugin.SharedUpgradesLock)
            {
                if (Plugin._sharedUpgrades.Count > 0)
                {
                    _pendingReapply = true;
                    _reapplyDelay = REAPPLY_DELAY_SECONDS;
                    Plugin.Logger.LogInfo($"[LuckyUpgrades] Scheduled reapply in {REAPPLY_DELAY_SECONDS}s...");
                }
            }
        }

        if (_pendingReapply)
        {
            _reapplyDelay -= Time.deltaTime;
            if (_reapplyDelay <= 0f)
            {
                _pendingReapply = false;

                if (PhotonNetwork.IsMasterClient)
                {
                    lock (Plugin.SharedUpgradesLock)
                    {
                        if (Plugin._sharedUpgrades.Count == 0)
                        {
                            Plugin.Logger.LogInfo("[LuckyUpgrades] Host with no received upgrades — skipping reapply.");
                            return;
                        }
                    }
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