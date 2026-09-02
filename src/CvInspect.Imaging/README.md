# CvInspect.Imaging

Camera acquisition contract and tame frame sources for the
[CvInspect](https://github.com/cmir79/CvInspect) machine-vision toolkit, based on
[OpenCvSharp](https://github.com/shimat/opencvsharp).

- **`ICam`** — the acquisition contract: open/close, single grab, continuous grab,
  connection/grabbing events, best-effort exposure control. Frames are `OpenCvSharp.Mat`.
- **`VirtualCam`** — no-hardware source: a shifting-gradient test pattern, or name-ordered
  cyclic replay of an image folder (`CamOpt.VirtualImageDir`), re-enumerated on folder change.
  Ideal for development, demos and CI regression runs.
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

cam.FrameAcquired += (_, e) =>
{
    // e.Frame is only valid during this callback — Clone() to keep or hand off.
    using var frame = e.Frame.Clone();
    // ... run CvInspect tools, update display ...
};

cam.Open();
cam.StartContinuous();
```

## Frame lifetime

`CamFrameEvt.Frame` is **valid only during the event callback** — sources may reuse or
dispose the buffer afterwards, so high-rate acquisition does not allocate per frame.
`Clone()` the `Mat` if you store it or post it to another thread (e.g. a UI dispatcher).
Frames already have `CamOpt.Flip` / `Rotation` applied.

## Vendor adapters

Implement `ICam` in your application (or an adapter package) against the vendor SDK and
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

## License

Apache-2.0. Not affiliated with OpenCV or OpenCvSharp.
