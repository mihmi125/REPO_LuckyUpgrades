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

        private readonly ConfigFile _config;

        /// <summary>
        /// Initializes the config file and binds all settings.
        /// </summary>
        public UpgradeConfig(ConfigFile config)
        {
            _config = config;

            // Built-in upgrades
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

            // Modded upgrade fallback
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
        /// This is called automatically by Plugin.RegisterModdedUpgrade when
        /// createConfigEntry is true (the default).
        /// </summary>
        public ConfigEntry<int> BindModdedUpgrade(string upgradeId, int defaultChance = 25)
        {
            defaultChance = Math.Max(0, Math.Min(100, defaultChance));

            return _config.Bind(
                "ModdedUpgrades",
                upgradeId,
                defaultChance,
                new ConfigDescription(
                    $"% Chance to share the '{upgradeId}' upgrade (added by another mod)",
                    new AcceptableValueRange<int>(0, 100)
                )
            );
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