# Third-party notices

CvInspect contains no third-party source code. Every source file in this repository is
original work; nothing was copied or adapted from another project. This file records what the
published packages depend on, so anyone redistributing a build knows which notices travel
with it.

## Package dependencies

| Package | License | Pulled in by |
|---|---|---|
| [OpenCvSharp4](https://github.com/shimat/opencvsharp) | Apache-2.0 | every package — `Mat` is the image currency |
| [System.Text.Json](https://github.com/dotnet/runtime) | MIT | `CvInspect`, `netstandard2.1` asset only |
| [GevSharp](https://github.com/cmir79/GevSharp) | Apache-2.0 | `CvInspect.Imaging.Gev` |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | MIT | `CvInspect.Avalonia` |
| Avalonia.Remote.Protocol | MIT | `CvInspect.Avalonia`, through `Avalonia` |
| MicroCom.Runtime | MIT | `CvInspect.Avalonia`, through `Avalonia` |
| Avalonia.BuildServices | MIT | `CvInspect.Avalonia`, through `Avalonia` — build time only, see below |

`CvInspect.Wpf` adds nothing beyond the WPF framework that ships with .NET.

The Avalonia rows were checked against the `<license>` element of the nuspecs restored for this
repository: `Avalonia` and `Avalonia.Remote.Protocol` 12.1.4, `MicroCom.Runtime` 0.11.6,
`Avalonia.BuildServices` 11.3.2.

## Avalonia: the application picks the rest, and the build sends telemetry

`CvInspect.Avalonia` references `Avalonia` alone. The platform, renderer, text shaper and theme
(`Avalonia.Desktop`, `Avalonia.Skia`, `Avalonia.HarfBuzz`, `Avalonia.Themes.Fluent`, …) are the
application's choice, and so are their notices. Two that every desktop application ends up with:
`SkiaSharp` 3.119.4 and `HarfBuzzSharp` 8.3.1.3 (both MIT by their nuspecs). Their
`*.NativeAssets.*` packages ship native Skia and HarfBuzz binaries and carry their own
`THIRD-PARTY-NOTICES.txt` for what those binaries contain — read those if you redistribute. The
licenses of `Avalonia.Desktop` and the platform packages it brings were not checked here.

**Build telemetry.** `Avalonia.BuildServices` comes in with `Avalonia` and runs a build task before
compilation that sends anonymous build statistics to Avalonia; the list of what it sends is in that
package's README. It reaches projects that reference only `CvInspect.Avalonia` — this was measured
with a consumer project that referenced nothing else. That README states that collection runs at
build time only, not in deployed applications. This package does not turn it off on your behalf.
To opt out, set the environment variable `AVALONIA_TELEMETRY_OPTOUT` on the build machine; the task checks only that the variable exists, so
any value works. Read from the task's code in 11.3.2: when an Avalonia Accelerate license key of the
Community or Trial tier is present, the opt-out is ignored. This repository's CI sets the variable.
Whether anything is actually sent was not observed here — only that the task runs.

## Native OpenCV is your choice, and so is its notice

These packages depend on the *managed* `OpenCvSharp4` assembly only — no native binary ships
inside them. The consumer adds an `OpenCvSharp4.runtime.*` package (or supplies its own OpenCV
build), and that choice carries licenses this project cannot pick on your behalf:

- **OpenCV** is Apache-2.0 from 4.5.0 onwards (3-clause BSD before that).
- **FFmpeg is the one worth reading.** `OpenCvSharp4.runtime.win` ships
  `opencv_videoio_ffmpeg*.dll` next to `OpenCvSharpExtern.dll`, and the `OpenCvSharp4.Windows`
  meta-package pulls that same runtime in — so choosing the runtime package directly does not
  avoid the codec. FFmpeg is **LGPL-2.1-or-later**, which attaches redistribution conditions
  that Apache-2.0 does not. Checked against `OpenCvSharp4.runtime.win` 4.13.0.20260627.

Only `CvInspect.Imaging`'s `VideoCaptureCam` (video-file playback) reaches for that codec.
Nothing else here touches `videoio`, and it is demand-loaded, so a build that drops the codec
keeps working. If LGPL redistribution is not acceptable to you, the README's **Install** section
carries the two MSBuild targets that remove it from both the publish list and the build output
(verified here: publish output 95 MB to 68 MB, nothing named `opencv_videoio_ffmpeg*` left in
either place).

## Test-only dependencies

xunit and xunit.runner.visualstudio (Apache-2.0) and Microsoft.NET.Test.Sdk (MIT) are
referenced by `tests/` alone and are not part of any published package.

`tests/CvInspect.Avalonia.Tests` adds, also test-only: xunit.v3 3.2.2 and xunit.runner.visualstudio
4.0.1 (Apache-2.0), and Avalonia.Headless.XUnit, Avalonia.Skia, Avalonia.Themes.Fluent and
Avalonia.Fonts.Inter 12.1.4 (MIT), which bring SkiaSharp and HarfBuzzSharp (MIT) — all by their
nuspecs. The Inter font itself is embedded in the `Avalonia.Fonts.Inter` assembly, and the package
carries no separate font license file; the font's own license was not checked here.

## Trademarks

"OpenCV" is a trademark of OpenCV.org, and "GigE Vision" is a trademark of the Association for
Advancing Automation (A3). This project is not affiliated with, endorsed by, or sponsored by
either of them, or by any camera vendor. Vendor and product names in the documentation appear
only to record what was actually tested.
