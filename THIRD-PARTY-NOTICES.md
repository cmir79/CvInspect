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

`CvInspect.Wpf` adds nothing beyond the WPF framework that ships with .NET.

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

## Trademarks

"OpenCV" is a trademark of OpenCV.org, and "GigE Vision" is a trademark of the Association for
Advancing Automation (A3). This project is not affiliated with, endorsed by, or sponsored by
either of them, or by any camera vendor. Vendor and product names in the documentation appear
only to record what was actually tested.
