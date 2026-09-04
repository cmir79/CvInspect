# CvInspect

Machine-vision inspection toolkit for .NET, based on [OpenCvSharp](https://github.com/shimat/opencvsharp).

All algorithms operate on plain `OpenCvSharp.Mat` images and are pure and deterministic —
the same input always produces the same result, so tools can be replayed and verified offline.

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

The Windows `OpenCvSharp4.Windows` meta-package also works, but note that its bundled
`opencv_videoio_ffmpeg` codec is LGPL-2.1 — choosing the runtime is deliberately left to you.

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

## License

Apache-2.0. Not affiliated with OpenCV or OpenCvSharp.
