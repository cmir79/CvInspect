# CvInspect.Wpf

[![ci](https://github.com/cmir79/CvInspect/actions/workflows/ci.yml/badge.svg)](https://github.com/cmir79/CvInspect/actions/workflows/ci.yml) [![NuGet CvInspect.Wpf](https://img.shields.io/nuget/v/CvInspect.Wpf?logo=nuget&label=CvInspect.Wpf)](https://www.nuget.org/packages/CvInspect.Wpf) [![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://github.com/cmir79/CvInspect/blob/main/LICENSE)

WPF display and shape-editing controls for the [CvInspect](https://www.nuget.org/packages/CvInspect)
machine-vision toolkit, based on [OpenCvSharp](https://github.com/shimat/opencvsharp).

- **`CvDispCtrl`** — an image display control with a dockable toolbar (grab / live toggle / fit /
  zoom / pan / clear), a status bar (cursor position, pixel value, image size, zoom level), and a
  right-click menu (load / save image file, view operations). Renders `OpenCvSharp.Mat` frames,
  renderer-neutral result overlays (`ViOverlay`), and interactive teaching shapes.
- **Teaching shapes** — the draggable shapes (`CvEditShape` family: rotated rect, segment, circle,
  arc, concentric ring) and the contract by which option POCOs supply them (`ICvShapeSource`,
  implemented by every CvInspect option that has geometry, and by host-defined options the same way)
  live in the core package under `CvInspect.Vision.Edit`. This package draws them and turns mouse
  drags into moves, corner resizes and rotation/angle/radius grips; each drag writes straight back
  into the POCO through the shape's `Changed` event.

## Install

```
dotnet add package CvInspect.Wpf
```

This package references only the managed assemblies (`CvInspect` → `OpenCvSharp4`). **Pick a native
OpenCV runtime yourself** in the application project, e.g.:

```
dotnet add package OpenCvSharp4.runtime.win
```

## Quick start

```xml
<Window xmlns:cv="clr-namespace:CvInspect.Controls;assembly=CvInspect.Wpf" ...>
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

| Property | Type | Meaning |
|---|---|---|
| `Frame` | `Mat` or `ICvPixelSource` | Frame to display — a `Mat` is copied on assignment, an `ICvPixelSource` (e.g. `CamFrame` from CvInspect.Imaging) is held by reference. `null` shows a placeholder. Auto-fits when the frame dimensions change and when the display area is resized — a resize replaces a manual zoom. Hiding and showing the control (a tab switch, a collapsed parent) is not a resize: the zoom is kept unless the size changed while it was hidden — call `FitToView()` to return to the fit. |
| `Overlay` | `ViOverlay` | Result graphics (segments, labels, rects, polylines); replace the reference to refresh. Lines are drawn at a screen-fixed width, independent of zoom: 2 for segments and rects, 1.2 for polylines (device-independent pixels). An item's `StrokeScale` multiplies that width (and a segment's end arrow), so one item, such as an NG box, can be made bolder; zero, negative, NaN and infinity draw as `1.0`, and other values are clamped to 0.25–50. Rects are drawn as one closed outline, so a bold box keeps full corners. Labels ignore it. |
| `ClipOverlayToImage` | `bool` | Clip `Overlay` to the drawn image (default `false`: graphics run to the edge of the display area, so a label on the image border stays readable where the fit leaves a margin on that side). Turn it on where a cut-out is shown (`ViOverlay.CropTo`) — otherwise geometry crossing the cut and items outside it are drawn in the margin, as if the image went on. With it on, items are cut at the image edge; label text keeps its screen size, so the smaller the image is drawn, the more of an edge label is lost. Two things are not clipped to the image: the corner summary HUD (`ViOverlayLabel.IsHud`), whose lines would otherwise vanish on a small cut-out, and `Shapes`, whose edge handles must stay grabbable. |
| `OverlayLabelScale` | `double` | Text size multiplier for `Overlay` labels on this control only (default `1.0`). Use it to enlarge result text on one screen (an operator view seen from a distance) without copying the overlay that history and other screens share. Only the text size changes: the anchor and alignment stay, and the label's background box follows the text. The corner summary HUD (`ViOverlayLabel.IsHud` with a corner alignment — the same test `ClipOverlayToImage` and `ViOverlay.CropTo` use), shapes and lines, and the name tags of `Shapes` keep their size. Zero, negative, NaN and infinity are rejected: set from code they throw `ArgumentException`; produced by a binding they leave the default `1.0` (only the WPF binding trace says so). |
| `Shapes` | `IReadOnlyList<CvEditShape>` | Editable teaching shapes; drag edits raise `Changed` immediately. A resizable rect has corner handles and, drawn as a slightly thicker middle stretch of each side (at most 24 device-independent px long), side handles that move that one side only, by the distance dragged — along the rect's own axes, so a rotated rect keeps its angle; the opposite side stays put. A side handle is left out when the side is shorter than 21 device-independent px on screen or its middle is within a corner handle's reach. Corner and rotation handles of every shape come first; a side handle then loses to the body of a smaller shape under the pointer (a search box around a train box), and of two sides in reach the nearer one is taken. The control subscribes to each shape's `Changed` only while it is loaded (attached on `Loaded`, released on `Unloaded`), so a long-lived shape list never keeps a discarded control alive. |
| `GrabCommand` / `ContinuousCommand` / `StopCommand` | `ICommand` | Camera actions behind the 📸 / ⏯️ toolbar buttons. The buttons honour `CanExecute(null)` like a WPF `Button.Command` — greyed out and not executed while it is false — and re-query on `CanExecuteChanged`, which may be raised from any thread. ⏯️ follows `StopCommand` while `IsRunning` is true and `ContinuousCommand` otherwise, so a blocked start never blocks stopping. A click refused by `CanExecute` re-queries the buttons, so a command that turned false without raising `CanExecuteChanged` greys out on that click — raise `CanExecuteChanged` when it becomes runnable again. If `CanExecute` throws, the command is treated as runnable and a warning is logged once per command instance. Subscriptions are weak: a long-lived view-model command does not keep the control alive. |
| `LoadFrameCommand` | `ICommand` | Receives a `Mat` loaded from a file via the right-click menu. Unset it to hide the load menu entry. `CanExecute` is not consulted — the parameter exists only after the file dialog closes; to refuse a load, reject it inside `Execute` and still dispose the `Mat` you received. |
| `IsRunning` | `bool` | Live state shown by the ⏯️ toggle (the view-model is the source of truth). |
| `IsToolbarVisible` / `IsCamControlVisible` / `ToolbarDock` | | Toolbar visibility and docking (top/bottom/left/right). |
| `FitToView()` | method | Same as the ⛶ toolbar button: drop the user's zoom and pan and fit the image. The fit is applied at the next draw, so it can be called while the control is hidden (it shows fitted when it comes back) or before the first frame. Call it on the UI thread. |
| `ToolbarBackground` / `StatusBarBackground` / `StatusBarForeground` / `SeparatorBrush` / `ViewBackground` | `Brush` | Per-instance toolbar, status bar and image-area colors; `null` (default) follows the resource keys — see [Localization and theming](#localization-and-theming). |

## Property editor

`CvPropEditCtrl` unfolds any option POCO into category groups and rows — checkbox for `bool`,
text for numbers and strings, combo for enums, a button for `Action`, a read-only line list for
enumerables, and an indented block of child rows for a nested object — using only the standard
`System.ComponentModel` attributes, which the core's `[CvCategory]` / `[CvName]` / `[CvDesc]` derive
from. So a core `Cv*Opt` needs nothing extra:

```xml
<cv:CvPropEditCtrl Source="{Binding ToolOpt}" Committed="OnCommitted" />
```

| Member | Meaning |
|---|---|
| `Source` | The POCO to edit; replacing it rebuilds the rows. `null` shows nothing. |
| `ShowDesc` | Show each row's description as a caption under it (the label tooltip always has it). |
| `ExecuteText` | Button text for `Action` rows; defaults to the `cv:Execute` translation. |
| `Committed` | A row wrote a value into the POCO — the host's dirty-marking signal. If the POCO implements `INotifyPropertyChanged`, the editor re-reads the notified rows, so a value written by a teaching-shape drag or an action button (train, span toggle) shows at once — but that is not a commit. The core options notify only such values (grip geometry, train results; most grip coordinates are hidden from the editor, so only visible ones such as the ring-fill band follow a drag), so their notifications cannot stand in for `Committed`. |
| `ActionExecuting` / `ActionExecuted` / `ActionFailed` | Around an `Action` row's button. Failures are logged through `CvLog` and never escape to the dispatcher. |

Because a list row reads and enumerates a property that nothing read before — a lazy getter that
talks to a device, a list another thread is filling — reading is guarded: a property whose getter or
enumeration throws is left out of the editor instead of taking the whole control down with it, and a
`CvLog` warning names it. Setting `Source` never throws because of one bad property.

A list row prints at most the first 20 items and then stops enumerating — it appends `… (+N)` when
the count is cheap to know and a bare `…` otherwise, so a large array or an endless lazy sequence
cannot freeze the editor just by being shown.

A property becomes a nested block only when it carries
`[TypeConverter(typeof(ExpandableObjectConverter))]` — nothing else is walked into, so an option
POCO never drags a whole object graph onto the screen. The block follows the **value's runtime
type**, and when the property starts returning a different instance (a mode switch that swaps the
detail object) the child rows are rebuilt, so the POCO must raise `PropertyChanged` for that
property at the moment it swaps; otherwise the old rows keep writing into the discarded object.
Nesting stops at three levels, and a level past the limit is not drawn at all rather than left
half-drawn.

**A nested block with no child rows is hidden entirely, heading and all** — a mode whose detail object
exposes nothing to edit leaves no trace on screen, and the block appears when the mode changes to one
that does. Expect this while checking that a property finally shows up. Hiding it is the point: an empty heading
would make "there is nothing to unfold" look exactly like "this could not be read", and those two
deserve different reactions. If you need to tell them apart in code, `CvPropNestedRowVm.HasChildren`
is public.

Rules the rows follow: `[Browsable(false)]` hides, `[ReadOnly(true)]` or a getter-only property
disables — except list and nested rows, which show or unfold instead of assigning the value itself
and so stay enabled — group order comes from `ICvOrderedCategory` (or a `"N. "` prefix on plain
`[Category]` strings), and numeric text commits on Enter or focus loss — an unparsable entry is
rejected and the row snaps back to the stored value, so what you see is always what is stored.
The control references no UI library; it adopts the host theme's `SecondaryTextBrush` and
`PrimaryBrush` when those keys exist (and follows them when the host swaps its dictionary at run
time) and falls back to fixed colors otherwise.

**Since 0.34 the row view-models and the builder live in the core package.** `CvPropGroupVm`, the
`CvProp…RowVm` rows, `CvPropRowBldr` and the four `CvProp…Evt` records moved from this package into
`CvInspect` with the same names and namespace. This package forwards them, so code and XAML built
against 0.33 keep working — but raise both packages together. A core-only or non-WPF host can build
the same rows itself with `CvPropRowBldr.Build`. The builder is reflection-based, so under IL2CPP
managed stripping keep the option types (for example with `link.xml`), or rows silently go missing.

The editor follows `PropertyChanged` through a weak subscription: a dropped editor never keeps the
POCO's rows alive. What is left on the source after its rows are collected is one small bridge
object, and it unsubscribes itself **the next time that source raises `PropertyChanged`** — not on a
timer. So opening and dropping editors many times on a long-lived POCO that rarely notifies leaves
that many bridges until it next notifies. That self-removal runs inside the source's raise, which is
safe for ordinary field-like events; a hand-written event accessor that iterates its own handler list
must copy the list before invoking it. `CvPropRowBldr.Detach` (used by the editor when `Source`
changes) unsubscribes at once, and it is final: detached rows never write again. Call it only on rows
you built yourself, never on an editor's live `Groups`.

## Frame lifetime

`Frame` accepts two kinds of value, with different lifetime rules:

- **`Mat`** — `CvDispCtrl` **copies the pixels** into its own buffer, so the host may `Dispose()`
  the `Mat` right after setting the property. That copy is the price of a type that can be
  disposed underneath the control.
- **`ICvPixelSource`** (the core contract, implemented by `CamFrame` in CvInspect.Imaging) — the
  control **holds the pixel array by reference** and copies it only once, into the WPF back
  buffer. The reference is kept for the status-bar pixel probe and for file save, not for
  rendering — so a buffer that changes after publishing does not tear the picture, it makes
  those two disagree with what is on screen. This relies on the contract that `Pixels` is
  immutable once published; a source that recycles its buffer must not implement it.

Supported formats either way: 8-bit with 1 (Gray8), 3 (Bgr24) or 4 (Bgra32) channels; anything
else shows the no-image placeholder. A value of any other type also shows the placeholder and
logs a warning through `CvLog`.

The `Mat` passed to `LoadFrameCommand` is **owned by the receiver** — the view-model disposes it
when done (typically after assigning it back to `Frame`).

Overlay and shape coordinates are in the pixel space of the image being displayed — bind an image
and coordinates from the same space, whether that is the original or a derived (scaled) stage image.

## Localization and theming

UI strings (menu labels, placeholder text) come from `CvLoc` in the core package, under the `cv:`
scope. Labels are re-read every time the context menu opens, so a language switch applies without
rebuilding the control.

`CvDispCtrl`'s toolbar, status bar and image-area colors come from the first of: a value set on the instance,
a resource key defined anywhere above it (application dictionary, window, the control's own
`Resources`), or a default — a fixed color, except for the image area (see below). Keys are held as resource references, so swapping the host's color
dictionary at run time (a light/dark switch) repaints every display without a restart.

| Key (constant on `CvDispCtrl`) | Key string | Instance property | Default | Paints |
|---|---|---|---|---|
| `ToolbarBackgroundKey` | `CvDispToolbarBackgroundBrush` | `ToolbarBackground` | `#FAFAFA` | Toolbar background |
| `StatusBarBackgroundKey` | `CvDispStatusBarBackgroundBrush` | `StatusBarBackground` | `#FAFAFA` | Status bar background |
| `StatusBarForegroundKey` | `CvDispStatusBarForegroundBrush` | `StatusBarForeground` | `#555555` | Status bar text |
| `SeparatorBrushKey` | `CvDispSeparatorBrush` | `SeparatorBrush` | `#DDDDDD` | Toolbar group separators, the toolbar's edge line, the status bar's edge line |
| `ViewBackgroundKey` | `CvDispViewBackgroundBrush` | `ViewBackground` | the control's `Background` (`#2A2A2A`) | Image area around the picture and the no-image screen; opaque image pixels are drawn over it (a Bgra32 frame's transparent pixels show it through) |

```xml
<!-- the host's dark color dictionary — one place covers every CvDispCtrl, including those made by a DataTemplate -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:cv="clr-namespace:CvInspect.Controls;assembly=CvInspect.Wpf">
  <SolidColorBrush x:Key="{x:Static cv:CvDispCtrl.ToolbarBackgroundKey}" Color="#2D2D30" />
  <SolidColorBrush x:Key="{x:Static cv:CvDispCtrl.StatusBarForegroundKey}" Color="#B4B4B4" />
</ResourceDictionary>
```

The image area is dark by default, which suits a dark theme. A light theme can lighten it in its own dictionary:

```xml
<SolidColorBrush x:Key="{x:Static cv:CvDispCtrl.ViewBackgroundKey}" Color="#E6E6E6" />
```

Write the key through `x:Static`: a typo then fails loudly when the dictionary loads (a
`XamlParseException` whose inner exception names the member) — the build does not check it — while a mistyped key string
is silently ignored and the default stays. The value must be a `Brush`. A non-`null` value of another
type under one of these five keys is ignored, and each display logs a `CvLog` warning naming the key
when it picks the value up.

The instance properties take a direct value, a binding, a `DynamicResource` or a style setter, and win
over the keys. Clearing a direct value (`ClearValue`) returns that instance to the next source: a style
setter if there is one, otherwise the key. `CvDispCtrl` is not a `Control`, so by WPF's rules an
implicit style in a window's or user control's resources does not reach a `CvDispCtrl` created by a
`DataTemplate` (one in the application's resources does) — inside templates, use the keys or set the
property on the element in the template.

`ViewBackground` falls back one step further than the others: with neither a value nor the key, the
image area shows the control's own `Background` (`#2A2A2A` unless the host set it), exactly as before
0.34. The no-image text stays `#9E9E9E`, which is faint on a very light background, and the named
overlay colors were chosen against a dark view — a host that lightens the view should choose its
overlay colors to match.

Toolbar accent colors use the host theme's `PrimaryBrush` / `SuccessBrush` / `DangerBrush` resource
keys when present, and `CvPropEditCtrl` uses `SecondaryTextBrush` / `PrimaryBrush`; both fall back to
fixed colors otherwise and follow a dictionary swap the same way — no UI library is referenced.

## Targets

`net8.0-windows`, AnyCPU. The native OpenCV runtime is chosen by the consuming application.

## Versions

The four CvInspect packages move together — upgrade them as a set. **This is a 0.x API: breaking
changes happen, so pin versions.** What changed in each release is on the
[Releases page](https://github.com/cmir79/CvInspect/releases), one entry per version; read the entries
between the version you are on and the one you are moving to.

## License

Apache-2.0. Not affiliated with OpenCV or OpenCvSharp.
