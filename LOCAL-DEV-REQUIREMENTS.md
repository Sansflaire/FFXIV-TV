# FFXIV-TV — Local Development Requirements

Everything you need to build, run, and work on this plugin from scratch on a new machine.

---

## Platform

- **Windows only** — `net10.0-windows`, x64. No Linux/macOS support.
- **Windows 11** recommended (WebView2 is pre-installed; Windows 10 may need a manual runtime install).

---

## 1. Game & Launcher

| Requirement | Notes |
|-------------|-------|
| **Final Fantasy XIV** | Installed and licensed. Any data center. |
| **XIVLauncher** | Latest release from [goatcorp/FFXIVQuickLauncher](https://github.com/goatcorp/FFXIVQuickLauncher). |
| **Dalamud** | Enabled in XIVLauncher settings. Use the **Staging** branch for dev DLLs (`/xlsettings` → Dalamud → Branch: Staging). |
| **Dalamud dev DLLs** | Must exist at `%APPDATA%\XIVLauncher\addon\Hooks\dev\` — Dalamud places them there on first launch with Staging branch. The `.csproj` references them from this path. |

---

## 2. SDK & Build Tools

| Requirement | Version | Notes |
|-------------|---------|-------|
| **.NET SDK** | **10.0** | `dotnet --version` should report `10.x`. Download from [dot.net](https://dot.net). |
| **Visual Studio 2022** or **VS Code + C# Dev Kit** | Latest | VS2022 Community is free. VS Code needs the C# Dev Kit extension. |
| **Git** | Any recent | For cloning and contributing. |

> NuGet packages (Vortice, LibVLCSharp, WebView2, etc.) are all restored automatically on first build — no manual installs needed.

---

## 3. Plugin Setup (XIVLauncher)

After building:

1. Open XIVLauncher → `/xlsettings` → **Experimental** → **Dev Plugin Locations**
2. Add: `%APPDATA%\XIVLauncher\devPlugins\FFXIV-TV\FFXIV-TV.dll`
3. Open `/xlplugins` → find **FFXIV-TV** → enable it.

The post-build target in `src/FFXIV-TV.csproj` automatically copies the DLL and all native dependencies to `%APPDATA%\XIVLauncher\devPlugins\FFXIV-TV\` after every build, so you only need to do this registration once.

**Hot-reload:** Disable the plugin in `/xlplugins`, rebuild, re-enable.

---

## 4. Optional Binaries (not in repo, place in plugin folder)

These go in `%APPDATA%\XIVLauncher\devPlugins\FFXIV-TV\` alongside the DLL.

| Binary | Purpose | Where to get it |
|--------|---------|-----------------|
| `yt-dlp.exe` | YouTube URL support (host only — clients receive a direct stream URL automatically) | [yt-dlp releases](https://github.com/yt-dlp/yt-dlp/releases) — grab `yt-dlp.exe` |
| `cloudflared.exe` | Cloudflare tunnel mode for sync server (no port forwarding required) | [cloudflared releases](https://github.com/cloudflare/cloudflared/releases) — grab `cloudflared-windows-amd64.exe`, rename to `cloudflared.exe` |

Neither is required for core functionality. The plugin works without both — YouTube support and tunnel mode are simply unavailable.

---

## 5. WebView2 Runtime

Required for browser mode (Phase 3.7). **Already pre-installed on Windows 11.** If on Windows 10 and browser mode fails to initialize, download the Evergreen runtime from [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/).

---

## 6. Relay Server (optional — only if self-hosting cloud sync)

Only needed if you want to run your own relay instead of using the deployed fly.io instance.

| Requirement | Version |
|-------------|---------|
| **Node.js** | 18 or later (`node --version`) |
| **npm** | Comes with Node.js |

```bash
cd relay-server
npm install
node server.js        # runs on port 8080
```

For deployment to fly.io:
```bash
npm install -g flyctl
fly auth login
fly deploy            # from relay-server/
```

---

## 7. Build & Run

```bash
git clone https://github.com/Sansflaire/FFXIV-TV.git
cd FFXIV-TV/src
dotnet build
# DLL + native libs auto-copy to devPlugins/FFXIV-TV/
```

Launch FFXIV through XIVLauncher, then use `/fftv` in-game.

---

## 8. Quick Checklist for a Fresh Machine

- [ ] FFXIV installed and working
- [ ] XIVLauncher installed, Dalamud on Staging branch, launched at least once (populates `addon/Hooks/dev/`)
- [ ] .NET 10 SDK installed
- [ ] Repo cloned to anywhere on disk
- [ ] `dotnet build` succeeds from `src/`
- [ ] Plugin DLL registered in XIVLauncher dev plugin locations
- [ ] *(optional)* `yt-dlp.exe` dropped into devPlugins folder
- [ ] *(optional)* `cloudflared.exe` dropped into devPlugins folder
