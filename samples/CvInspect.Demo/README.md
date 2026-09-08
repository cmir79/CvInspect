# CvInspect.Demo

![CvInspect.Demo — synthetic part, line and circle found, parameter editor on the right](screenshot.png)

One window that wires the whole toolkit together:

```
VirtualCam (folder playback) ──CamFrame──▶ CvDispCtrl.Frame        (held by reference, no copy)
                               └──AsMat()──▶ CvLineFinder / CvCircleFinder / CvBlobFinder ──▶ ViOverlay ──▶ CvDispCtrl.Overlay
CvPropEditCtrl ◀── CvFindLineOpt / CvFindCircleOpt / CvBlobOpt ──▶ CvShapeBinder ──▶ draggable search shapes
```

The part is synthetic (`DemoImage`): a bright plate on a dark background with a dark hole in the
middle, written once as `part.png` under the temp folder and played back by `VirtualCam` — so the
frames arrive through the same `ICam` path a real camera would use.

## Try

- **📸 / ⏯️** on the toolbar grab one frame or stream at 10 fps.
- **Drag the search shape** (the line's segment, the circle's expected arc, the blob's search
  rectangle) — the tool re-runs as you drag, because the shape writes straight back into the
  option POCO. Widen the blob rectangle past the plate and watch the dark background become the
  biggest blob: the search region is what keeps the hole the only hit.
- **Edit a parameter** in the right pane (calipers, search length, polarity, gates) — every
  commit re-runs the inspection. Hover a label for its description.
- **Right-click → Load image** to inspect a file of your own; 8-bit 1/3/4-channel formats only.
- Switch **Tool** to edit another tool; all three results stay on the overlay. The blob tool
  reports every blob above `MinArea` — count, area, centroid and contour — not just the largest.

## Run

```
dotnet run --project samples/CvInspect.Demo
```

Windows only (WPF). The inspection core (`DemoInspector`) is pure and deterministic — the WPF test
suite runs it on the synthetic image and checks the measured line angle and hole radius.
