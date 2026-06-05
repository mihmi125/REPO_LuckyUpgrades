<div align="center">

### 🌐 README Language : [English](README.md) | [한국어](README.ko.md)
<br>

# LuckyUpgrades Fork

> **Note:** This is a modified fork of the original LuckyUpgrades by ataraxia7899. It adds full support for custom/modded upgrades alongside the base game upgrades.

[![Language](https://img.shields.io/badge/Language-C%23-239120?logo=c-sharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![Thunderstore Profile](https://img.shields.io/badge/THUNDERSTORE-PROFILE-blue?logo=thunderstore&logoColor=white)](https://thunderstore.io/c/repo/p/YourUsername/)
[![Thunderstore Version](https://img.shields.io/thunderstore/v/YourUsername/LuckyUpgradesFork?label=THUNDERSTORE&color=00AFEC&logo=thunderstore&logoColor=white)](https://thunderstore.io/c/repo/p/YourUsername/LuckyUpgradesFork/)
[![Thunderstore Downloads](https://img.shields.io/thunderstore/dt/YourUsername/LuckyUpgradesFork?label=DOWNLOADS&color=00FF00&logo=thunderstore&logoColor=white)](https://thunderstore.io/c/repo/p/YourUsername/LuckyUpgradesFork/)

[**R.E.P.O Upgrade Sharing Mod (Thunderstore)**](https://thunderstore.io/c/repo/p/YourUsername/LuckyUpgradesFork/)

When a player picks up an upgrade item, there is a configurable chance that **ALL other players** will also receive the same upgrade.

---
</div>

### 🛠 Tech Stack

| Item | Description |
| :--- | :--- |
| **Language** | C# |
| **Framework** | .NET / BepInEx 5.4.x |
| **Game** | R.E.P.O. (Unity) |
| **Library** | Harmony (for patching) |

---

### ⚠️ Important Notice

> **All players in the lobby MUST have this mod installed for it to work correctly!**

---

### 🎬 Quick Guide

<div align="center">

![LuckyUpgrades Quick Guide](https://raw.githubusercontent.com/ataraxia7899/REPO_LuckyUpgrades/main/QuickGuide.png)

</div>

---

### ✨ Features

* 🧩 **Modded Upgrade Support (NEW)**: Fully supports upgrades added by other mods!
* 🎲 **Probability-based sharing**: Configurable share chance for each upgrade type.
* ⚙️ **Per-upgrade settings**: Set different chances for each upgrade.
* 🔧 **Base Upgrades supported**: All 13 original player upgrades are supported.

---

### 📋 Supported Upgrades

| Upgrade | Config Name | Default |
| :--- | :--- | :--- |
| **Any Modded Upgrade** | *(Dynamically Supported)* | 25% |
| Health | `ChanceToActivatePlayerHealth` | 25% |
| Energy (Stamina) | `ChanceToActivatePlayerEnergy` | 25% |
| Sprint Speed | `ChanceToActivatePlayerSprintSpeed` | 25% |
| Extra Jump | `ChanceToActivatePlayerExtraJump` | 25% |
| Tumble Launch | `ChanceToActivatePlayerTumbleLaunch` | 25% |
| Tumble Climb | `ChanceToActivatePlayerTumbleClimb` | 25% |
| Tumble Wings | `ChanceToActivatePlayerTumbleWings` | 25% |
| Crouch Rest | `ChanceToActivatePlayerCrouchRest` | 25% |
| Grab Range | `ChanceToActivatePlayerGrabRange` | 25% |
| Grab Strength | `ChanceToActivatePlayerGrabStrength` | 25% |
| Grab Throw | `ChanceToActivatePlayerGrabThrow` | 25% |
| Map Player Count | `ChanceToActivateMapPlayerCount` | 25% |
| Death Head Battery | `ChanceToActivateDeathHeadBattery` | 25% |

---

### 📦 Installation

#### **Thunderstore Mod Manager (Recommended)**
1.  Install Thunderstore Mod Manager
2.  Search for **LuckyUpgradesFork** and install
3.  **Ensure all players in your lobby install the mod**

#### **Manual Installation**
1.  BepInEx must be installed
2.  Copy `LuckyUpgradesFork.dll` to `BepInEx/plugins/` folder
3.  Launch the game
4.  **Share the mod with all players in your lobby**

---

### 📝 Changelog

| Version | Changes |
| :--- | :--- |
| **1.0.0** | Initial fork release. Added sharing support for custom/modded upgrades alongside all base game features from the original mod. |