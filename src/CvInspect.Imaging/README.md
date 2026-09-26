# CvInspect.Imaging

[![ci](https://github.com/cmir79/CvInspect/actions/workflows/ci.yml/badge.svg)](https://github.com/cmir79/CvInspect/actions/workflows/ci.yml) [![NuGet CvInspect.Imaging](https://img.shields.io/nuget/v/CvInspect.Imaging?logo=nuget&label=CvInspect.Imaging)](https://www.nuget.org/packages/CvInspect.Imaging) [![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://github.com/cmir79/CvInspect/blob/main/LICENSE)

Camera acquisition contract and tame frame sources for the
[CvInspect](https://github.com/cmir79/CvInspect) machine-vision toolkit, based on
[OpenCvSharp](https://github.com/shimat/opencvsharp).

- **`ICam`** — the acquisition contract: open/close, single grab, continuous grab,
  connection/grabbing events, best-effort exposure control. Frames are GC-owned
  **`CamFrame`** buffers (`byte[]` + width/height/stride/format/timestamps) with **no
  lifetime contract** — `frame.AsMat()` gives a zero-copy `Mat` view for inspection, and the
  type implements the core `ICvPixelSource` contract, so `CvDispCtrl` (CvInspect.Wpf) displays
  it by reference without copying.
  A frame carries two clocks: `TimestampUtc` is when it **arrived**, and `DeviceTimestamp`
  is when the camera **captured** it (null if the device does not report one). The device
  clock has its own epoch, so compare frames to each other rather than reading it as wall
  time — the change in the gap between the two is time the frame spent waiting.
- **`VirtualCam`** — no-hardware source: a shifting-gradient test pattern, or name-ordered
  cyclic replay of an image folder (`CamOpt.VirtualImageDir`), re-enumerated on folder change.
  Ideal for development, demos and CI regression runs. Note: color→gray decoding follows
  OpenCV's decoder coefficients, so pixel values may differ slightly from images converted
  by other stacks (e.g. GDI luma) — build regression baselines through the same path.
- **`VideoCaptureCam`** — webcam (device index), video file (paced by `FrameRate`, loops at
  end) or stream URL (RTSP, …) through OpenCV `VideoCapture` (`CamOpt.VideoSource`).
- **`CamFactory`** — `ComType` string → implementation, with `Register` as the injection
  point for vendor SDK adapters (GigE etc.) so this package never references heavy SDKs.

## Quick start

```csharp
using CvInspect.Imaging;

var cam = CamFactory.Create(new CamOpt
{
    ComType = "Virtual",              // or "VideoCapture", or a registered vendor ComType
    VirtualImageDir = @"D:\samples",  // empty → test pattern
    FrameRate = 10,
});

cam.FrameAcquired += (_, frame) =>
{
    using var mat = frame.AsMat();   // zero-copy view into frame.Pixels
    // ... run CvInspect tools, update display ...
    // frame itself is GC-owned — keep it or hand it to another thread freely.
};

cam.Open();
cam.StartContinuous();
```

## One frame, awaited

`GrabOne()` publishes through `FrameAcquired` and nothing else, so taking a single frame means
subscribing, calling, waiting and unsubscribing. Every consumer wrote that by hand, which is a sign
the library was missing something. `GrabFrameAsync` does it for you:

```csharp
using CvInspect.Imaging;

var frame = await cam.GrabFrameAsync(TimeSpan.FromSeconds(2));   // GevCam throws TimeoutException on timeout — see below
if (frame is null) { /* no frame, and no reason to throw — see below */ }
else using (var mat = frame.AsMat()) { /* inspect */ }
```

`GrabFrameAsync` is an extension method, so it only appears on `ICam` when `CvInspect.Imaging` is
imported — code that spells every type out in full will not see it without a `using`.

The frame **also goes out on `FrameAcquired`** — it is the same acquisition, and a display already
subscribed should not miss this one frame. What you get back is that very instance, not merely
some frame that arrived around the same time.

**`null` versus an exception is the contract here.** `null` means *no frame for this call, and no
reason to throw*: the generic path's timeout expired, the implementation can never answer and has
already logged why (`DeadCam`), or it received a frame it could not use and logged why (`GevCam` with
an unsupported pixel format). **`null` can come before the timeout**, so do not read it as "timed out".
Anything that *should* be able to answer but cannot — not open, closed, disposed, control lost,
continuous acquisition running, another grab already waiting — **throws**, exactly as `GrabOne` does.
Folding those into `null` would collapse the reason channel and leave the caller waiting for a frame
that is never coming.

**Timeouts are where backends differ.** The generic path cannot tell why nothing came, so it returns
`null`. `GevCam` knows where to look and throws a `TimeoutException` whose message points at what it
logged at open (trigger mode on the camera-state line, and a separate warning when chunk mode is on) —
the same body `GrabOne` runs. Code
that must work on any backend treats `null` and `TimeoutException` alike as "no frame"; cancellation
through the token comes out as `OperationCanceledException`, so it stays distinguishable from both.

The `timeout` argument wins over whatever the implementation has configured, because the call site
knows more than the configuration did. Pass `Timeout.InfiniteTimeSpan` to defer to the
implementation's own deadline — `GevCam` then uses its `GrabTimeoutMs`. On the generic path that
deadline is `GrabOne`'s own; if `GrabOne` comes back without a frame, a late one gets a one-second
grace and then the call returns `null`.

Implement **`ICamGrabAsync`** when your camera knows something the generic path cannot, and the
extension will call you instead. There are two such things:

- **Which frame is *this* grab's.** The generic path takes whatever arrives while it is subscribed;
  only the implementation, holding the device's own pairing evidence (frame id, ticket), can tell that
  from a frame that merely showed up at the same moment. `GevCam` does this with a start line latched
  from the device clock just before it starts acquisition, and falls back to the frame id on a camera
  that does not expose its clock.
- **That no frame is coming at all.** The generic path cannot distinguish "not yet" from "never", so
  it waits out the timeout rather than guessing — it will not cut short a backend that publishes just
  after `GrabOne` returns. (With `Timeout.InfiniteTimeSpan` there is no caller timeout left to wait
  out, so a late frame gets a one-second grace after `GrabOne` returns.) A camera that knows the answer immediately should say so: `DeadCam` returns
  `null` at once instead of stalling every grab for the full timeout.

`GevCam`, `ReconnectingCam` (which forwards to its inner camera) and `DeadCam` implement it.

## Frame lifetime

`CamFrame` is a GC-owned `byte[]` holder — **there is no lifetime contract**: keep it,
queue it, or post it to a UI dispatcher as-is. `AsMat()` wraps the pixel buffer without
copying (disposing the view only unpins; the pixels stay valid), so running CvInspect
tools costs no conversion. Sources materialize one buffer per frame; frames already have
`CamOpt.Flip` / `Rotation` applied.

`Crop(x, y, width, height)` returns a new frame for a region — a copy with a tight stride that keeps
the format and both timestamps (it is the same shot). The region must lie inside the frame; pass the
rect `CvImageOps.Crop` reported, and move the result overlay with the same rect (`ViOverlay.CropTo`).
On `CvDispCtrl`, set `ClipOverlayToImage` for such a view, or items outside the region are drawn in the fit margin.

Two more contract points implementers must honor: **only complete frames are published**
(corrupt / partially received frames are dropped with a `CvLog` warning, never delivered),
and **`Stride` may exceed width × bytes-per-pixel** on devices that pad rows — consumers
must always walk rows by `Stride` (`AsMat()` handles this automatically).

## USB webcams — a stable identity

OpenCV opens webcams by index, and the index is whatever order the OS enumerated the devices in
this time. Re-plug a hub, reboot, add a capture card, and camera 0 and camera 1 swap without a
warning. `VideoCaptureCam` therefore accepts an identity in `CamOpt.SerialNumber`:

```csharp
new CamOpt { ComType = "VideoCapture", SerialNumber = "VID_046D&PID_082D" }
```

Accepted keys: `VID_046D&PID_082D`, `046D:082D`, a full Windows instance id
(`USB\VID_046D&PID_082D\5&2C1F7A8&0&0001`) or a Linux `/dev/v4l/by-id` name. When the key is not
found, the exception lists every enumerated device so you can copy the right one into the
settings; when two identical models match a VID/PID, it refuses to guess and lists their instance
ids. `UsbCamId.Enumerate()` gives you the same list programmatically. `UserSettings` may carry
`backend=dshow|msmf|v4l2|any` to pin the OpenCV backend.

| OS | How | Verified |
|---|---|---|
| Windows | SetupAPI enumeration of `KSCATEGORY_VIDEO`; the enumeration position is the OpenCV index | **Verified** on a Windows 11 PC with two cameras of different models (built-in + external): the enumeration position matched the OpenCV index for the ANY, DSHOW and MSMF backends alike, and opening by VID/PID returned the right camera. The code it was ported from also ran on a two-webcam production line. Not yet exercised: identical models (instance-id path), hot re-plug |
| Linux | `/sys/class/video4linux` + `device/modalias` for VID/PID, `/dev/v4l/by-id` for the instance; opens by `/dev/videoN` path, so ordering never matters | **Not run on hardware** — only a synthetic-sysfs regression. `net8.0` asset only (symlink resolution) |
| macOS | — | **Unsupported**: `Enumerate()` throws; use `VideoSource` with an index |

## Vendor adapters

Implement `ICam` in your application (or an adapter package) against the vendor SDK —
materialize SDK buffers into `CamFrame` directly (or via `CamFrame.FromMat`) — and
register it once at startup:

```csharp
CamFactory.Register("MyGigE", opt => new MyGigECam(opt));
```

Proprietary SDK assemblies stay out of this package by design.

## Targets

`netstandard2.1` and `net8.0`. Depends on `CvInspect` (managed `OpenCvSharp4` flows
transitively); **the native OpenCV runtime is chosen by the consuming application** —
`VideoCaptureCam` additionally needs the `videoio` backend for your source type
(the Windows runtime bundles it).

## Versions

The four CvInspect packages move together — upgrade them as a set. **This is a 0.x API: breaking
changes happen, so pin versions.** What changed in each release is on the
[Releases page](https://github.com/cmir79/CvInspect/releases), one entry per version; read the entries
between the version you are on and the one you are moving to.

## License

Apache-2.0. Not affiliated with OpenCV or OpenCvSharp.
