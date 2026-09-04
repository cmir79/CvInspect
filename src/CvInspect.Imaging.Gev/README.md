# CvInspect.Imaging.Gev

GigE camera acquisition for the [CvInspect](https://github.com/cmir79/CvInspect) machine-vision
toolkit — an `ICam` backend that speaks the protocol directly, so **no vendor SDK and no
proprietary DLLs** are needed.

> **Status.** Verified against real hardware: cameras from two different vendors are discovered,
> opened, streamed and stopped with **no vendor SDK and no vendor filter driver installed**.
> Only those two models have been exercised and production run time has not accumulated yet,
> so treat other cameras as unproven.

## Use

```csharp
CamFactory.Register("GigE", opt => new GevCam(opt, new GevCamOpt
{
    XmlCacheDir = cacheDir,        // second Open skips the camera XML transfer
    BufferCount = 8,
}));

var cam = CamFactory.Create(new CamOpt
{
    ComType = "GigE",
    SerialNumber = "12345678",     // cameras are bound by serial, not by address
    ExposureTimeUs = 8000,
    UserSettings = "UserSet1",     // loaded on open when set
});

cam.FrameAcquired += (_, frame) => { using var mat = frame.AsMat(); /* inspect */ };
cam.Open();
cam.StartContinuous();
```

Wrap it in `ReconnectingCam` to survive link drops:

```csharp
var cam = new ReconnectingCam(() => CamFactory.Create(camOpt));
```

## Why serial numbers

Addresses move — DHCP hands them out, and a camera that fails to get a lease falls back to a
link-local address. Serial numbers do not. When the camera is not found, the exception lists every
device that answered, with its serial, model, address, the host NIC that heard it, and whether the
two share a subnet — enough to tell a network problem from a wrong serial from an addressing
problem without going to the machine.

## Pixel formats

Mono 8/10/12/16, Bayer 8/10/12/16 (all four phases), RGB8, BGR8, RGBa8, BGRa8, and the packed mono
formats. Everything is folded to the toolkit's 8-bit frame types; packed data is folded directly
without a 16-bit intermediate. Formats that cannot be represented are dropped with a warning rather
than delivered wrong.

### Bayer phase

Camera-side mirroring (`ReverseX`/`ReverseY`) and an odd ROI offset shift the start of the 2×2
colour-filter tile, which changes the effective pattern — and the standard does not require the
camera to update its reported `PixelFormat` when that happens. Getting it wrong swaps red and blue
with no error at all.

This package **does not force its own calculation**, because firmware that already compensates
would then be corrected twice. It uses the camera's declaration, and when the geometry implies a
different pattern it logs both so the mismatch is visible. If colours are wrong, pin the pattern:

```csharp
new GevCamOpt { BayerPatternOverride = CvBayerPattern.GR }
```

## Health

`GevCam.GetHealth()` returns one atomic snapshot of the acquisition counters — completed and
incomplete frames, frames dropped for want of a buffer, missing packets, resend activity, and frames
whose device sequence number never arrived. Reading the fields separately would mix moments and
manufacture events that never happened, so they come together or not at all.

The snapshot names its own camera and device address. Snapshots travel — into a log line, a list, a
message to something upstream — and a number that cannot say which of eight cameras it describes
answers nothing when one of them goes bad.

The counters are cumulative since the stream started; subtract two snapshots to get your own window
rather than one this package picked for you. **Compare `StreamStartedUtc` before subtracting** — a
reconnect restarts the counters, and a consumer computing deltas across that boundary sees negative
numbers.

Alarm on `MissingPackets` and `IncompleteFrames`, never on `ResendRequests`. Packets that arrive out
of order but still in time leave a resend request behind with nothing actually lost — a measured run
logged 6,392 requests against zero missing packets over eight hours, and raising the timeouts does
not reduce it.

## Diagnostics

Attach `CvLog.Sink` before opening — discovery results, feature fallbacks, dropped frames and
Bayer-phase disagreements all go there. The `gevprobe` sample in this repository opens a camera,
records what it declares, saves a frame, and runs a timed acquisition; it is the quickest way to
find out what a specific camera does.

## Targets

`netstandard2.1` and `net8.0`. Depends on `CvInspect.Imaging` and on the GigE protocol library;
neither pulls in a native OpenCV runtime — the consuming application still chooses that.

## License

Apache-2.0. Not affiliated with OpenCV, OpenCvSharp, or any camera vendor.
