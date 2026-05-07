# Live Shelf

Live Shelf is a Windows-native WPF prototype that shelves the current foreground window into a right-edge live card using DWM thumbnails.

Live Shelf turns hidden windows into live task cards.

## Run

```powershell
dotnet run --project src\LiveShelf\LiveShelf.csproj
```

Press `Ctrl+Alt+S` while another normal desktop window is focused. The window is parked offscreen, a live thumbnail card appears on the shelf, and clicking the card restores the original window placement. Press `Ctrl+Alt+H` to hide or show the shelf. Press `Ctrl+Alt+Shift+R` to emergency-restore all shelved windows.

## MVP Behavior

- Hotkey shelving with `Ctrl+Alt+S`.
- Shelf hide/show with `Ctrl+Alt+H`.
- Emergency restore with `Ctrl+Alt+Shift+R`.
- The shelf stays hidden until at least one window is shelved.
- DWM live thumbnail cards in a topmost right-edge shelf.
- Animated hover peek that widens the shelf and enlarges the live preview.
- Hover a card and pinch in or use `Ctrl` + mouse wheel up to full zoom.
- Full zoom automatically enters interactive mode, forwarding mouse, wheel, keyboard, and text input to the source window.
- Pinch out and `Ctrl` + mouse wheel down are ignored by the shelf while zoomed, so those gestures go to the source app where possible.
- Moving the mouse outside the shelf automatically exits full zoom and collapses the peek.
- Cards show task badges for changed, updated, loading, running, editing files, running command, waiting for approval, done, done-needs-review, failed, closed, needs-input, playing, paused, upload-complete, and error states.
- Cards subtly pulse when a shelved window title changes, closes, restores itself, or gets focus.
- Agent Cards can receive direct lifecycle events from Codex and Claude Code through the bundled `liveshelf-bridge.exe`, the `LiveShelfAgentEvents` named pipe, and automatic session linking. Once a card is linked to a hook session, hook state is the source of truth for agent badges.
- Media Cards use Windows System Media Transport Controls through `GlobalSystemMediaTransportControlsSessionManager`. They show playing/paused state, track/video metadata, and estimated timeline progress.
- Smart Cards use softer layered detection from window titles and UI Automation text. Browser/native integrations can also publish status events to `LiveShelf.Events` for loading, updated, done, needs-input, playing, paused, upload-complete, and error badges.
- Click to restore.
- Right-click actions for restore, close source, and remove card.
- On app exit or handled UI crash, still-shelved windows are restored to their original placement/state when their HWND still exists.
- Shelved window placement is also persisted under `%LOCALAPPDATA%\LiveShelf\shelved-windows.json`, so the next launch can restore windows left behind by a hard crash.

## Agent Tracking

Use the `Agents` menu in the shelf header to enable Codex tracking, Claude tracking, or both. Live Shelf copies the bundled bridge files into `%LOCALAPPDATA%\LiveShelf` and writes the user-level hook config files:

- Codex: `%USERPROFILE%\.codex\config.toml` and `%USERPROFILE%\.codex\hooks.json`
- Claude Code: `%USERPROFILE%\.claude\settings.json`

Codex and Claude hooks run:

```text
"%LOCALAPPDATA%\LiveShelf\liveshelf-bridge.exe" --source codex
"%LOCALAPPDATA%\LiveShelf\liveshelf-bridge.exe" --source claude
```

The bridge reads hook JSON from stdin, normalizes it, sends it to the local named pipe `LiveShelfAgentEvents`, and exits. If Live Shelf is closed, the bridge appends JSONL events to `%LOCALAPPDATA%\LiveShelf\queued-agent-events.jsonl`; Live Shelf drains that queue on launch.

Live Shelf keeps a registry keyed by `source:sessionId`. It auto-links sessions to shelved terminal cards by scoring cwd, title/project name, agent hints, terminal process type, and recency. A confident winner links automatically; ambiguous matches surface a small status prompt instead of guessing. After linking, OCR/window text no longer creates agent badges for that card, and alerts only fire for waiting-for-approval, done, or final failed states.

## Status Event Bridge

Browser/native integrations can still send newline-delimited JSON to the local named pipe `LiveShelf.Events`.

```json
{"source":"codex","event":"PreToolUse","tool":"apply_patch","status":"editing_files","filesChanged":4}
{"source":"codex","event":"PermissionRequest","details":"npm install wants permission"}
{"source":"codex","event":"Stop","filesChanged":4}
{"source":"browser","status":"upload_complete","title":"Google Drive"}
```

Events can target a card with `hwnd`, `processId`/`pid`, `processName`, or `windowTitle`/`title`. Hook-linked agent cards ignore this generic bridge for agent badges.

## Media Cards

When a shelved window matches an active Windows media session, Live Shelf upgrades the card automatically. Matching uses the media session source app, the shelved process name, the shelved window title, and media metadata. Native media apps like Spotify and VLC usually match by source app. Browser media like YouTube or Netflix matches best when the browser tab title includes the media title/site name.

The card displays:

- Source, such as YouTube, Spotify, Edge, Chrome, or VLC.
- Playback state and timeline, such as `Playing • 12:48 / 21:10`.
- Media title and artist/channel when Windows exposes it.
- A progress bar.

## Known Limits

- Elevated/admin windows may not be controllable unless Live Shelf is also elevated.
- Exclusive fullscreen apps, games, DRM video, and some Store/UWP windows are not supported.
- Some apps may stop visually updating when parked offscreen.
- Drag-to-edge shelving is not implemented yet.
- Browser extension and native-messaging packaging are not included yet; this means browser media matching uses Windows media sessions plus tab/window title matching, not exact tab IDs.
- DRM or custom media players may expose limited metadata, timeline, or controls through Windows media sessions.
- Interactive full zoom uses Win32 input forwarding. Some elevated, protected, or custom-rendered apps may ignore forwarded input.

## Crash Logs

Runtime exceptions are written to:

```text
%LOCALAPPDATA%\LiveShelf\crash.log
```
