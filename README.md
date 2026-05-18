# Live Shelf

Live Shelf is a Windows-native WPF app that turns hidden windows into live task cards on a right-edge shelf.

It is built for people who keep long-running work open in terminals, browsers, editors, media apps, and agent sessions, but do not want those windows taking over the desktop.

## Download

Download the latest `LiveShelf-*-win-x64.zip` from GitHub Releases, extract it, and run `LiveShelf.exe`.

The release build is self-contained for Windows x64. You do not need to install the .NET runtime separately.

Live Shelf is not code-signed yet. Windows SmartScreen may warn the first time you run it.

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

## Usage

- Press `Ctrl+Alt+S` while another normal desktop window is focused to shelf it.
- Click a card to restore its source window.
- Right-click a card to restore, close the source window, or remove the card.
- Press `Ctrl+Alt+H` to hide or show the shelf.
- Press `Ctrl+Alt+Shift+R` to emergency-restore all shelved windows.
- Hover a card to peek; pinch in or use `Ctrl` + mouse wheel up to enter full zoom.

Full zoom forwards mouse, wheel, keyboard, and text input to the source window when possible.

## Features

- DWM live thumbnail cards in a topmost right-edge shelf.
- Animated peek, zoom, and collapsed rail modes.
- Task badges for changed, updated, loading, running, editing, waiting, done, failed, media, and closed states.
- Windows media-session cards through `GlobalSystemMediaTransportControlsSessionManager`.
- Crash and exit recovery for still-shelved windows when their HWND still exists.
- Placement persistence under `%LOCALAPPDATA%\LiveShelf\shelved-windows.json` for recovery after hard crashes.
- Optional Codex and Claude Code tracking through a local bridge and named pipes.

## Agent Tracking

Use the `Agents` menu in the shelf header to enable Codex tracking, Claude Code tracking, or both.

This feature writes user-level hook/config files:

- Codex: `%USERPROFILE%\.codex\config.toml` and `%USERPROFILE%\.codex\hooks.json`
- Claude Code: `%USERPROFILE%\.claude\settings.json`

Live Shelf creates backups before changing supported config files. The bridge executable is copied into `%LOCALAPPDATA%\LiveShelf`.

The Codex bridge command is installed through a small command shim:

```text
cmd.exe /d /c call "%LOCALAPPDATA%\LiveShelf\liveshelf-codex-hook.cmd"
```

The Claude Code bridge command runs the bundled bridge directly:

```text
"%LOCALAPPDATA%\LiveShelf\liveshelf-bridge.exe" --source claude
```

The bridge reads hook JSON from stdin, normalizes it, sends it to the local named pipe `LiveShelfAgentEvents`, and exits. If Live Shelf is closed, it queues events in `%LOCALAPPDATA%\LiveShelf\queued-agent-events.jsonl` for the next launch.

## Status Event Bridge

Browser or native integrations can publish newline-delimited JSON to the local named pipe `LiveShelf.Events`.

```json
{"source":"codex","event":"PreToolUse","tool":"apply_patch","status":"editing_files","filesChanged":4}
{"source":"codex","event":"PermissionRequest","details":"npm install wants permission"}
{"source":"codex","event":"Stop","filesChanged":4}
{"source":"browser","status":"upload_complete","title":"Google Drive"}
```

Events can target a card with `hwnd`, `processId`/`pid`, `processName`, or `windowTitle`/`title`.

## Known Limits

- Elevated/admin windows may not be controllable unless Live Shelf is also elevated.
- Exclusive fullscreen apps, games, DRM video, and some Store/UWP windows are not supported.
- Some apps may stop visually updating when parked offscreen.
- Browser extension and native-messaging packaging are not included yet.
- Browser media matching uses Windows media sessions plus tab/window title matching, not exact tab IDs.
- DRM or custom media players may expose limited metadata, timeline, or controls.
- Interactive full zoom uses Win32 input forwarding. Some elevated, protected, or custom-rendered apps may ignore forwarded input.

## Privacy And Local Data

Live Shelf runs locally. It uses Windows APIs, local named pipes, and local files under `%LOCALAPPDATA%\LiveShelf`.

The app may inspect window titles, process names, selected UI Automation text, and media-session metadata to classify cards. It does not send this information to a remote service.

Runtime exceptions are written to:

```text
%LOCALAPPDATA%\LiveShelf\crash.log
```

## License

MIT. See [LICENSE](LICENSE).
