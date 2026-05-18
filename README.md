# Live Shelf

> [!WARNING]
> Live Shelf is an early Windows beta. It works well enough to publish and test publicly, but it is not production-grade yet.

<p align="center"><strong>A Windows-native shelf for hiding active windows without losing sight of them.</strong></p>

Live Shelf turns normal desktop windows into live task cards on a right-edge shelf. It is built for long-running terminals, browsers, editors, media apps, and coding-agent sessions that you want nearby, but not sitting on top of your workspace.

<p align="center">
  <img src="assets/rail.png" alt="Live Shelf collapsed rail mode" width="360" />
  <br />
  <em>Collapsed rail mode</em>
</p>

<p align="center">
  <img src="assets/shelf.png" alt="Live Shelf expanded shelf with live task cards" width="720" />
  <br />
  <em>Expanded shelf mode</em>
</p>

## What It Does

- Shelves the focused window with `Ctrl+Alt+S`.
- Keeps live DWM thumbnail previews in a compact right-edge shelf.
- Collapses into a thin rail when idle.
- Restores, removes, or closes shelved windows from each card.
- Supports peek and full zoom modes for larger previews.
- Shows status badges for changed, running, waiting, done, failed, media, and closed states.
- Tracks Windows media sessions for playback/status cards.
- Restores still-existing shelved windows after crashes or forced exits.
- Optionally tracks Codex and Claude Code through local hooks and named pipes.

## Download

Download the latest `LiveShelf-*-win-x64.zip` from [GitHub Releases](https://github.com/ebanez8/liveshelf/releases), extract it, and run:

```text
LiveShelf.exe
```

The Windows x64 release is self-contained, so the .NET runtime does not need to be installed separately.

Live Shelf is not code-signed yet. Windows SmartScreen may warn on first launch.

## Usage

- `Ctrl+Alt+S`: shelf the currently focused app window.
- `Ctrl+Alt+H`: hide or show the shelf.
- `Ctrl+Alt+Shift+R`: emergency-restore all shelved windows.
- Click a card to restore the source window.
- Right-click a card to restore, close, or remove it.
- Hover a card to peek.
- Pinch in or use `Ctrl` + mouse wheel up to enter full zoom.

Full zoom forwards mouse, wheel, keyboard, and text input to the source window when possible.

## Agent Tracking

Use the `Agents` menu in the shelf header to enable Codex tracking, Claude Code tracking, or both.

Live Shelf writes user-level hook/config files and creates backups before changing supported files:

- Codex: `%USERPROFILE%\.codex\config.toml` and `%USERPROFILE%\.codex\hooks.json`
- Claude Code: `%USERPROFILE%\.claude\settings.json`

The bridge executable is copied into `%LOCALAPPDATA%\LiveShelf`. Hook events are normalized and sent to the local named pipe `LiveShelfAgentEvents`. If Live Shelf is closed, events are queued in `%LOCALAPPDATA%\LiveShelf\queued-agent-events.jsonl` for the next launch.

## Local Event Bridge

Browser or native integrations can publish newline-delimited JSON to the local named pipe `LiveShelf.Events`.

```json
{"source":"codex","event":"PreToolUse","tool":"apply_patch","status":"editing_files","filesChanged":4}
{"source":"codex","event":"PermissionRequest","details":"npm install wants permission"}
{"source":"codex","event":"Stop","filesChanged":4}
{"source":"browser","status":"upload_complete","title":"Google Drive"}
```

Events can target a card with `hwnd`, `processId`/`pid`, `processName`, or `windowTitle`/`title`.

## Build From Source

Requirements:

- Windows 10 version 1809 or newer.
- .NET 8 SDK.

```powershell
dotnet build LiveShelf.sln
dotnet run --project src\LiveShelf\LiveShelf.csproj
```

Run smoke tests:

```powershell
dotnet run --project tests\LiveShelf.SmokeTests\LiveShelf.SmokeTests.csproj
```

Create a local release build:

```powershell
dotnet publish src\LiveShelf\LiveShelf.csproj -c Release -r win-x64 --self-contained true -p:PublishDir="$PWD\artifacts\publish\LiveShelf-win-x64\"
```

## Known Limits

- Elevated/admin windows may not be controllable unless Live Shelf is also elevated.
- Exclusive fullscreen apps, games, DRM video, and some Store/UWP windows are not supported.
- Some apps may stop visually updating when parked offscreen.
- Browser extension and native-messaging packaging are not included yet.
- Browser media matching uses Windows media sessions plus tab/window title matching, not exact tab IDs.
- DRM or custom media players may expose limited metadata, timeline, or controls.
- Interactive full zoom uses Win32 input forwarding. Some elevated, protected, or custom-rendered apps may ignore forwarded input.

## Privacy

Live Shelf runs locally. It uses Windows APIs, local named pipes, and local files under `%LOCALAPPDATA%\LiveShelf`.

The app may inspect window titles, process names, selected UI Automation text, and media-session metadata to classify cards. It does not send this information to a remote service.

Runtime exceptions are written to:

```text
%LOCALAPPDATA%\LiveShelf\crash.log
```

## License

MIT. See [LICENSE](LICENSE).
