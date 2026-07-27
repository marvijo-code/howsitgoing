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
   - `http://127.0.0.1:5217` for the desktop and web heads (the default)
   - a LAN address only after reading [Security](#security) below

To keep the bridge running across logons, `pwsh ./scripts/install-bridge-autostart.ps1`
publishes it to `%LOCALAPPDATA%\HowsItGoing\bridge` and starts it at logon. Without a
running bridge the app has no data to show.

## Security

**Treat the bridge as a remote shell on your machine.** It reads every local agent transcript
and starts agents with approval prompts disabled (`codex --full-auto`,
`claude --dangerously-skip-permissions`, `opencode --auto`). Anyone who can reach it can read
your sessions and run code as you.

It therefore ships closed:

- **Loopback by default.** `Bridge:Urls` is `http://127.0.0.1:5217`. `adb reverse`, the emulator,
  and the desktop/web heads all work over loopback with no extra setup.
- **Non-loopback callers need a token.** With no `Bridge:AccessToken` set, `/api` is served to
  loopback only. Set a token and every `/api` request must carry
  `Authorization: Bearer <token>`. `/healthz` stays open so the app can discover the bridge.
- **Origins are allow-listed.** Only `Bridge:AllowedOrigins` (the WASM head) may call it from a
  browser. A wildcard would let any page you visit drive the bridge, and in loopback mode those
  requests arrive from `127.0.0.1` and would be trusted - that is a DNS-rebinding drive-by.
- **Workspaces are allow-listed.** An agent may only be launched inside
  `Bridge:AllowedRepositoryRoots`, which defaults to the parent of the monitored repository.
- **Agents are never launched through a shell.** npm `.cmd` shims are dereferenced to the real
  executable and started with an argument vector, so no request value can be re-parsed as a
  command. Session ids, models, and reasoning efforts are validated before reaching a CLI.

### Pairing a device

Devices authenticate with a per-device token, but you never type one. Pairing hands it over:

1. On the machine running the bridge, open **Settings -> Show a pairing code**. You get an
   8-character code, good for 5 minutes.
2. On the phone (or any other device), open **Settings**, type the code, and press **Pair**.
3. The bridge issues that device a 32-byte token, which the app stores and sends from then on.

The short code is only a bootstrap credential and is safe because it is tightly bounded: it only
exists while you have a pairing window open, it expires after 5 minutes, it is destroyed on first
use, and five wrong guesses close the window. Guessing 8 characters inside that budget is not a
viable attack. The token it issues is what actually authenticates, and the bridge stores only a
hash of it - the `paired-devices.json` file cannot be replayed as a credential if it leaks.

Manage paired devices with `GET /api/pair/devices` and revoke one with
`DELETE /api/pair/devices/{deviceId}`.

### Reaching the bridge from another device

Pairing gets a device a token, but the bridge still has to be reachable and willing to answer.
In `HowsItGoing.Bridge/appsettings.Local.json`:

```json
{
  "AllowedHosts": "localhost;127.0.0.1;[::1];10.0.2.2;192.168.1.50",
  "Bridge": {
    "Urls": "http://0.0.0.0:5217",
    "AllowedRepositoryRoots": [ "C:\\dev" ]
  }
}
```

`Urls` opens the socket, and your machine's address must be in `AllowedHosts` or the host filter
rejects the request with an opaque `400` before authentication runs. `Bridge:AccessToken` is still
supported as a static shared secret, but pairing is the better path - it gives each device its own
revocable token instead of one secret copied everywhere.

Only do this on a network you trust: the traffic is plain HTTP and includes transcript text, so a
WireGuard/Tailscale address beats a raw LAN IP.

`appsettings.Local.json` is gitignored - keep the token out of source control. Note that the Uno
SDK embeds `appsettings*.json` into the app assembly, so a locally built APK carries whatever is
in the app's copy of that file; do not put the bridge token or the shared-store connection string
in `HowsItGoing/appsettings.Local.json` if you plan to share that APK.

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
