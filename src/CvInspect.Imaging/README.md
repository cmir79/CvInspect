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

## Frame lifetime

`CamFrame` is a GC-owned `byte[]` holder — **there is no lifetime contract**: keep it,
queue it, or post it to a UI dispatcher as-is. `AsMat()` wraps the pixel buffer without
copying (disposing the view only unpins; the pixels stay valid), so running CvInspect
tools costs no conversion. Sources materialize one buffer per frame; frames already have
`CamOpt.Flip` / `Rotation` applied.

Two more contract points implementers must honor: **only complete frames are published**
(corrupt / partially received frames are dropped with a `CvLog` warning, never delivered),
and **`Stride` may exceed width × bytes-per-pixel** on devices that pad rows — consumers
must always walk rows by `Stride` (`AsMat()` handles this automatically).

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

## License

Apache-2.0. Not affiliated with OpenCV or OpenCvSharp.
