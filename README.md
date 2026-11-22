# BG3 Modpack Manager

A portable Windows desktop utility for managing multiple **Baldur's Gate 3** modpack profiles, auditing **BG3ModManager** load orders against live `.pak` files, detecting override mods, and installing NexusMods archives with single-copy deduplication.

## Key Features

- **Single-Copy Mod Library & Vault (`Mods-off` Storage)**
  - Every unique mod is indexed by **SHA-256 content hash** (distinguishing variants that even share both filename and UUID, such as KAZSTRA `unique_tav.pak` vs original `unique_tav.pak`).
  - Shared mods across profiles stay in place on disk during profile switches (zero redundant copying).
- **Automatic Override Detection (`LSPK` v15/v16/v18)**
  - Reads `.pak` manifests and `meta.lsx` directly in pure C# to flag mods that load without being listed in `modsettings.lsx` (e.g. missing `meta.lsx`, or overriding `Public/<BaseModule>/`, `Mods/<BaseModule>/`, `Video/`).
  - Ignores harmless custom icon packs under `Public/Game/GUI/Assets/...`.
- **BG3ModManager Load Order Review**
  - Cross-checks any BG3MM `.json` load order against your live `Mods` folder, Vault, and `modsettings.lsx`.
  - Highlights `Active`, `Active Override`, `In Vault`, `Missing`, and `Unlisted Override` `.pak` files with one-click cleanup to the Vault.
- **Drag & Drop NexusMods Import (`.zip`, `.7z`, `.rar`, `.pak`)**
  - Drop one or multiple `.zip`, `.7z`, `.rar`, `.pak`, or unpacked folders anywhere onto the window to inspect contents and route `.pak` files to `Mods/`, `Generated`/`Public`/`Localization`/`Video` folders to BG3 `Data/`, and `bin`/`NativeMods` files (e.g., NativeModLoader, Native Camera Tweaks, WASD) to the BG3 game folder.
  - Offers a single batch confirmation after unpacking/importing to optionally delete the original archive file(s).
  - Records a full manifest of tracked loose files (or bundles `.pak` + `Data/` together as a combined mod), renames replaced original game files in place (`.bg3mpm_orig`), and restores those originals automatically when deactivating or uninstalling via **Uninstall Mod**.
  - Drop a BG3MM `.json` onto the window to jump directly into Load Order Review.
- **Baldur's Gate 3 Running Guard (Vulkan & DX11)**
  - Monitors both `bg3.exe` and `bg3_dx11.exe` and blocks file-moving operations (profile switches, active imports, moves to/from Vault, uninstalls) until the game is closed.
- **Configurable Paths & Modes**
  - Auto-detects BG3 `Mods`, `Game`, `Data`, `modsettings.lsx`, and BG3ModManager `Orders` folders while letting you override any path in **Settings**.
  - Supports **Full Profile Switch** and **Overrides & Data Only** modes, plus optional automatic `modsettings.lsx` synchronization (creating a `.bak` backup first).
- **Custom Dark & Light Themes + Multi-Language Support**
  - Built-in **Dark** and **Light** palettes switchable live from the top bar or **Settings**.
  - Built-in **English (`en`)** and **Hungarian (`hu`)** translations, plus extensible drop-in JSON language packs: click **Export Template** in Settings to write `languages\template_en.json`, copy it to `<code>.json` (e.g. `de.json`), translate, and click **Reload**.

## Portable Layout

After running `publish.ps1`, copy `dist\BG3ModpackManager.exe` to any folder (ideally on the same drive as your BG3 folders for instant atomic file moves):

- `BG3ModpackManager.exe` — portable application executable (`.NET 8` Windows Desktop runtime required)
- `data\settings.json` — saved folder paths and switch mode
- `data\library.json` — single-copy mod manifests, loose file tracking, and profiles
- `data\pak-cache.json` — cached LSPK metadata and hashes for fast scans
- `Vault\` — default `mods-off` storage folder for inactive PAKs and loose `Data`/`Game` mods (customizable in Settings)

## Building & Publishing

```powershell
# Build entire solution
dotnet build BG3ModpackManager.sln -c Release

# Run automated sandbox tests
dotnet run --project tests\BG3ModpackManager.Tests\BG3ModpackManager.Tests.csproj -c Release

# Publish portable single-file executable to .\dist\
.\publish.ps1
```
