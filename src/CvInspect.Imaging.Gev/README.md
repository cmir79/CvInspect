# CvInspect.Imaging.Gev

[![ci](https://github.com/cmir79/CvInspect/actions/workflows/ci.yml/badge.svg)](https://github.com/cmir79/CvInspect/actions/workflows/ci.yml) [![NuGet CvInspect.Imaging.Gev](https://img.shields.io/nuget/v/CvInspect.Imaging.Gev?logo=nuget&label=CvInspect.Imaging.Gev)](https://www.nuget.org/packages/CvInspect.Imaging.Gev) [![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://github.com/cmir79/CvInspect/blob/main/LICENSE)

GigE camera acquisition for the [CvInspect](https://github.com/cmir79/CvInspect) machine-vision
toolkit — an `ICam` backend that speaks the protocol directly, so **no vendor SDK and no
proprietary DLLs** are needed.

> **Status.** Verified against real hardware, with **no vendor SDK and no vendor filter driver
> installed** on any of the machines. Two monochrome cameras from different vendors are discovered,
> opened, streamed and stopped on a bench, and a pair of colour cameras acquires on an inspection
> line, where the Bayer path produced the right colour with nothing pinned and nothing tuned.
> **That is hours of run time, not months.** The eight-hour figures quoted further down were
> measured on the GigE transport this package streams through, not through this `ICam` layer — the
> transport carried 1.46 TB without losing a packet, but nothing has run this backend for that
> long. Treat other cameras as unproven.

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
different pattern it logs both so the mismatch is visible. On the colour cameras run so far the
declared pattern was the right one — colour came out correct with nothing pinned and no
disagreement logged. If colours are wrong, pin the pattern:

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
of order but still in time leave a resend request behind with nothing actually lost — an eight-hour
two-camera bench run of the protocol library logged 6,392 requests against zero missing packets, and
raising the timeouts does not reduce it. The rule is what that run settled; it was measured on the
transport, not through this package.

From 0.29.0 (GevSharp 0.4.1) a block that ends short of what its leader announced is counted as
incomplete instead of being handed on with a stale tail — `IncompleteFrames` goes up by one and
`MissingPackets` by the part that never came. Cameras do cut blocks. On the Basler measured here
(acA2500-14gm), bursts of grabs cancelled within a few milliseconds of the call produced cut blocks, and
in 2 of 8 such bursts the *next* normal grab's frame was cut as well — with both 0.4.0 and 0.4.1 of the
GigE library. Later runs showed whose blocks those were: in 758 repetitions the cancelled grab's own
block never arrived cut — it was either delivered whole or not sent at all — and what got cut was the
block of a start sent shortly after it (next section). Plain timeouts cut nothing in the runs made.
Other models are unmeasured. If both counters climb only around stops or right after a cancelled grab,
read them as cut blocks rather than loss. For a drop cut by our own stop this package logs an Info line
instead of a warning until the next acquisition starts; the GigE library still warns once per open, the
first time a block ends short.

A grab whose own frame the camera cuts now fails — `TimeoutException`, with a `frame N dropped:
Incomplete` warning that is correct, because it is that grab's frame. **Before 0.29.0 that frame was
returned as the grab's answer with its bottom rows left over from an earlier frame.** The timeout
message says when blocks were dropped while the grab waited, and for what reason, so the cause is not
mistaken for a trigger setting.

**Why a start right after a cancelled grab can lose its frames, and what `GevCam` does about it
(0.29.1).** On the Basler measured here, when a single grab was stopped right after its start and its
frame was not sent, a start sent shortly afterwards could have its frames cut in transfer. Measured at 5,
30 and 100 ms exposure, 20–30 tries per point: the window in which the next start was cut ended at about
the previous start + 2 × exposure + 68 ms, and 45–90 % of the tries inside it were cut; a next start later
than the previous start + exposure + transfer time (about 70 ms here) was never cut. Why the camera does
this was not observed — it has no readable acquisition-status node — so only the timing rule is a
measurement. A stop that landed after the exposure, or 20–80 ms into a 100 ms exposure, left nothing
behind. Stopping live acquisition after 300 ms and grabbing at once was clean in 20 of 20; a live
acquisition stopped right after it started is unmeasured.

So after a single grab that ended without its frame, `GevCam` holds the next start — of a single grab or
of `StartContinuous` — until the previous start + exposure + measured transfer time + 25 ms, and logs
`waiting N ms before starting`. The hold is at most exposure + 325 ms after the previous start and is not
counted in the grab's timeout; `StartContinuous` blocks its caller for it. It is skipped when the camera
was opened with `TriggerMode` on — that mode was not measured, and a trigger arriving while acquisition is
stopped would be lost. After a grab that used a normal timeout, the moment has usually passed and nothing
waits.

Measured effect, alternating the hold on and off within the same runs, with cancellations 2–7 ms after
the call: single grabs started 30 ms later at 5 ms exposure were cut in 7 of 40 tries without the hold and
0 of 40 with it (at 100 ms exposure and 100 ms later: 3 of 20 and 0 of 20). Counting only the cancelled
grabs whose start had reached the camera, live acquisition started 75 ms later at 30 ms exposure came up
with *every* block cut — no complete frame for a whole second, until it was
stopped and restarted — in 5 of 43 tries without the hold and 0 of 42 with it; at 5 and 100 ms exposure
only its first block was cut (4 of 27 and 2 of 24 without, 0 with). Live started at once after the cancel
was never affected (0 of 35). Other models are unmeasured; on a camera that does not do this, the hold is
only a short delay after an aborted grab.

## Diagnostics

Attach `CvLog.Sink` before opening — discovery results, feature fallbacks, dropped frames and
Bayer-phase disagreements all go there. This package bridges the GigE library's own log into
`CvLog` automatically, but the bridge ends there: with no sink attached the lines are held (the
last 64, replayed on attach) rather than delivered, and `CvLog.IsAttached` is false. The `gevprobe`
sample in this repository opens a camera,
records what it declares, saves a frame, and runs a timed acquisition; it is the quickest way to
find out what a specific camera does.

**A debugger that pauses the process drops the camera.** A breakpoint, Break All, or a memory
snapshot stops the heartbeat, and once it has been quiet for the device's heartbeat timeout — a few
seconds — the device takes control back. Every call after that throws `GevControlLostException` and
only reopening recovers. **The exception says how long the gap was**, so a stall of tens of seconds
against a timeout of three names itself; a loss with no gap at all points at another application
taking the channel, or at the device restarting.

What makes this cost time is how it looks. A live view goes on showing the frame it already had, so
nothing on screen changes; the loss surfaces at the next operation that actually uses the control
channel, which is usually a grab. The grab then looks like the cause of a camera that died much
earlier — in one case 53 minutes earlier. Subscribe to `ICam.ConnectionChanged` if you want to see
it when it happens rather than when something else trips over it.

## Targets

`netstandard2.1` and `net8.0`. Depends on `CvInspect.Imaging` and on the GigE protocol library;
neither pulls in a native OpenCV runtime — the consuming application still chooses that.

## Versions

The four CvInspect packages move together — upgrade them as a set. **This is a 0.x API: breaking
changes happen, so pin versions.** What changed in each release is on the
[Releases page](https://github.com/cmir79/CvInspect/releases), one entry per version; read the entries
between the version you are on and the one you are moving to.

**If you are on a version below 0.20.0, move up.** Below it the device's 16-bit frame id was compared
by size, so acquisition stalled when the number wrapped — about every 78 minutes at 14 fps.

## License

Apache-2.0. Not affiliated with OpenCV, OpenCvSharp, or any camera vendor.
