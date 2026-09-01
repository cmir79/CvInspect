# CvInspect.Wpf

WPF display and shape-editing controls for the [CvInspect](https://www.nuget.org/packages/CvInspect)
machine-vision toolkit, based on [OpenCvSharp](https://github.com/shimat/opencvsharp).

- **`CvDispCtrl`** — an image display control with a dockable toolbar (grab / live toggle / fit /
  zoom / pan / clear), a status bar (cursor position, pixel value, image size, zoom level), and a
  right-click menu (load / save image file, view operations). Renders `OpenCvSharp.Mat` frames,
  renderer-neutral result overlays (`ViOverlay`), and interactive teaching shapes.
- **`CvEditShape`** family — draggable shapes (rotated rect, segment, circle, arc, concentric ring)
  edited directly on the image: move, corner resize, rotation grip, angle/radius grips.
- **`CvShapeBinder`** — builds edit shapes from CvInspect tool option POCOs (`CvPatternOpt`,
  `CvFindLineOpt`, `CvFindCircleOpt`, `CvBlobOpt`, …) and writes drag edits straight back into them.
- **`ICvShapeSource`** — lets host-defined option POCOs supply their own edit shapes.

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
| `Frame` | `Mat` | Frame to display; `null` shows a placeholder. Auto-fits when dimensions change. |
| `Overlay` | `ViOverlay` | Result graphics (segments, labels, rects, polylines); replace the reference to refresh. |
| `Shapes` | `IReadOnlyList<CvEditShape>` | Editable teaching shapes; drag edits raise `Changed` immediately. |
| `GrabCommand` / `ContinuousCommand` / `StopCommand` | `ICommand` | Camera actions behind the 📸 / ⏯️ toolbar buttons. |
| `LoadFrameCommand` | `ICommand` | Receives a `Mat` loaded from a file via the right-click menu. Unset it to hide the load menu entry. |
| `IsRunning` | `bool` | Live state shown by the ⏯️ toggle (the view-model is the source of truth). |
| `IsToolbarVisible` / `IsCamControlVisible` / `ToolbarDock` | | Toolbar visibility and docking (top/bottom/left/right). |

## Frame lifetime

`CvDispCtrl` **copies the pixels** of an assigned `Frame` into its own buffer, so the host may
`Dispose()` the `Mat` right after setting the property. Supported formats: `CV_8UC1` (Gray8),
`CV_8UC3` (Bgr24), `CV_8UC4` (Bgra32); other formats show the no-image placeholder.

The `Mat` passed to `LoadFrameCommand` is **owned by the receiver** — the view-model disposes it
when done (typically after assigning it back to `Frame`).

Overlay and shape coordinates are in the pixel space of the image being displayed — bind an image
and coordinates from the same space, whether that is the original or a derived (scaled) stage image.

## Localization and theming

UI strings (menu labels, placeholder text) come from `CvLoc` in the core package, under the `cv:`
scope. Labels are re-read every time the context menu opens, so a language switch applies without
rebuilding the control. Toolbar accent colors use the host theme's `PrimaryBrush` / `SuccessBrush` /
`DangerBrush` resource keys when present, and fall back to fixed colors otherwise — no UI library
is referenced.

## Targets

`net8.0-windows`, AnyCPU. The native OpenCV runtime is chosen by the consuming application.

## License

Apache-2.0. Not affiliated with OpenCV or OpenCvSharp.
