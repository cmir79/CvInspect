# CvInspect

[![ci](https://github.com/cmir79/CvInspect/actions/workflows/ci.yml/badge.svg)](https://github.com/cmir79/CvInspect/actions/workflows/ci.yml) [![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://github.com/cmir79/CvInspect/blob/main/LICENSE)

[![NuGet CvInspect](https://img.shields.io/nuget/v/CvInspect?logo=nuget&label=CvInspect)](https://www.nuget.org/packages/CvInspect)
[![NuGet CvInspect.Imaging](https://img.shields.io/nuget/v/CvInspect.Imaging?logo=nuget&label=CvInspect.Imaging)](https://www.nuget.org/packages/CvInspect.Imaging)
[![NuGet CvInspect.Imaging.Gev](https://img.shields.io/nuget/v/CvInspect.Imaging.Gev?logo=nuget&label=CvInspect.Imaging.Gev)](https://www.nuget.org/packages/CvInspect.Imaging.Gev)
[![NuGet CvInspect.Wpf](https://img.shields.io/nuget/v/CvInspect.Wpf?logo=nuget&label=CvInspect.Wpf)](https://www.nuget.org/packages/CvInspect.Wpf)

Machine-vision inspection toolkit for .NET, based on [OpenCvSharp](https://github.com/shimat/opencvsharp).

All algorithms operate on plain `OpenCvSharp.Mat` images and are pure and deterministic —
the same input always produces the same result, so tools can be replayed and verified offline.

## Why CvInspect

This is the algorithm layer of a production inspection stack, extracted and published as-is — the
same code runs on factory lines today. What it does differently from "OpenCV plus a display":

- **A pattern finder built around measurements, not defaults.** Rotation (and optional scale) is a
  coarse-to-fine stepped search with parabolic angle interpolation and a parallel angle sweep.
  Rectangular templates rotate the *scene* rather than the template, because masked matching
  measured 6× slower; circular templates keep the mask since it does real work there. A search
  region is the *allowed centre region*, so a target half outside the box is still found. Empty
  scenes finish at the coarse stage and return early (measured 3.7 s → 0.05 s). Multiple hits come
  out non-overlapping with a suppression radius. The minimum reliable coarse size (`11 px`
  geometric-mean side) was derived from 80-trial-per-cell synthetic sweeps across aspect ratios,
  and the finder *reports* it rather than silently overriding your settings.
- **A fixture without a coordinate-space tree.** The pattern finder returns a `CvPose` — one
  similarity transform (rotation, isotropic scale, translation about the taught origin) — and
  `CvInspGeom.XformByPose` moves every taught tool geometry (caliper segments, circle centres,
  search regions) onto the found part, so downstream tools follow it. The transform is explicit and
  algebraic (`Inverse`, `Compose`) rather than an implicit space tree: replays stay deterministic and
  nothing hides in a hierarchy, at the price that stacking fixtures is the caller's job.
- **Caliper metrology.** Edges come from 1-D projected profiles with parabolic sub-pixel
  refinement; line and circle finders are caliper arrays with two-pass refit, outlier drop and
  RMS/angle gates, and every fit carries its residual so you can gate on quality, not just on found/not found.
- **Pure and deterministic.** Tools are static functions over `Mat`: same input, same output.
  Recipes can be replayed offline and the test suite pins real defects that were observed.
- **Teaching UI without boilerplate.** Option POCOs carry attributes; from those alone the WPF
  package builds the property editor, the draggable search shapes and the localized labels, and
  `System.Text.Json` persists them with string enums.
- **Acquisition without vendor SDKs.** `CamFrame` is a GC-owned buffer with no lifetime contract,
  GigE cameras are driven by a managed protocol implementation (no drivers, no vendor DLLs), and
  virtual/video sources make offline work identical to live work.
- **Small surface.** The core depends on the managed `OpenCvSharp4` assembly only, targets
  `netstandard2.1` for Unity and Mono, and never uses reflection-based serialization.

## Tools

- **Caliper** (`CvCaliper`) — projected edge detection along a search axis with polarity / contrast / selection rules
- **Line finder** (`CvLineFinder`) — caliper array + robust line fit (outlier drop, refit, RMS gate)
- **Circle finder** (`CvCircleFinder`) — radial caliper array + circle fit, with convergent two-pass refit
- **Hough circle finder** (`CvHoughCircleFinder`) — classic Hough transform search
- **Pattern matcher** (`CvPatternFinder` / `CvPatternTeach`) — rotation/scale-stepped masked template matching with coarse-to-fine search
- **Blob finder** (`CvBlobFinder`) — thresholded connected components with geometric filters
- **Color segmentation** (`CvColorSegment`) — HSV band segmentation with trainable band
- **Region / ring masks, ring fill, unwrap geometry, pose & space mapping, geometry fitting**
  (`CvRegionMask`, `CvRingFill`, `CvUnwrapGeom`, `CvPose`, `CvSpaceMap`, `CvFit`, `CvInspGeom`, `CvLineGeom`)
- **Overlay primitives** (`ViOverlay`, `ViDraw`, `ViHud`) — renderer-neutral result graphics
  (segments, labels, rects, polylines) that any display layer can draw

Companion packages keep this core platform-neutral: **CvInspect.Wpf** (WPF display /
shape-editing controls), **CvInspect.Imaging** (camera acquisition contract with virtual /
VideoCapture sources, and a factory that vendor adapters register into) and
**CvInspect.Imaging.Gev** (GigE Vision cameras spoken to directly — no vendor SDK, no
proprietary DLLs).

## Install

```
dotnet add package CvInspect
```

The `net8.0` asset depends only on the managed `OpenCvSharp4` assembly; the
`netstandard2.1` asset additionally depends on `System.Text.Json` (serialization
attributes and translation-file parsing), which matters if you import DLLs into Unity
by hand. **Pick a native runtime yourself** — for example one of:

```
dotnet add package OpenCvSharp4.runtime.win
dotnet add package OpenCvSharp4.runtime.ubuntu.20.04-x64
```

The Windows `OpenCvSharp4.Windows` meta-package also works. Either way, the Windows native
runtime ships an `opencv_videoio_ffmpeg` codec that is LGPL-2.1 — the meta-package pulls in
`OpenCvSharp4.runtime.win`, so picking the runtime package directly carries the same codec.
Choosing the runtime is deliberately left to you; see
[THIRD-PARTY-NOTICES.md](https://github.com/cmir79/CvInspect/blob/main/THIRD-PARTY-NOTICES.md).

**If you do not want that codec in your output**, drop it during the build. Only
`CvInspect.Imaging`'s `VideoCaptureCam` (video-file playback) needs it, and `videoio` is
demand-loaded, so everything else keeps working without it. Two targets are required, because
the publish list and the build output are separate item flows — removing from one leaves the
file in the other:

```xml
<!-- Directory.Build.targets, at your solution root -->
<Project>
  <!-- Publish list. A single-file bundle is fixed at ComputeResolvedFilesToPublishList, so the
       removal has to run after that target: removing after ComputeFilesToPublish only stops the
       loose copy and leaves the codec inside the bundled exe. -->
  <Target Name="DropVideoCodecFromPublish" AfterTargets="ComputeResolvedFilesToPublishList">
    <ItemGroup>
      <ResolvedFileToPublish Remove="@(ResolvedFileToPublish)"
                             Condition="$([System.String]::Copy('%(ResolvedFileToPublish.Filename)').StartsWith('opencv_videoio_ffmpeg'))" />
    </ItemGroup>
  </Target>
  <!-- Build output (bin\). -->
  <Target Name="DropVideoCodecFromBuild" AfterTargets="ResolveLockFileCopyLocalFiles">
    <ItemGroup>
      <ReferenceCopyLocalPaths Remove="@(ReferenceCopyLocalPaths)"
                               Condition="$([System.String]::Copy('%(Filename)').StartsWith('opencv_videoio_ffmpeg'))" />
    </ItemGroup>
  </Target>
</Project>
```

Measured on this repository's own sample (`samples/CvInspect.GevProbe`): publish output drops
from 95 MB to 68 MB and `bin\` no longer carries the codec.

## Targets

`netstandard2.1` and `net8.0`. The `netstandard2.1` asset covers Unity (2021+) and Mono.
The library itself never uses reflection-based JSON serialization (translation files are read
with `JsonDocument`), so it is IL2CPP/AOT friendly.

## Localization

Property metadata (`[CvCategory]`, `[CvName]`, `[CvDesc]`) derives from the standard
`System.ComponentModel` attributes, so any property grid renders localized names without
knowing this library. Resolution order:

1. `CvLoc.Resolver` — inject your own translator (`Func<string, string?>`, return `null` for unknown keys)
2. Built-in tables — the embedded `cv.{culture}.json` is loaded first, and a loose
   `Assets/lang/cv.{culture}.json` next to the executable overrides it per key;
   `CvLoc.Culture` selects the language (`"en"` default, `"ko"` included; regional tags
   such as `ko-KR` fold to their parent language). The package ships the tables embedded
   only — for field edits, create the loose file yourself with just the keys you want to
   change (partial files merge over the embedded table)
3. The raw key, with a one-time `CvLoc.MissingKey` notification

Category display order is exposed as `CvCategoryAttribute.Order` (see `ICvOrderedCategory`);
unnumbered categories sort last. The category string itself carries no ordering prefix.

Note: `CategoryAttribute.Category` caches its translated string per attribute instance on
first read (a BCL limitation), and `TypeDescriptor`-based grids reuse those instances for
the process lifetime. A grid that must follow a live language switch should re-translate
via `CvCategoryAttribute.ScopedKey` + `CvLoc.T` rather than the cached `Category` string.

## Pixel source contract

`ICvPixelSource` is the core's small interop contract for 8-bit pixel buffers (`Pixels`, `Width`,
`Height`, `Stride`, `Channels`). The core itself never consumes it; it exists so sibling packages
can meet without referencing each other — `CvInspect.Imaging`'s `CamFrame` implements it and
`CvInspect.Wpf`'s `CvDispCtrl` accepts it, holding the array by reference instead of copying.
Implementations must treat `Pixels` as immutable once published.

## Logging

The library never writes logs itself. Attach a sink once at startup:

```csharp
CvLog.Sink = (level, source, message, ex) => myLogger.Log(level, source, message, ex);
```

## Serializing tool options

Tool option POCOs (`Cv*Opt`) are annotated for `System.Text.Json`: delegate-typed hook
properties (train hooks, shape-sync callbacks) and volatile runtime values are marked
`[JsonIgnore]`, and option enums carry `JsonStringEnumConverter` so they serialize as
identifier strings — saved recipes survive enum evolution, and old integer-valued files
still deserialize. If you persist them with another serializer (e.g. Json.NET in Unity),
configure it to skip delegate-typed members.

## Samples

- `samples/CvInspect.Demo` — a WPF window that wires everything together: `VirtualCam` playing a
  synthetic part, `CvDispCtrl` showing the `CamFrame` directly, draggable search shapes, the
  `CvPropEditCtrl` parameter editor, and a line, circle and blob inspection drawn as an overlay
  ([screenshot](https://github.com/cmir79/CvInspect/blob/main/samples/CvInspect.Demo/screenshot.png)).
- `samples/CvInspect.GevProbe` — a console tool that opens a GigE camera with no vendor SDK and
  dumps what the adapter sees (transport statistics, frame geometry, saved frames).
- `samples/CvInspect.UsbCamProbe` — a console tool for verifying USB webcam identity on a
  multi-camera PC: enumerates devices, snaps one frame per index and backend, then reopens each
  camera by VID/PID through `CamOpt.SerialNumber` so the two pictures can be compared.

## Tests

```
dotnet test
```

The suite is a regression harness rather than an exhaustive unit-test corpus: each case pins a
defect that was actually observed — Bayer phase parity, packed-format bit depth, buffer length
bounds, reconnect invariants, exposure-grid snapping — so a failure message states why that case
exists. It needs no camera and no network.

`tests/CvInspect.Wpf.Tests` renders `CvDispCtrl` off-screen (no window) and compares pixels: the
`ICvPixelSource` path against the `Mat` path, row padding, and what happens when the pixel
contract is violated. It is WPF, so it builds and runs on Windows only — on other platforms run
the core suite alone with `dotnet test tests/CvInspect.Tests`.

## Limits

Read these before adopting — they are real, and they are not going away soon.

- **Template matching is intensity correlation, not geometric matching.** Scores degrade under
  occlusion and strong non-linear lighting; rotation and scale are stepped searches (angle
  refined to sub-step, scale on a grid); circular (masked) templates cost about 6× rectangular
  ones; everything runs on the CPU (`Parallel.For`), there is no GPU path.
- **Calipers need a starting pose.** Line and circle finders search only within the caliper
  length around the taught geometry — a pattern finder or fixture has to bring them close first.
  The Hough circle finder is the only global search.
- **8-bit only through acquisition and display.** `CamFrame` and `CvDispCtrl` carry Mono8/Bgr24/
  Bgra32; 16-bit sensor data is folded to 8 bits at the adapter. Individual core tools may accept
  other depths, but nothing around them does.
- **USB webcam identity is verified on Windows only.** `CamOpt.SerialNumber` selects a webcam by VID/PID or
  instance id; the Windows path was checked on one two-camera PC (different models, three backends), the
  Linux path has no hardware run at all, and macOS is unsupported.
- **Display is WPF, so Windows only.** The core and Imaging packages are cross-platform; the WPF
  package and its off-screen test suite build on Windows alone.
- **GigE has hours, not years, behind it.** Two cameras from two vendors have been streamed
  end-to-end; production run time has not accumulated. Bayer is demosaiced to Bgr24, packed
  10/12-bit is folded to 8, vendor-specific features are not exposed.
- **It is a toolkit, not a framework.** No tool chain, recipe persistence or run history; no
  pixel-to-millimetre calibration beyond scalar resolution fields; no lens distortion correction;
  no OCR, deep learning or 3D.
- **0.x API.** Breaking changes have happened (the `CvDispCtrl.Frame` type changed in 0.16.0) and
  will happen again before 1.0 — pin versions. Releases are cut from `v*` tags only; `main` carries
  the *next* version number while changes accumulate, so a version bump is not one change but one release.
- **Tests are a regression harness, not coverage.** Each case pins a defect that was actually
  seen; accuracy figures quoted above come from synthetic scenes.
- **Comments are Korean.** READMEs are English; XML documentation (what IntelliSense shows) is
  Korean. Contributions in either language are fine.
- **The native OpenCV runtime is your choice** and the Windows runtime bundles an LGPL ffmpeg
  codec — see [THIRD-PARTY-NOTICES.md](https://github.com/cmir79/CvInspect/blob/main/THIRD-PARTY-NOTICES.md).

## License

Apache-2.0. Not affiliated with OpenCV or OpenCvSharp.
Dependency licences and trademark notes: [THIRD-PARTY-NOTICES.md](https://github.com/cmir79/CvInspect/blob/main/THIRD-PARTY-NOTICES.md).
