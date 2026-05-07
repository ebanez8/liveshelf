# Live Shelf

Live Shelf is a Windows-native WPF prototype that shelves the current foreground window into a right-edge live card using DWM thumbnails.

Live Shelf turns hidden windows into live task cards.

## Run

```powershell
dotnet run --project src\LiveShelf\LiveShelf.csproj
```

Press `Ctrl+Alt+S` while another normal desktop window is focused. The window is parked offscreen, a live thumbnail card appears on the shelf, and clicking the card restores the original window placement. Press `Ctrl+Alt+H` to hide or show the shelf.

## MVP Behavior

- Hotkey shelving with `Ctrl+Alt+S`.
- Shelf hide/show with `Ctrl+Alt+H`.
- The shelf stays hidden until at least one window is shelved.
- DWM live thumbnail cards in a topmost right-edge shelf.
- Animated hover peek that widens the shelf and enlarges the live preview.
- Hover a card and pinch in or use `Ctrl` + mouse wheel up to full zoom.
- Full zoom automatically enters interactive mode, forwarding mouse, wheel, keyboard, and text input to the source window.
- Pinch out and `Ctrl` + mouse wheel down are ignored by the shelf while zoomed, so those gestures go to the source app where possible.
- Moving the mouse outside the shelf automatically exits full zoom and collapses the peek.
- Cards show task badges for changed, updated, loading, running, editing files, running command, waiting for approval, done, done-needs-review, failed, closed, needs-input, playing, paused, upload-complete, and error states.
- Cards subtly pulse when a shelved window title changes, closes, restores itself, or gets focus.
- Agent Cards can receive direct lifecycle events from Codex, Claude Code, Cursor, terminal agents, or hook scripts through the `LiveShelf.Events` named pipe. `PreToolUse`, `PostToolUse`, `PermissionRequest`, `UserPromptSubmit`, `Stop`, and `StopFailure` map to high-confidence running, editing, waiting-for-approval, done-needs-review, and failed badges.
- Smart Cards use softer layered detection from window titles and UI Automation text. Browser/native integrations can also publish status events to `LiveShelf.Events` for loading, updated, done, needs-input, playing, paused, upload-complete, and error badges.
- Click to restore.
- Right-click actions for restore, close source, and remove card.
- On app exit, still-shelved windows are restored to their original placement/state when their HWND still exists.

## Status Event Bridge

Hook scripts and browser/native integrations can send newline-delimited JSON to the local named pipe `LiveShelf.Events`.

```json
{"source":"codex","event":"PreToolUse","tool":"apply_patch","status":"editing_files","filesChanged":4}
{"source":"codex","event":"PermissionRequest","details":"npm install wants permission"}
{"source":"codex","event":"Stop","filesChanged":4}
{"source":"browser","status":"upload_complete","title":"Google Drive"}
```

Events can target a card with `hwnd`, `processId`/`pid`, `processName`, or `windowTitle`/`title`. If an agent event has no explicit target, Live Shelf applies it to the most recently active shelved terminal/agent card.

## Known Limits

- Elevated/admin windows may not be controllable unless Live Shelf is also elevated.
- Exclusive fullscreen apps, games, DRM video, and some Store/UWP windows are not supported.
- Some apps may stop visually updating when parked offscreen.
- Drag-to-edge shelving is not implemented yet.
- Browser extension and native-messaging packaging are not included yet; the named-pipe event bridge is the app-side listener.
- Interactive full zoom uses Win32 input forwarding. Some elevated, protected, or custom-rendered apps may ignore forwarded input.

## Crash Logs

Runtime exceptions are written to:

```text
%LOCALAPPDATA%\LiveShelf\crash.log
```
