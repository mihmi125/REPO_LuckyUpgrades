using System;
using BepInEx.Configuration;

namespace LuckyUpgrades
{
    /// <summary>
    /// Manages upgrade sharing probability settings.
    /// Defines the chance for each upgrade type to be shared with other players.
    /// </summary>
    public class UpgradeConfig
    {
        // === Built-in Upgrade Chances ===

        /// <summary>Health upgrade share chance.</summary>
        public ConfigEntry<int> ChanceToActivatePlayerHealth { get; private set; }

        /// <summary>Energy (Stamina) upgrade share chance.</summary>
        public ConfigEntry<int> ChanceToActivatePlayerEnergy { get; private set; }

        /// <summary>Sprint Speed upgrade share chance.</summary>
        public ConfigEntry<int> ChanceToActivatePlayerSprintSpeed { get; private set; }

        /// <summary>Extra Jump upgrade share chance.</summary>
        public ConfigEntry<int> ChanceToActivatePlayerExtraJump { get; private set; }

        /// <summary>Tumble Launch upgrade share chance.</summary>
        public ConfigEntry<int> ChanceToActivatePlayerTumbleLaunch { get; private set; }

        /// <summary>Grab Range upgrade share chance.</summary>
        public ConfigEntry<int> ChanceToActivatePlayerGrabRange { get; private set; }

        /// <summary>Grab Strength upgrade share chance.</summary>
        public ConfigEntry<int> ChanceToActivatePlayerGrabStrength { get; private set; }

        /// <summary>Grab Throw upgrade share chance.</summary>
        public ConfigEntry<int> ChanceToActivatePlayerGrabThrow { get; private set; }

        /// <summary>Tumble Climb upgrade share chance.</summary>
        public ConfigEntry<int> ChanceToActivatePlayerTumbleClimb { get; private set; }

        /// <summary>Tumble Wings upgrade share chance.</summary>
        public ConfigEntry<int> ChanceToActivatePlayerTumbleWings { get; private set; }

        /// <summary>Crouch Rest upgrade share chance.</summary>
        public ConfigEntry<int> ChanceToActivatePlayerCrouchRest { get; private set; }

        /// <summary>Death Head Battery upgrade share chance.</summary>
        public ConfigEntry<int> ChanceToActivateDeathHeadBattery { get; private set; }

        /// <summary>Map Player Count upgrade share chance.</summary>
        public ConfigEntry<int> ChanceToActivateMapPlayerCount { get; private set; }

        // === Modded Upgrade Defaults ===

        /// <summary>
        /// Default share chance used for modded upgrades that don't supply their own shareChance
        /// in RegisterModdedUpgrade(). Also applied to any unrecognised upgrade type as a safety net.
        /// </summary>
        public ConfigEntry<int> DefaultModdedUpgradeChance { get; private set; }

        /// <summary>Whether the in-game upgrade notification toasts are shown.</summary>
        public ConfigEntry<bool> ShowUpgradeNotifications { get; private set; }

        /// <summary>How long (in seconds) each notification holds on screen before fading out.</summary>
        public ConfigEntry<float> NotificationDisplayTime { get; private set; }

        /// <summary>Show the name of the player whose pickup triggered the roll.</summary>
        public ConfigEntry<bool> ShowSourcePlayerName { get; private set; }

        /// <summary>Show a toast to the player who picked up an upgrade, telling them it's being shared.</summary>
        public ConfigEntry<bool> ShowPickerToast { get; private set; }

        /// <summary>Show win/loss streak counter on the toast (e.g. "3 in a row!").</summary>
        public ConfigEntry<bool> ShowStreakCounter { get; private set; }

        /// <summary>Show a session summary toast when returning to the lobby.</summary>
        public ConfigEntry<bool> ShowSessionSummary { get; private set; }

        /// <summary>
        /// Minimum seconds that must pass before the same upgrade type can trigger
        /// another share roll. Prevents rapid-fire duplicate toasts from physics jitter.
        /// </summary>
        public ConfigEntry<float> UpgradeCooldownSeconds { get; private set; }

        /// <summary>
        /// When enabled, each player rolls independently (original behaviour).
        /// When disabled, the host rolls once and all players share the same outcome.
        /// Requires all players to have the mod — toggle via config.
        /// </summary>
        public ConfigEntry<bool> IndependentRolls { get; private set; }

        private readonly ConfigFile _config;

        /// <summary>
        /// Initializes the config file and binds all settings.
        /// BepInEx writes sections to the .cfg file in the order they are first bound,
        /// so we bind Notifications and Gameplay first so they appear at the top of the
        /// file, then the per-upgrade chances (Upgrades + ModdedUpgrades) at the bottom.
        /// </summary>
        public UpgradeConfig(ConfigFile config)
        {
            _config = config;

            // ── Notification settings (bound first → appear at top of .cfg) ──────

            ShowUpgradeNotifications = config.Bind(
                "Notifications",
                "ShowUpgradeNotifications",
                true,
                "Show an in-game toast notification when an upgrade is shared or missed. Set false to disable."
            );

            NotificationDisplayTime = config.Bind(
                "Notifications",
                "NotificationDisplayTime",
                3.0f,
                new ConfigDescription(
                    "How long (in seconds) each upgrade notification stays on screen before fading out. " +
                    "Does not include the 0.45s fade animation.",
                    new AcceptableValueRange<float>(0.5f, 15f)
                )
            );

            ShowSourcePlayerName = config.Bind(
                "Notifications",
                "ShowSourcePlayerName",
                true,
                "Show the name of the player whose pickup triggered the share roll on the toast notification."
            );

            ShowPickerToast = config.Bind(
                "Notifications",
                "ShowPickerToast",
                true,
                "Show a toast to the player who picked up the upgrade, telling them the share roll is happening."
            );

            ShowStreakCounter = config.Bind(
                "Notifications",
                "ShowStreakCounter",
                true,
                "Show a win/loss streak counter on the toast (e.g. '3 wins in a row!'). Resets on session end."
            );

            ShowSessionSummary = config.Bind(
                "Notifications",
                "ShowSessionSummary",
                true,
                "Show a summary toast when returning to the lobby listing how many upgrades you received this run."
            );

            // ── Gameplay settings ────────────────────────────────────────────────

            UpgradeCooldownSeconds = config.Bind(
                "Gameplay",
                "UpgradeCooldownSeconds",
                1.5f,
                new ConfigDescription(
                    "Minimum seconds between share rolls for the same upgrade type. " +
                    "Prevents duplicate toasts caused by physics jitter firing the pickup twice. " +
                    "Set to 0 to disable.",
                    new AcceptableValueRange<float>(0f, 10f)
                )
            );

            IndependentRolls = config.Bind(
                "Gameplay",
                "IndependentRolls",
                true,
                "When true (default), each player rolls their own dice independently — you may get the upgrade " +
                "while a teammate doesn't. When false, everyone in the lobby shares the same outcome. " +
                "All players must have the same setting for consistent behaviour."
            );

            // ── Built-in upgrade chances (bound last → appear at bottom of .cfg) ─

            ChanceToActivatePlayerHealth = Bind("ChanceToActivatePlayerHealth",
                "% Chance to share the Health upgrade");

            ChanceToActivatePlayerEnergy = Bind("ChanceToActivatePlayerEnergy",
                "% Chance to share the Energy (Stamina) upgrade");

            ChanceToActivatePlayerSprintSpeed = Bind("ChanceToActivatePlayerSprintSpeed",
                "% Chance to share the Sprint Speed upgrade");

            ChanceToActivatePlayerExtraJump = Bind("ChanceToActivatePlayerExtraJump",
                "% Chance to share the Extra Jump upgrade");

            ChanceToActivatePlayerTumbleLaunch = Bind("ChanceToActivatePlayerTumbleLaunch",
                "% Chance to share the Tumble Launch upgrade");

            ChanceToActivatePlayerGrabRange = Bind("ChanceToActivatePlayerGrabRange",
                "% Chance to share the Grab Range upgrade");

            ChanceToActivatePlayerGrabStrength = Bind("ChanceToActivatePlayerGrabStrength",
                "% Chance to share the Grab Strength upgrade");

            ChanceToActivatePlayerGrabThrow = Bind("ChanceToActivatePlayerGrabThrow",
                "% Chance to share the Grab Throw upgrade");

            ChanceToActivatePlayerTumbleClimb = Bind("ChanceToActivatePlayerTumbleClimb",
                "% Chance to share the Tumble Climb upgrade");

            ChanceToActivatePlayerTumbleWings = Bind("ChanceToActivatePlayerTumbleWings",
                "% Chance to share the Tumble Wings upgrade");

            ChanceToActivatePlayerCrouchRest = Bind("ChanceToActivatePlayerCrouchRest",
                "% Chance to share the Crouch Rest upgrade");

            ChanceToActivateDeathHeadBattery = Bind("ChanceToActivateDeathHeadBattery",
                "% Chance to share the Death Head Battery upgrade");

            ChanceToActivateMapPlayerCount = Bind("ChanceToActivateMapPlayerCount",
                "% Chance to share the Map Player Count upgrade");

            // ── Modded upgrade fallback ──────────────────────────────────────────

            DefaultModdedUpgradeChance = config.Bind(
                "ModdedUpgrades",
                "DefaultModdedUpgradeChance",
                25,
                new ConfigDescription(
                    "Default % chance used for modded upgrades that don't specify their own share chance. " +
                    "Also used as a fallback for any unrecognised upgrade type.",
                    new AcceptableValueRange<int>(0, 100)
                )
            );
        }

        // -------------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------------

        /// <summary>
        /// Returns the share chance for a built-in upgrade type (matched by its internal key,
        /// e.g. "Health", "Energy"). Falls back to DefaultModdedUpgradeChance for unknown types.
        /// </summary>
        public int GetShareChance(string upgradeType)
        {
            switch (upgradeType)
            {
                case "Health":        return ChanceToActivatePlayerHealth.Value;
                case "Energy":        return ChanceToActivatePlayerEnergy.Value;
                case "SprintSpeed":   return ChanceToActivatePlayerSprintSpeed.Value;
                case "ExtraJump":     return ChanceToActivatePlayerExtraJump.Value;
                case "TumbleLaunch":  return ChanceToActivatePlayerTumbleLaunch.Value;
                case "TumbleClimb":   return ChanceToActivatePlayerTumbleClimb.Value;
                case "TumbleWings":   return ChanceToActivatePlayerTumbleWings.Value;
                case "CrouchRest":    return ChanceToActivatePlayerCrouchRest.Value;
                case "GrabRange":     return ChanceToActivatePlayerGrabRange.Value;
                case "GrabStrength":  return ChanceToActivatePlayerGrabStrength.Value;
                case "GrabThrow":     return ChanceToActivatePlayerGrabThrow.Value;
                case "MapPlayerCount":    return ChanceToActivateMapPlayerCount.Value;
                case "DeathHeadBattery":  return ChanceToActivateDeathHeadBattery.Value;

                default:
                    // Unknown type — log once and return the configurable default
                    Plugin.Logger?.LogWarning(
                        $"[LuckyUpgrades] GetShareChance: unknown upgrade type '{upgradeType}'. " +
                        $"Using DefaultModdedUpgradeChance ({DefaultModdedUpgradeChance.Value}%).");
                    return DefaultModdedUpgradeChance.Value;
            }
        }

        /// <summary>
        /// Dynamically adds a config entry for a modded upgrade so players can tweak
        /// its share chance in the cfg file, just like built-in upgrades.
        /// Returns the bound ConfigEntry so the caller can read it later.
        ///
        /// BUG FIX: BepInEx's reset-to-default always uses 25 (literal constant), never
        /// the caller's initialChance. Previously BindModdedUpgrade passed
        /// DefaultModdedUpgradeChance.Value as the BepInEx defaultValue, so if that
        /// setting was e.g. 100 at bind time, every auto-registered modded upgrade
        /// would reset to 100 instead of 25.
        /// </summary>
        public ConfigEntry<int> BindModdedUpgrade(string upgradeId, int initialChance = 25)
        {
            initialChance = Math.Max(0, Math.Min(100, initialChance));
            const int RESET_DEFAULT = 25;

            var entry = _config.Bind(
                "ModdedUpgrades",
                upgradeId,
                RESET_DEFAULT,
                new ConfigDescription(
                    $"% Chance to share the '{upgradeId}' upgrade (added by another mod). Default: {RESET_DEFAULT}%.",
                    new AcceptableValueRange<int>(0, 100)
                )
            );

            // If the entry was just created (value equals the BepInEx default we passed),
            // write the caller's initialChance so first-launch respects DefaultModdedUpgradeChance.
            if (entry.Value == RESET_DEFAULT && initialChance != RESET_DEFAULT)
                entry.Value = initialChance;

            return entry;
        }

        // -------------------------------------------------------------------------
        // Private helper
        // -------------------------------------------------------------------------

        private ConfigEntry<int> Bind(string key, string description, int defaultValue = 25)
        {
            return _config.Bind(
                "Upgrades",
                key,
                defaultValue,
                new ConfigDescription(description, new AcceptableValueRange<int>(0, 100))
            );
        }
    }
}