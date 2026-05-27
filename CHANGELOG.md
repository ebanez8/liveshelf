# Changelog

## v0.2.0 — Multi-monitor fixes

Single shelf HWND that follows the source window's monitor, plus the DPI and
monitor-enumeration fixes that kept the shelf from rendering on high-DPI and
multi-monitor setups.

### Fixed

- **Multi-monitor shelving.** Pressing `Ctrl+Alt+S` on a window now resolves the
  source window's monitor with `MonitorFromWindow` + `GetMonitorInfo(rcWork)`
  and repositions the single shelf HWND to that monitor's right edge before the
  card is created. Cards from monitor 1 land on monitor 1, cards from monitor 2
  land on monitor 2. Supports negative monitor coordinates and non-primary
  layouts.
- **`MONITORINFOEXW` marshalling.** The struct was declared without
  `CharSet = CharSet.Unicode`, so `szDevice` was sized for ANSI (32 bytes)
  while `GetMonitorInfoW` expects Unicode (64 bytes). `cbSize` came out as 72
  instead of 104 and every call failed, dropping us into the virtual-screen
  fallback where the shelf was sized to span every monitor at once.
- **High-DPI shelf positioning.** `Window.Left/Top/Width/Height` are in DIPs;
  the previous code assigned pixel values from `_monitor.WorkArea` straight to
  them. On 125% scaling this drove the WPF window 25% past the monitor edge,
  so the shelf appeared briefly and then "hid itself" as WPF re-synced the
  HWND to its DIP-interpretation of those values. Pixel↔DIP conversion is now
  done at every WPF property assignment, and native `SetWindowPos` keeps using
  pixels.
- **Rail collapse on high-DPI.** `CollapseToRail` and `ExpandFromRail`
  animated `Window.Left` to a pixel-derived target, so the 48-DIP rail flew
  offscreen on 125% scaling and looked like the shelf was hiding instead of
  collapsing.

### Changed

- **Transactional shelving flow** (placeholder card → DWM register →
  verify card bounds + DWM HRESULTs → park source) now drives a single shelf
  HWND. The source window is only parked after the card and live-preview
  thumbnail are confirmed visible; on any failure the source stays visible.
- **Diagnostics.** Every `Ctrl+Alt+S` attempt logs source HWND/title/process,
  resolved monitor `rcWork`, shelf HWND/bounds, card id/bounds, DWM destination
  rect, `DwmRegisterThumbnail` / `DwmUpdateThumbnailProperties` HRESULTs, and
  whether the source was parked. Logs live at
  `%LOCALAPPDATA%\LiveShelf\logs\shelving.log`.
- **No more per-monitor card collections.** The single shelf HWND owns the
  one card list, and every DWM thumbnail destination is that HWND. The
  `ReassignShelf` cross-shelf migration path is gone.
