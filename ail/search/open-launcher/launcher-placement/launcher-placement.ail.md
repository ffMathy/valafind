# Where the launcher appears

The position of the launcher window on the screen with the pointer, as numbers.

```ail
node LauncherPlacement : fn   level: system
  why   The launcher should appear where the user is looking: on the screen with the pointer, centred, in the upper
        part, and never off the screen.   <- OpenLauncher
  what
    type PixelRect = { x: integer, y: integer, width: integer, height: integer }
    type PixelPoint = { x: integer, y: integer }
    LauncherWindow.computePosition(workingArea: PixelRect, scaling: number, windowSize: { width: number, height: number }, cardTopMargin: number) -> PixelPoint
      the window's size in pixels is its size times scaling, each rounded up
      x centres the window in the working area; y puts the card's top (the window's top plus cardTopMargin) at 22 % of the working area's height, rounded
      then y is moved up so the window ends inside the working area, and x and y are clamped so it starts inside it
    TOP_FRACTION: number = 0.22
    examples
      computePosition({ x: 0, y: 0, width: 1920, height: 1080 }, 1, { width: 1220, height: 786 }, 0) => { x: 350, y: 238 }
      computePosition({ x: 0, y: 0, width: 1280, height: 800 }, 1, { width: 1220, height: 786 }, 0) => { x: 30, y: 14 }
      computePosition({ x: 0, y: 0, width: 1280, height: 767 }, 1, { width: 1220, height: 786 }, 0) => { x: 30, y: 0 }
      computePosition({ x: 0, y: 0, width: 2560, height: 1440 }, 1, { width: 1292, height: 862 }, 20) => { x: 634, y: 297 }
  how
    decide the screen is the one under the pointer; without a pointer position, the screen the window was on, else the primary screen
      because the user is looking where the pointer is (LauncherWindow, PositionOnActiveScreen)
    decide the position is worked out each time the launcher is shown while hidden, never while it is already shown
      because a compositor may have started or stopped meanwhile, which changes the card's margin
    decide the working area, not the whole screen, is used
      because panels and docks must not cover the search box
    decide on macOS the pointer is in points, so the screen is found by comparing points; elsewhere in pixels
      because macOS reports the pointer position in points (LauncherWindow, Contains)
  sources
    src/Valafind.App/Views/LauncherWindow.axaml.cs, ComputePosition, PositionOnActiveScreen, Contains
    examples run against the built app on 2026-11-25 (a small program calling ComputePosition by reflection)
    driven on Linux (Xvfb, openbox) 2026-11-25: on a 1280 by 800 screen whose working area ends above a 33 pixel
      panel, the 1220 by 786 launcher opened at 30,0
```
