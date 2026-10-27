# CvInspect.Avalonia

[![ci](https://github.com/cmir79/CvInspect/actions/workflows/ci.yml/badge.svg)](https://github.com/cmir79/CvInspect/actions/workflows/ci.yml) [![NuGet CvInspect.Avalonia](https://img.shields.io/nuget/v/CvInspect.Avalonia?logo=nuget&label=CvInspect.Avalonia)](https://www.nuget.org/packages/CvInspect.Avalonia) [![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://github.com/cmir79/CvInspect/blob/main/LICENSE)

[Avalonia](https://avaloniaui.net/) display and shape-editing controls for the
[CvInspect](https://www.nuget.org/packages/CvInspect) machine-vision toolkit, based on
[OpenCvSharp](https://github.com/shimat/opencvsharp). The same controls and contract as
[CvInspect.Wpf](https://www.nuget.org/packages/CvInspect.Wpf), for Windows and Linux (macOS is not verified).

**First release (0.34).** See [Targets](#targets) for the supported platforms.

- **`CvDispCtrl`** — an image display control with a dockable toolbar (grab / live toggle / fit /
  zoom / pan / clear), a status bar (cursor position, pixel value, image size, zoom level), and a
  right-click menu (load / save image file, view operations). Renders `OpenCvSharp.Mat` frames,
  renderer-neutral result overlays (`ViOverlay`), and interactive teaching shapes.
- **Teaching shapes** — the draggable shapes (`CvEditShape` family: rotated rect, segment, circle,
  arc, concentric ring) and the contract by which option POCOs supply them (`ICvShapeSource`) live in
  the core package under `CvInspect.Vision.Edit`. This package draws them and turns pointer drags into
  moves, corner and side resizes and rotation/angle/radius grips; each drag writes straight back into
  the POCO through the shape's `Changed` event.
- **`CvPropEditCtrl`** — a property editor that unfolds an option POCO into category groups and rows.

## Install

```
dotnet add package CvInspect.Avalonia
```

This package references only `Avalonia` itself (12.1.4 or later) and the managed CvInspect assemblies
(`CvInspect` → `OpenCvSharp4`). **The application picks the rest:**

- the platform and renderer, e.g. `Avalonia.Desktop` (which brings Skia and the text shaper);
- a theme, e.g. `Avalonia.Themes.Fluent` — the controls use standard Avalonia controls (buttons,
  menus, text boxes, scroll viewers) and do not ship or force a theme, so **with no theme loaded
  those parts have no template and are not drawn**;
- fonts, if wanted — `.WithInterFont()` in the Quick start below comes from `Avalonia.Fonts.Inter`;
- a native OpenCV runtime, e.g. `OpenCvSharp4.runtime.win` or `OpenCvSharp4.official.runtime.linux-x64`.

**Keep every `Avalonia.*` package on the same version.** Referencing a lower `Avalonia` directly stops
the restore (NU1605), but an `Avalonia.Desktop` of a lower version mixes versions without any warning.

## Quick start

```csharp
// Program.cs
AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .WithInterFont()
    .LogToTrace()
    .StartWithClassicDesktopLifetime(args);
```

```xml
<!-- App.axaml -->
<Application.Styles>
  <FluentTheme />
</Application.Styles>
```

```xml
<Window xmlns:cv="using:CvInspect.Avalonia.Controls" ...>
  <cv:CvDispCtrl Frame="{Binding Frame}"
                 Overlay="{Binding Overlay}"
                 Shapes="{Binding Shapes}"
                 GrabCommand="{Binding GrabCmd}"
                 ContinuousCommand="{Binding LiveCmd}"
                 StopCommand="{Binding StopCmd}"
                 LoadFrameCommand="{Binding LoadFrameCmd}"
                 IsRunning="{Binding IsLive}" />
</Window>
```

The namespace is `CvInspect.Avalonia.Controls`, not the WPF package's `CvInspect.Controls`, so one
project can reference both packages.

| Member | Type | Meaning |
|---|---|---|
| `Frame` | `Mat` or `ICvPixelSource` | Frame to display — a `Mat` is copied on assignment, an `ICvPixelSource` (e.g. `CamFrame` from CvInspect.Imaging) is held by reference. `null` shows a placeholder. Auto-fits when the frame dimensions change and when the display area is resized — a resize replaces a manual zoom. |
| `Overlay` | `ViOverlay` | Result graphics; replace the reference to refresh. Lines are drawn at a screen-fixed width (2 for segments and rects, 1.2 for polylines, device-independent pixels), multiplied by an item's `StrokeScale` (invalid values draw as `1.0`, others are clamped to 0.25–50). |
| `ClipOverlayToImage` | `bool` | Clip `Overlay` to the drawn image (default `false`). The corner summary HUD and `Shapes` are never clipped. |
| `OverlayLabelScale` | `double` | Text size multiplier for `Overlay` labels on this control only (default `1.0`). The corner HUD, shapes, lines and shape name tags keep their size. Zero, negative, NaN and infinity are rejected. |
| `Shapes` | `IReadOnlyList<CvEditShape>` | Editable teaching shapes; drag edits raise `Changed` immediately. The control subscribes to each shape's `Changed` only while it is loaded, so a long-lived shape list never keeps a discarded control alive. Change shape values (and so raise `Changed`) on the UI thread — drawing reads them there. |
| `GrabCommand` / `ContinuousCommand` / `StopCommand` | `ICommand` | Camera actions behind the 📸 / ⏯️ toolbar buttons. The buttons honour `CanExecute(null)` and re-query on `CanExecuteChanged`, which may be raised from any thread. ⏯️ follows `StopCommand` while `IsRunning` is true and `ContinuousCommand` otherwise. Subscriptions are weak. |
| `LoadFrameCommand` | `ICommand` | Receives a `Mat` loaded from a file via the right-click menu. Unset it to hide the load entry. The receiver owns the `Mat`. |
| `IsRunning` | `bool` | Live state shown by the ⏯️ toggle (the view-model is the source of truth). |
| `IsToolbarVisible` / `IsCamControlVisible` / `ToolbarDock` | | Toolbar visibility and docking (`Avalonia.Controls.Dock`: top/bottom/left/right). |
| `FitToView()` | method | Same as the ⛶ toolbar button: drop the user's zoom and pan and fit the image at the next draw. Call it on the UI thread. |
| `ImageFileFailed` | event | A load or save from the right-click menu failed — see [Differences](#differences-from-cvinspectwpf). |
| `ToolbarBackground` / `StatusBarBackground` / `StatusBarForeground` / `SeparatorBrush` / `ViewBackground` / `PlaceholderForeground` | `IBrush` | Per-instance colors; `null` (default) follows the resource keys — see [Localization and theming](#localization-and-theming). |

The detailed behaviour of each member (fit rules, side handles, command refusal, stroke scaling) is the
same as in CvInspect.Wpf; its README is the reference for those details.

## Property editor

`CvPropEditCtrl` unfolds any option POCO into category groups and rows — checkbox for `bool`, text
for numbers and strings, combo for enums, a button for `Action`, a read-only line list for
enumerables, and an indented block of child rows for a nested object — using only the standard
`System.ComponentModel` attributes, which the core's `[CvCategory]` / `[CvName]` / `[CvDesc]` derive
from.

```xml
<cv:CvPropEditCtrl Source="{Binding ToolOpt}" Committed="OnCommitted" />
```

| Member | Meaning |
|---|---|
| `Source` | The POCO to edit; replacing it rebuilds the rows. `null` shows nothing. |
| `ShowDesc` | Show each row's description as a caption under it (the label tooltip always has it). |
| `ExecuteText` | Button text for `Action` rows; defaults to the `cv:Execute` translation. |
| `Committed` | A row wrote a value into the POCO — the host's dirty-marking signal. |
| `ActionExecuting` / `ActionExecuted` / `ActionFailed` | Around an `Action` row's button. Failures are logged through `CvLog`. |
| `Groups` | The groups currently shown (`CvPropGroupVm`, read-only snapshot). |

The rows, their rules and the builder are the core package's (`CvPropRowBldr`, `CvProp…RowVm`), shared
with CvInspect.Wpf, so an option shows the same rows in both. Text commits on Enter or focus loss; an
unparsable number snaps back to the stored value. A nested block with no child rows is hidden
entirely. The editor follows the POCO's `PropertyChanged` through a weak subscription; a notification
raised on a worker thread is shown through the binding, which moves it to the UI thread. Set the
editor's own properties on the UI thread. The full rules (guarded reads, list limits, nesting) are in
the CvInspect.Wpf README under "Property editor".

## Frame lifetime

- **`Mat`** — `CvDispCtrl` **copies the pixels** into its own buffer, so the host may `Dispose()`
  the `Mat` right after setting the property.
- **`ICvPixelSource`** (implemented by `CamFrame` in CvInspect.Imaging) — the control **holds the
  pixel array by reference** and copies it once, into its screen bitmap. The reference is kept for
  the status-bar pixel probe and for file save. This relies on the contract that `Pixels` is
  immutable once published.

Supported formats either way: 8-bit with 1 (Gray8), 3 (Bgr24) or 4 (Bgra32) channels; anything else
shows the no-image placeholder. The screen bitmap is one BGRA bitmap reused across frames; gray and
BGR frames are converted into it row by row.

Overlay and shape coordinates are in the pixel space of the image being displayed.

## Localization and theming

UI strings come from `CvLoc` in the core package, under the `cv:` scope; menu labels are re-read each
time the context menu opens.

`CvDispCtrl`'s colors come from the first of: a value set on the instance (direct value, binding or
style setter), a resource key defined anywhere above it, or a default. Keys are observed, so swapping
a dictionary at run time **or switching the theme variant** (light/dark, including keys in
`ThemeDictionaries`) repaints without a restart. The key strings are the same as in CvInspect.Wpf:

| Key (constant on `CvDispCtrl`) | Key string | Default | Paints |
|---|---|---|---|
| `ToolbarBackgroundKey` | `CvDispToolbarBackgroundBrush` | `#FAFAFA` | Toolbar background |
| `StatusBarBackgroundKey` | `CvDispStatusBarBackgroundBrush` | `#FAFAFA` | Status bar background |
| `StatusBarForegroundKey` | `CvDispStatusBarForegroundBrush` | `#555555` | Status bar text |
| `SeparatorBrushKey` | `CvDispSeparatorBrush` | `#DDDDDD` | Separators and edge lines |
| `ViewBackgroundKey` | `CvDispViewBackgroundBrush` | the control's `Background` (`#2A2A2A`) | Image area and the no-image screen |
| `PlaceholderForegroundKey` | `CvDispPlaceholderForegroundBrush` | `#9E9E9E` | No-image text (`ISolidColorBrush` only) |

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.ThemeDictionaries>
      <ResourceDictionary x:Key="Dark">
        <SolidColorBrush x:Key="{x:Static cv:CvDispCtrl.ToolbarBackgroundKey}" Color="#2D2D30" />
        <SolidColorBrush x:Key="{x:Static cv:CvDispCtrl.StatusBarForegroundKey}" Color="#B4B4B4" />
      </ResourceDictionary>
    </ResourceDictionary.ThemeDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

Write the key through `x:Static`; a mistyped key string is silently ignored. A value under one of the
six keys that is not an `IBrush` is ignored and logged as a `CvLog` warning naming the key.

Toolbar accents use the host's `PrimaryBrush` / `SuccessBrush` / `DangerBrush` keys and
`CvPropEditCtrl` uses `SecondaryTextBrush` / `PrimaryBrush` — the same names as in CvInspect.Wpf —
and fall back to fixed colors otherwise. **The Fluent and Simple themes do not define these keys**, so
with those themes the fixed colors are used unless the application defines them.

Text in the display (status bar, overlay labels) uses the font list
`Consolas, Cascadia Mono, DejaVu Sans Mono, Menlo`; toolbar icons are emoji and request
`Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji`. On Linux, install a color emoji font
(e.g. `fonts-noto-color-emoji`) or the toolbar icons may show as boxes.

## Targets

`net8.0`, AnyCPU, Avalonia 12.1.4 or later. Windows and Linux are the supported targets; macOS is not
verified.

## Limits

- **Frame cost is higher than in CvInspect.Wpf.** Gray8 and Bgr24 frames are converted to BGRA before
  upload. Measured on one development PC with a 2448×2048 frame: about 1.9 ms (Gray8) and 3.5 ms
  (Bgr24) when frames are converted back to back, and 2.9 ms / 4.7 ms with idle gaps between frames,
  against 0.08–1.6 ms for the WPF upload on the same PC. The cost grows with frame size and frame rate.
- **Smart App Control.** Avalonia's native and managed assemblies, including the text shaper
  (`Avalonia.HarfBuzz`), are not signed; on a Windows PC where Smart App Control is enforcing, they can
  be blocked and the application may not draw text. WPF is not affected.
- **Telemetry.** Avalonia's build tooling (`Avalonia.BuildServices`) sends build statistics, and it
  reaches projects that reference only this library. This package does not turn it off on your
  behalf. Set the environment variable `AVALONIA_TELEMETRY_OPTOUT` (any value) on build machines to
  opt out; see Avalonia's documentation for what is sent and which license tiers can opt out.
- Everything listed as a limit of the CvInspect toolkit in the root README also applies.

## Differences from CvInspect.Wpf

The member names and meanings follow CvInspect.Wpf. Where Avalonia works differently:

1. **`ToolbarDock`** is `Avalonia.Controls.Dock`.
2. **Colors are `IBrush?`**, and every property is a styled property — host styles can set any of them,
   including the default `Background` (`#2A2A2A`), which is a default value, not a local value — so
   clearing a `Background` the host set (`ClearValue`) returns to `#2A2A2A`. `CvDispCtrl` is a `Control` here, so a style in a window's or user control's resources reaches
   instances created by a `DataTemplate` too.
3. **Theme keys also follow theme-variant switches**, not only dictionary swaps.
4. **No theme is forced.** Buttons and menus are standard controls and need the application's theme.
5. **No message box.** A failed load or save is logged as a `CvLog` warning and raised as
   `ImageFileFailed` (`EventHandler<CvDispFileErrEvt>`), whose arguments carry `Op`
   (`CvDispFileOp.Load` / `Save`), `FileName` (a name, not a path) and `Error`. An exception thrown by a
   handler is caught and logged.
6. **File dialogs use the window's `StorageProvider`** and read and write through streams, so they also
   work where storage gives no file path. Load is offered only when `LoadFrameCommand` is set and the
   provider can open files; save only when it can save. Before the control is attached to a window both
   are assumed available. Load and save are asynchronous; a second request while a picker is open is
   ignored, and saving encodes before opening the file, so a failed encode leaves an existing file intact.
7. **No constructor-ordering rule.** Application styles are applied when the control joins the logical
   tree, so the WPF note about building the control inside a constructor does not apply.
8. **Touchpad wheels** — fractional wheel deltas are accumulated and zoom one step per whole notch.
   A wheel event with no vertical part (horizontal scrolling) is left unhandled so a parent scroll viewer
   gets it, and the accumulated fraction is kept. One that cannot zoom further in its direction is also
   left unhandled, and the accumulated fraction is dropped.
9. **Status notices are posted to the UI thread** rather than raised during drawing.
10. **Toolbar buttons carry `AutomationProperties.Name`** with the same English text as their tooltips,
    and editor inputs carry their row label as their accessible name.
11. **Dashed shape outlines** are drawn with flat caps (closed paths with square caps); they match the WPF
    look to within a fraction of a pixel but are not identical.
12. **Bitmap interpolation is fixed to nearest-neighbour** for the frame, so a host cannot turn on smoothing.
13. **An invalid `OverlayLabelScale` from a binding** (zero, negative, NaN or infinity) is not applied: the
    property falls back to its default `1.0` — not to the previous value, and not to a value set by a
    style. Setting such a value from code throws `ArgumentException`.
14. **`Shapes` values are changed on the UI thread** — see the member table. Redraws requested by
    `Changed` are posted to the UI thread, but that does not make writing shape values from a worker
    thread safe while they are being drawn.

## Versions

The five CvInspect packages (`CvInspect`, `CvInspect.Imaging`, `CvInspect.Imaging.Gev`,
`CvInspect.Wpf`, `CvInspect.Avalonia`) move together — upgrade them as a set. **This is a 0.x API:
breaking changes happen, so pin versions.** What changed in each release is on the
[Releases page](https://github.com/cmir79/CvInspect/releases).

## License

Apache-2.0. Not affiliated with OpenCV, OpenCvSharp or Avalonia.
