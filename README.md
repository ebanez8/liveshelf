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
- The shelf stays hidden until at least one window is shelved.
- DWM live thumbnail cards in a topmost right-edge shelf.
- Animated hover peek that widens the shelf and enlarges the live preview.
- Hover a card and pinch in or use `Ctrl` + mouse wheel up to full zoom.
- Full zoom automatically enters interactive mode, forwarding mouse, wheel, keyboard, and text input to the source window.
- Pinch out and `Ctrl` + mouse wheel down are ignored by the shelf while zoomed, so those gestures go to the source app where possible.
- Moving the mouse outside the shelf automatically exits full zoom and collapses the peek.
- Cards show smart badges for changed, updated, done, closed, and needs-attention states.
- Cards subtly pulse when a shelved window title changes, closes, restores itself, or gets focus.
- Click to restore.
- Right-click actions for restore, close source, and remove card.
- On app exit, still-shelved windows are restored when their HWND still exists.

## Known Limits

- Elevated/admin windows may not be controllable unless Live Shelf is also elevated.
- Exclusive fullscreen apps, games, DRM video, and some Store/UWP windows are not supported.
- Some apps may stop visually updating when parked offscreen.
- Drag-to-edge shelving and smart status detection are not implemented yet.
- Interactive full zoom uses Win32 input forwarding. Some elevated, protected, or custom-rendered apps may ignore forwarded input.

## Crash Logs

Runtime exceptions are written to:

```text
%LOCALAPPDATA%\LiveShelf\crash.log
```
