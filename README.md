# HowsItGoing

`HowsItGoing` is an Android-first Uno app plus a local ASP.NET Core bridge for monitoring Codex activity.

## What It Does

- Lists Codex sessions from the local Codex state database and rollout JSONL files
- Filters sessions by text, status, source, and archived state
- Raises Android device notifications when Codex threads complete
- Monitors the repo's GitHub push events through the local bridge
- Starts new `codex exec` runs in any repo path you send from the app
- Checks GitHub releases for newer APKs and can hand the latest APK to the Android package installer

## Projects

- `HowsItGoing`: Uno app with three heads - Android (`net9.0-android`), desktop (`net9.0-desktop`), and web/WebAssembly (`net9.0-browserwasm`)
- `HowsItGoing.Bridge`: local bridge API that reads Codex state, monitors GitHub, and launches Codex runs
- `HowsItGoing.Contracts`: shared DTOs between app and bridge
- `HowsItGoing.Tests`: app-level tests
- `HowsItGoing.Bridge.Tests`: bridge parsing tests

## Local Run

1. Start the bridge:
   `pwsh ./scripts/run-bridge.ps1`
2. Build or run the app:
   - Android: `dotnet build ./HowsItGoing/HowsItGoing.csproj -f net9.0-android`
     (add `-p:RuntimeIdentifier=android-x64` for the x86_64 emulator)
   - Desktop: `dotnet build ./HowsItGoing/HowsItGoing.csproj -f net9.0-desktop`, then run
     `./HowsItGoing/bin/Debug/net9.0-desktop/HowsItGoing.exe`
   - Web: `dotnet build ./HowsItGoing/HowsItGoing.csproj -f net9.0-browserwasm`, then
     `pwsh ./scripts/serve-wasm.ps1` and open `http://localhost:5219/`
3. In the app, point the bridge URL at:
   - `http://127.0.0.1:5217` when using `adb reverse tcp:5217 tcp:5217` on a physical Android device
   - `http://10.0.2.2:5217` for an Android emulator
   - `http://<your-pc-lan-ip>:5217` for a physical device on the same network
   - `http://127.0.0.1:5217` for the desktop and web heads (the default)

The bridge allows any origin so the browser head can call it; it binds loopback-style
developer tooling only and is not meant to be exposed off the machine.

To keep the bridge running across logons, `pwsh ./scripts/install-bridge-autostart.ps1`
publishes it to `%LOCALAPPDATA%\HowsItGoing\bridge` and starts it at logon. Without a
running bridge the app has no data to show.

## Signed Release Build

The app supports standard .NET Android signing properties through these environment variables:

- `HOWSITGOING_ANDROID_KEYSTORE_PATH`
- `HOWSITGOING_ANDROID_STORE_PASSWORD`
- `HOWSITGOING_ANDROID_KEY_ALIAS`
- `HOWSITGOING_ANDROID_KEY_PASSWORD`

Local signed build:

`pwsh ./scripts/build-android-release.ps1`

GitHub Actions release publishing is defined in `.github/workflows/android-release.yml`.

Required GitHub secrets:

- `ANDROID_KEYSTORE_BASE64`
- `ANDROID_KEYSTORE_PASSWORD`
- `ANDROID_KEY_ALIAS`
- `ANDROID_KEY_PASSWORD`
