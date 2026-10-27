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

- **The session feed is paged, and the Sessions tab uses the page-level ScrollViewer.** `/api/sessions?offset=&limit=&cursor=`  returns `SessionPageDto` (items + total + next cursor); the shared MySQL mirror uses SQL `LIMIT`
  and a keyset cursor (`updated_at desc, id`), because `OFFSET 12000` cost 3.4s. The app shows
  **10 cards per page** with Newer/Older paging: an ItemsControl realises every card, and 30 rich
  cards were enough to block the phone's UI thread past Android's 5s input timeout (a real ANR).
  A **nested** ScrollViewer (a sessions-only scroller inside a Grid row) would not receive touch on
  the OPPO at all - text boxes inside it never focused and drags did nothing - so all four panels
  stay in the one page-level `PanelScroller`. Do not reintroduce a per-tab ScrollViewer or raise the
  page size without testing on the phone.
- **The thread viewer reads transcripts on demand, never on a tick.**
  `/api/sessions/thread?agent=&sessionId=&limit=&before=` returns a `SessionThreadDto`: one
  newest-anchored window plus `startIndex`/`totalCount`, so the app can say "40 of 146" and ask for
  the window before `startIndex`. `SessionThreadService` parses the Codex rollout JSONL
  (`response_item` role user/assistant, `event_msg` for older files), Claude project JSONL and the
  OpenCode `session_message`/`part` tables, drops injected machine turns (`<heartbeat>`,
  `environment_context`, `system-reminder`, the `AGENTS.md` preamble) and collapses consecutive tool
  rows into one "Ran a, b" line. A Codex rollout reaches 25 MB, so this endpoint stays out of
  `RefreshAsync` and out of the 4-second aggregator cache.
- **The Sessions filter panel is collapsed by default, and the search box lives inside it.** With
  the TextBox always on screen, a drag that ended near the top of the feed landed on the field and
  raised the keyboard - the "scrolling to the top randomly opens search" report. Keep the top of
  the feed inert (header row + status pills only) and release focus (`ReleaseFocus`) after Apply,
  paging and tab switches.
- **Schema changes in `SharedBridgeStore` must be guarded by information_schema checks.** An
  unconditional `alter table ... modify column` runs on every bridge start; on a 13k-row mirror that
  made the first page take 5s. `EnsureIndexAsync`/`ColumnExistsAsync` only change what is missing.
- **Android alert settings fingerprint the notification channel id** (`AndroidNotifications.
  GetEventChannelId`). A channel's sound is immutable after creation, so changing the sound must
  create a new channel and retire the old `howsitgoing-events-*` ones - editing the existing channel
  silently does nothing.
- **Nothing renders without a running bridge.** Check `/healthz` first. A hung bridge can keep
  holding port 5217 while answering nothing - find the owner with
  `Get-NetTCPConnection -LocalPort 5217 -State Listen` and kill it.
- **The refresh timer must stay wall-clock driven, never one tick per interval.** `MainPage` ticks
  every 5s and each tick asks whether 30s has elapsed. A 30s-interval timer looks perfect in a
  foreground tab, but mobile browsers throttle background timers to ~1/min and freeze them on
  tab-hide/screen-lock, so the web head came back stale after every phone unlock.
  `WebLifecycleCoordinator` (`howsitgoing-lifecycle.js`, a polled resume counter) additionally
  refreshes on `visibilitychange`/`focus`/`pageshow`/`online`. **Testing this needs the page
  actually backgrounded** - redefine `document.visibilityState`/`hidden` and dispatch
  `visibilitychange`; a plain headless tab will green-light the bug.
- **Phone layout lives in the `AdaptiveTrigger` states on `RootLayout` (640px breakpoint).** Narrow
  halves the gutters, collapses the Refresh label to its glyph, and caps `StatusCounts.MaxWidth`.
  Do NOT "fix" `HorizontalAlignment="Stretch"` + `MaxWidth` to `Center` - Stretch already centres
  once MaxWidth binds, whereas Center shrink-wraps to content and stops filling a phone screen.
  Header text needs `TextWrapping="NoWrap"`; `TextTrimming` alone still wraps and grows the header.
- **Browser-only code goes behind `#if __WASM__`** (`WebPushCoordinator`, `WebLifecycleCoordinator`)
  with `#pragma warning disable CA1416` - Uno reports its platform as "BrowserWasm", not "browser",
  so every `[JSImport]` call is flagged unreachable. Static web files belong in
  `Platforms/WebAssembly/wwwroot/`; a project-level `wwwroot/` is not deployed. VAPID keys stay in
  the gitignored `appsettings.Local.json` - never commit key material.
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
