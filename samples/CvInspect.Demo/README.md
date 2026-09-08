# CvInspect.Demo

![CvInspect.Demo — synthetic part, line and circle found, parameter editor on the right](screenshot.png)

One window that wires the whole toolkit together — including the fixture:

```
VirtualCam (folder playback) ──CamFrame──▶ CvDispCtrl.Frame        (held by reference, no copy)
                               └──AsMat()──▶ CvPatternFinder ──CvPose──▶ XformByPose(taught geometry)
                                                    └──▶ CvLineFinder / CvCircleFinder / CvBlobFinder ──▶ ViOverlay ──▶ CvDispCtrl.Overlay
CvPropEditCtrl ◀── CvPatternOpt / CvFindLineOpt / CvFindCircleOpt / CvBlobOpt ──▶ CvShapeBinder ──▶ draggable shapes
```

The part is synthetic (`DemoImage`): a bright plate on a dark background with a dark hole in the
middle and a dark "L" mark near one corner. Six frames of it — shifted and rotated a little each —
are written under the temp folder and played back by `VirtualCam`, so the frames arrive through the
same `ICam` path a real camera would use, and the part visibly moves in live mode.

**The fixture** is the point of the demo. The pattern tool trains on the L mark (automatically on the
first frame; press **Train** in the editor to redo it after moving the yellow train box). Every run
matches the mark, gets a `CvPose`, and `CvInspGeom.XformByPose` moves the taught line segment, circle
centre and blob search rectangle onto the found part before the tools run — the cyan dashed
geometry shows where they actually ran, the yellow shapes stay where you taught them. Untrained, the
tools run at the taught geometry and fail honestly once the part moves.

## Try

- **📸 / ⏯️** on the toolbar grab one frame or stream the moving part at 2 fps — watch the cyan
  geometry follow it while the yellow taught shapes stay put.
- **Drag the search shape** (the line's segment, the circle's expected arc, the blob's search
  rectangle) — the tool re-runs as you drag, because the shape writes straight back into the
  option POCO. Widen the blob rectangle past the plate and watch the dark background become the
  biggest blob: the search region is what keeps the hole the only hit.
- **Edit a parameter** in the right pane (calipers, search length, polarity, gates) — every
  commit re-runs the inspection. Hover a label for its description.
- **Right-click → Load image** to inspect a file of your own; 8-bit 1/3/4-channel formats only.
- Switch **Tool** to edit another tool; all results stay on the overlay. On the pattern tool, drag
  the train box somewhere featureless and press Train to see the refusal. The blob tool
  reports every blob above `MinArea` — count, area, centroid and contour — not just the largest.

## Run

```
dotnet run --project samples/CvInspect.Demo
```

Windows only (WPF). The inspection core (`DemoInspector`) is pure and deterministic — the WPF test
suite trains it on the reference part, runs it on a shifted-and-rotated one, and checks that the pose
angle, the followed line, circle and blob all land within a pixel of where the geometry went.
