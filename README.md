# Live Shelf

Live Shelf is a Windows-native WPF prototype that shelves the current foreground window into a right-edge live card using DWM thumbnails.

## Run

```powershell
dotnet run --project src\LiveShelf\LiveShelf.csproj
```

Press `Ctrl+Alt+S` while another normal desktop window is focused. The window is parked offscreen, a live thumbnail card appears on the shelf, and clicking the card restores the original window placement. Press `Ctrl+Alt+H` to hide or show the shelf.

## MVP Behavior

- Hotkey shelving with `Ctrl+Alt+S`.
- Shelf hide/show with `Ctrl+Alt+H`.
- DWM live thumbnail cards in a topmost right-edge shelf.
- Animated hover peek that widens the shelf and enlarges the live preview.
- Click to restore.
- Right-click actions for restore, close source, and remove card.
- On app exit, still-shelved windows are restored when their HWND still exists.

## Known Limits

- Elevated/admin windows may not be controllable unless Live Shelf is also elevated.
- Exclusive fullscreen apps, games, DRM video, and some Store/UWP windows are not supported.
- Some apps may stop visually updating when parked offscreen.
- Drag-to-edge shelving and smart status detection are not implemented yet.

## Crash Logs

Runtime exceptions are written to:

```text
%LOCALAPPDATA%\LiveShelf\crash.log
```
