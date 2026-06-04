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

    // Registry for modded upgrades registered by other mods
    internal static readonly Dictionary<string, (Action<string, int> apply, int chance)> _moddedUpgradeRegistry
        = new Dictionary<string, (Action<string, int>, int)>();

    private Harmony _harmony;

    private void Awake()
    {
        Instance = this;
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

        UpgradeConfiguration = new UpgradeConfig(Config);

        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        _harmony.PatchAll(typeof(Plugin));

        var updateRunner = new GameObject("LuckyUpgrades_UpdateRunner");
        updateRunner.AddComponent<UpgradeReapplyRunner>();
        UnityEngine.Object.DontDestroyOnLoad(updateRunner);
        updateRunner.hideFlags = HideFlags.HideAndDontSave;

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
            int resolvedChance = configEntry?.Value ?? shareChance;

            _moddedUpgradeRegistry[upgradeId] = (applyAction, resolvedChance);
            Logger?.LogInfo($"[LuckyUpgrades] ✓ REGISTERED modded upgrade: '{upgradeId}' ({resolvedChance}% share chance)");
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
        (Action<string, int> apply, int chance) entry;
        
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
            chanceOverride: entry.chance);
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

    [HarmonyPatch(typeof(ItemUpgrade), "PlayUpgrade")]
    [HarmonyPostfix]
    public static void ItemUpgrade_PlayUpgrade_Postfix(ItemUpgrade __instance)
    {
        try
        {
            // Skip if we triggered this call ourselves
            if (Interlocked.CompareExchange(ref _isApplyingSharedUpgrade, 0, 0) == 1) return;

            string mySteamID = GetMySteamID();

            // Identify which upgrade type this is by checking components on the same GameObject
            string upgradeType = GetUpgradeType(__instance);
            Logger.LogDebug($"[LuckyUpgrades] DEBUG PlayUpgrade fired: type='{upgradeType}' mySteamID='{mySteamID}'");

            if (string.IsNullOrEmpty(upgradeType)) return;

            // Get the SteamID of the player who used this item
            string sourceSteamID = GetSteamIDFromItem(__instance);
            Logger.LogDebug($"[LuckyUpgrades] DEBUG sourceSteamID='{sourceSteamID}'");

            if (string.IsNullOrEmpty(sourceSteamID)) return;
            if (string.IsNullOrEmpty(mySteamID)) return;

            // Only react to OTHER players' upgrades
            if (mySteamID == sourceSteamID) return;

            ApplySharedUpgradeToSelf(upgradeType, sourceSteamID, 1,
                applyToSelf: (_) =>
                {
                    string myID = GetMySteamID();
                    if (!string.IsNullOrEmpty(myID))
                        ApplyUpgradeByType(upgradeType, myID);
                });
        }
        catch (Exception ex)
        {
            Logger.LogError($"[LuckyUpgrades] Error in ItemUpgrade_PlayUpgrade_Postfix: {ex.Message}");
        }
    }

    /// <summary>
    /// Maps the concrete component type on the GameObject to our internal upgrade key string.
    /// For vanilla upgrades, checks known ItemUpgrade* component types.
    /// For REPOLib-based modded upgrades (NikkisUpgrades, GoopUpgrades, MoreUpgrades, etc.),
    /// reflects on the PlayerUpgrade component to extract the upgrade ID and auto-registers it.
    /// Returns null if the type is not a recognised player upgrade.
    /// </summary>
    private static string GetUpgradeType(ItemUpgrade item)
    {
        var go = item.gameObject;

        // --- Vanilla upgrades ---
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

        // --- REPOLib PlayerUpgrade (NikkisUpgrades, GoopUpgrades, MoreUpgrades, etc.) ---
        // REPOLib registers upgrades as components whose type name contains "ItemUpgrade"
        // and holds a reference to a PlayerUpgrade scriptable object with an upgradeName / id field.
        string repolibId = TryGetREPOLibUpgradeId(go);
        if (repolibId != null)
        {
            // Auto-register if not yet known so reapply and chance config work too
            lock (ModdedUpgradeRegistryLock)
            {
                if (!_moddedUpgradeRegistry.ContainsKey(repolibId))
                {
                    var configEntry = UpgradeConfiguration?.BindModdedUpgrade(repolibId, 25);
                    int resolvedChance = configEntry?.Value ?? 25;

                    // Capture id for the closure
                    string capturedId = repolibId;
                    _moddedUpgradeRegistry[capturedId] = (
                        (steamID, amount) => ApplyREPOLibUpgrade(capturedId, steamID, amount),
                        resolvedChance
                    );
                    Logger?.LogInfo($"[LuckyUpgrades] Auto-registered REPOLib upgrade: '{capturedId}' ({resolvedChance}% share chance)");
                }
            }
            return repolibId;
        }

        return null;
    }

    /// <summary>
    /// Attempts to extract the upgrade identifier from a REPOLib PlayerUpgrade component
    /// sitting on the same GameObject as the ItemUpgrade.
    ///
    /// REPOLib (Zehs/CritHaxXoG) exposes upgrades via a MonoBehaviour that holds a
    /// PlayerUpgrade (ScriptableObject). The SO has an "upgradeName" or "itemName" string
    /// field we can read via reflection. We fall back to the component's type name if those
    /// fields don't exist so future REPOLib versions are still covered.
    /// </summary>
    private static string TryGetREPOLibUpgradeId(GameObject go)
    {
        try
        {
            var components = go.GetComponents<MonoBehaviour>();
            foreach (var comp in components)
            {
                if (comp == null) continue;
                var compType = comp.GetType();

                // REPOLib upgrade MonoBehaviours typically have "ItemUpgrade" in their type name
                // but are NOT one of the vanilla types we already handled above.
                // They also commonly expose a "playerUpgrade" or "upgrade" field of a REPOLib type.
                var fields = compType.GetFields(
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);

                foreach (var field in fields)
                {
                    // Look for a field whose type name contains "PlayerUpgrade" (the REPOLib SO)
                    if (!field.FieldType.Name.Contains("PlayerUpgrade")) continue;

                    var upgradeObj = field.GetValue(comp);
                    if (upgradeObj == null) continue;

                    // Try to read a string identifier from the SO
                    string id = ReadStringField(upgradeObj, "upgradeName")
                             ?? ReadStringField(upgradeObj, "itemName")
                             ?? ReadStringField(upgradeObj, "name")
                             ?? ReadStringField(upgradeObj, "id");

                    if (!string.IsNullOrEmpty(id))
                    {
                        // Prefix so it never collides with vanilla keys
                        return "REPOLib_" + id;
                    }

                    // Fallback: use the SO's Unity object name
                    if (upgradeObj is UnityEngine.Object uObj && !string.IsNullOrEmpty(uObj.name))
                        return "REPOLib_" + uObj.name;
                }
            }
        }
        catch (Exception ex)
        {
            Logger?.LogWarning($"[LuckyUpgrades] TryGetREPOLibUpgradeId failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Reads a named string field or property from an arbitrary object via reflection.
    /// Returns null if the field/property doesn't exist or is empty.
    /// </summary>
    private static string ReadStringField(object obj, string memberName)
    {
        try
        {
            var type = obj.GetType();

            // Try field first
            var field = type.GetField(memberName,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(string))
            {
                var val = field.GetValue(obj) as string;
                if (!string.IsNullOrEmpty(val)) return val;
            }

            // Try property
            var prop = type.GetProperty(memberName,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(string))
            {
                var val = prop.GetValue(obj) as string;
                if (!string.IsNullOrEmpty(val)) return val;
            }
        }
        catch { /* ignore reflection errors */ }
        return null;
    }

    /// <summary>
    /// Applies a REPOLib-registered upgrade to a player by calling the registered action.
    /// Called both from the auto-registered action closure AND from ReapplySingleUpgrade.
    /// </summary>
    private static void ApplyREPOLibUpgrade(string upgradeId, string steamID, int amount)
    {
        // The registered action in _moddedUpgradeRegistry already knows how to apply.
        // This method is the action body itself — nothing extra to do here.
        // (If a mod called RegisterModdedUpgrade manually, that action takes precedence.)
        Logger?.LogInfo($"[LuckyUpgrades] ApplyREPOLibUpgrade: '{upgradeId}' → {steamID} +{amount}");

        // For auto-registered REPOLib upgrades we don't have a direct PunManager call.
        // We rely on REPOLib's own upgrade-application path via the PlayerUpgrade SO.
        // The correct approach is to find the PlayerUpgrade SO and call its Upgrade() method.
        TryApplyREPOLibUpgradeViaSO(upgradeId, steamID, amount);
    }

    /// <summary>
    /// Finds the REPOLib PlayerUpgrade ScriptableObject by its name and calls its upgrade method.
    /// REPOLib's Upgrades module keeps a registry; we search loaded SOs as a fallback.
    /// </summary>
    private static void TryApplyREPOLibUpgradeViaSO(string upgradeId, string steamID, int amount)
    {
        try
        {
            // Strip our prefix to get the original SO name
            string soName = upgradeId.StartsWith("REPOLib_") ? upgradeId.Substring(8) : upgradeId;

            // REPOLib registers upgrades in REPOLib.Modules.Upgrades. Try to invoke via reflection.
            var repolibUpgradesType = System.Type.GetType("REPOLib.Modules.Upgrades, REPOLib");
            if (repolibUpgradesType != null)
            {
                // Try GetPlayerUpgrade(string name) → PlayerUpgrade
                var getMethod = repolibUpgradesType.GetMethod("GetPlayerUpgrade",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(string) }, null);

                if (getMethod != null)
                {
                    var playerUpgrade = getMethod.Invoke(null, new object[] { soName });
                    if (playerUpgrade != null)
                    {
                        // PlayerUpgrade.Upgrade(string steamID, int amount) or similar
                        var upgradeMethod = playerUpgrade.GetType().GetMethod("Upgrade",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (upgradeMethod == null)
                            upgradeMethod = playerUpgrade.GetType().GetMethod("ApplyUpgrade",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                        if (upgradeMethod != null)
                        {
                            var upgradeParams = upgradeMethod.GetParameters();
                            if (upgradeParams.Length == 2)
                                upgradeMethod.Invoke(playerUpgrade, new object[] { steamID, amount });
                            else if (upgradeParams.Length == 1)
                                upgradeMethod.Invoke(playerUpgrade, new object[] { steamID });
                            Logger?.LogInfo($"[LuckyUpgrades] REPOLib upgrade applied via SO: '{soName}' → {steamID}");
                            return;
                        }
                    }
                }
            }

            // Fallback: search all loaded PlayerUpgrade SOs in memory
            var allUpgrades = Resources.FindObjectsOfTypeAll<ScriptableObject>();
            foreach (var so in allUpgrades)
            {
                if (so == null) continue;
                if (so.name != soName && !so.GetType().Name.Contains("PlayerUpgrade")) continue;

                var upgradeMethod = so.GetType().GetMethod("Upgrade",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (upgradeMethod == null) continue;

                var upgradeParams = upgradeMethod.GetParameters();
                if (upgradeParams.Length == 2)
                    upgradeMethod.Invoke(so, new object[] { steamID, amount });
                else if (upgradeParams.Length == 1)
                    upgradeMethod.Invoke(so, new object[] { steamID });
                Logger?.LogInfo($"[LuckyUpgrades] REPOLib upgrade applied via Resources fallback: '{soName}' → {steamID}");
                return;
            }

            Logger?.LogWarning($"[LuckyUpgrades] Could not apply REPOLib upgrade '{soName}' — no suitable method found. " +
                               $"Call Plugin.RegisterModdedUpgrade(\"{upgradeId}\", ...) from your mod to provide an explicit apply action.");
        }
        catch (Exception ex)
        {
            Logger?.LogError($"[LuckyUpgrades] TryApplyREPOLibUpgradeViaSO failed for '{upgradeId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Applies one stack of the named upgrade to the given player via PunManager (vanilla)
    /// or via the registered action for modded / REPOLib upgrades.
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
                // Modded / REPOLib upgrade — use the registered action
                lock (ModdedUpgradeRegistryLock)
                {
                    if (_moddedUpgradeRegistry.TryGetValue(upgradeType, out var moddedEntry))
                        moddedEntry.apply(steamID, 1);
                    else
                        Logger?.LogWarning($"[LuckyUpgrades] ApplyUpgradeByType: no action registered for '{upgradeType}'");
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
    /// FIXED: Now handles 100% probability and edge cases correctly.
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

            // CRITICAL FIX: Handle 100% probability and edge cases properly
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