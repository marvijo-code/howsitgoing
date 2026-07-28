# HowsItGoing

Monitors Codex / Claude Code / OpenCode agent sessions. Uno app + a local ASP.NET bridge.

> Deep runbook, dated history, and hard-won gotchas live in the central wiki:
> `rg -n -i "howsitgoing" "$env:MEMORIES_WIKI"` → `repos/howsitgoing.md`.
> Uno-specific traps (WASM head, desktop capture) → `concepts/uno-android-gotchas.md`.

## Layout

- `HowsItGoing` - Uno app, three heads: `net9.0-android;net9.0-desktop;net9.0-browserwasm`.
  Platform entry points live in `Platforms/{Android,Desktop,WebAssembly}/`.
- `HowsItGoing.Bridge` - ASP.NET minimal API on **port 5217**. Reads each agent's local state
  (Codex sqlite + rollout JSONL, Claude Code `~/.claude/projects/*.jsonl`, OpenCode sqlite) and
  serves `/api/sessions|notifications|issues|repository/status|update|settings`.
- `HowsItGoing.Contracts` - DTOs shared by app and bridge. `HowsItGoing.Core` - shared services
  (settings, the MySQL shared store). `HowsItGoing.Tests` / `HowsItGoing.Bridge.Tests` - plain net9.0.

## Commands

```
pwsh scripts/install-bridge-autostart.ps1   # publish bridge + run it at logon (do this first)
pwsh scripts/run-bridge.ps1                 # dev bridge (same port - stop the autostarted one)
dotnet build HowsItGoing/HowsItGoing.csproj -f net9.0-desktop      # then run bin/Debug/net9.0-desktop/HowsItGoing.exe
dotnet build HowsItGoing/HowsItGoing.csproj -f net9.0-browserwasm  # then pwsh scripts/serve-wasm.ps1 -> localhost:5219
dotnet build HowsItGoing/HowsItGoing.csproj -f net9.0-android -p:RuntimeIdentifier=android-x64
dotnet test HowsItGoing.Tests; dotnet test HowsItGoing.Bridge.Tests
```

`--no-launch-profile` is a `dotnet run` switch, not a `dotnet build` one.

## Rules / gotchas

- **Nothing renders without a running bridge.** Check `/healthz` first. A hung bridge can keep
  holding port 5217 while answering nothing - find the owner with
  `Get-NetTCPConnection -LocalPort 5217 -State Listen` and kill it.
- **Never let a slow call into the guarded refresh loop unguarded.** `MainViewModel.RefreshAsync`
  publishes sessions + notifications FIRST, then runs GitHub-backed work in `RefreshAuxiliaryAsync`,
  all under a 60s timeout. Awaiting a slow call before publishing re-creates the original bug: the
  banner sticks on "Refreshing…" and the in-flight guard eats every later 30s tick.
- **Adding a TargetFramework? Update `.github/workflows/android-release.yml`.** `dotnet publish -f
  net9.0-android` still *restores* every TFM, so each one's workload must be installed.
- **Android can't read loose `Content` files** - `appsettings.Local.json` is an APK asset there.
  Use `SharedStoreOptions.FromEmbeddedResource` (the Uno SDK already embeds `appsettings*.json`;
  do NOT add an explicit `<EmbeddedResource>` for it - NETSDK1022 duplicate item).
- **The browser has no TCP sockets**, so `SharedStoreOptions.IsConfigured` is false there and the
  web head is bridge-HTTP-only. The bridge allows any origin for that reason.
- **Config paths in the bridge must be absolute once published.** `Bridge:MonitoredRepositoryPath`
  defaults to a relative `".."`; the autostart installer rewrites it to the repo root, otherwise
  repository status, the update check, and the issue board silently return empty.
- `SynchronizeCollection` needs a content-signature selector for record DTOs holding lists
  (issues/PRs), or every row is replaced each refresh and the list scroll resets.
- Screenshot the desktop head with `PrintWindow(hwnd, hdc, 2)` after `SetProcessDPIAware()`.
  `CopyFromScreen` captures whatever window is on top; `MoveWindow` after launch clips the content.
- This checkout is often shared with other agents' worktrees (`.claude/worktrees/`). Stage your
  own files explicitly - never `git add -A`.
