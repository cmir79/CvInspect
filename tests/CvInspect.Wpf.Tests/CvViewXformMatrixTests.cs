// 뷰 변환(CvViewXform)이 WPF Matrix 와 비트 단위로 같은 값을 내는지 — 표시 표면은 0.33 까지 Matrix 로 뷰를 들고 있었고,
// UI 중립 계층으로 옮기면서 같은 식을 같은 순서로 계산하게 했다. 끝자리 하나만 달라도 히트 판정 경계(±8 DIP 칸, 변 길이 21 DIP)
// 에서 잡히는 것이 바뀔 수 있어 근사 비교로는 부족하다.
// 조작 순서는 옛 표면과 같다: 맞춤 = Identity → Scale → Translate, 휠·버튼 줌 = 배율 자르기 → ScaleAt, 팬 = Translate,
// 화면 → 이미지 = 사본 Invert → Transform, 이미지 → 화면 = Transform.
// 살아 있음 대조: 같은 루프에서 일부러 다른 식((sx − ox) / s)으로 구한 값이 Matrix 와 한 번 이상 달라야 한다 —
// 안 다르면 이 비교는 식 순서를 가려내지 못하는 무력한 시험이다.
using System.Windows;
using System.Windows.Media;
using CvInspect.Display;
using Xunit;
using Xunit.Abstractions;

namespace CvInspect.Wpf.Tests;

public class CvViewXformMatrixTests(ITestOutputHelper output)
{
    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static void SameBits(double matrix, double xform, string what)
    {
        if (!Same(matrix, xform)) Assert.Fail($"{what}: Matrix {matrix:R} vs CvViewXform {xform:R} — the view math must match the WPF Matrix path bit for bit");
    }

    [Fact]
    public void ViewXformMatchesWpfMatrixBitForBit()
    {
        var rng = new Random(20261026);
        var compared = 0;
        var zooms = 0;
        var clampedZooms = 0;
        var altDiffers = 0;

        var identityRuns = 0;
        for (var run = 0; run < 250; run++)
        {
            var viewW = 20 + rng.NextDouble() * 2000;
            var viewH = 20 + rng.NextDouble() * 1500;
            var imgW = rng.Next(1, 9000);
            var imgH = rng.Next(1, 9000);

            CvViewXform x;
            Matrix m;
            if (run < 200)
            {
                // 맞춤 — 옛 표면 그대로
                Assert.True(CvViewXform.TryFit(viewW, viewH, imgW, imgH, out x));
                var scale = Math.Min(viewW / imgW, viewH / imgH) * 0.97;
                scale = Math.Clamp(scale, CvViewXform.MinScale, CvViewXform.MaxScale);
                m = Matrix.Identity;
                m.Scale(scale, scale);
                m.Translate((viewW - imgW * scale) / 2, (viewH - imgH * scale) / 2);
            }
            else
            {
                // 맞춤이 실패한 표면(8 DIP 미만·이미지 폭 0) — 옛 표면의 뷰는 Matrix.Identity 그대로 남고, 다음 팬·줌은 WPF 의
                // Identity·이동만 경로를 탄다(배율+이동 경로와 다른 분기). 절반은 팬을 먼저 해서 이동만 행렬에서 출발한다.
                Assert.False(CvViewXform.TryFit(5, viewH, imgW, imgH, out x));
                m = Matrix.Identity;
                identityRuns++;
                if (run >= 225)
                {
                    var dx0 = (rng.NextDouble() - 0.5) * 600;
                    var dy0 = (rng.NextDouble() - 0.5) * 600;
                    x = x.Translate(dx0, dy0);
                    m.Translate(dx0, dy0);
                }
            }

            for (var step = 0; step < 40; step++)
            {
                switch (rng.Next(4))
                {
                    case 0:
                    case 1:
                    {
                        // 줌 — 휠 한 칸이 대부분, 가끔 임의 배수, 가끔 끝값에 닿는 큰 배수
                        var pick = rng.Next(10);
                        var factor = pick < 6 ? (rng.Next(2) == 0 ? 1.25 : 1 / 1.25)
                            : pick < 9 ? 0.3 + rng.NextDouble() * 3
                            : (rng.Next(2) == 0 ? 1e4 : 1e-4);
                        var cx = rng.NextDouble() * viewW;
                        var cy = rng.NextDouble() * viewH;

                        var changed = x.TryZoomAt(factor, cx, cy, out var next);
                        var target = Math.Clamp(m.M11 * factor, CvViewXform.MinScale, CvViewXform.MaxScale);
                        var f = target / m.M11;
                        var oldChanged = !(Math.Abs(f - 1) < 1e-9);
                        Assert.True(oldChanged == changed, $"zoom change report must match the old early-return (factor {factor:R}, scale {m.M11:R})");
                        if (oldChanged) m.ScaleAt(f, f, cx, cy);
                        else clampedZooms++;
                        x = next;
                        zooms++;
                        break;
                    }
                    case 2:
                    {
                        var dx = (rng.NextDouble() - 0.5) * 600;
                        var dy = (rng.NextDouble() - 0.5) * 600;
                        x = x.Translate(dx, dy);
                        m.Translate(dx, dy);
                        break;
                    }
                    default:
                        break;   // 상태만 다시 대조
                }

                SameBits(m.M11, x.Scale, "scale");
                SameBits(m.M22, x.Scale, "scale (y)");
                SameBits(m.OffsetX, x.OffsetX, "offset x");
                SameBits(m.OffsetY, x.OffsetY, "offset y");

                var inv = m;
                inv.Invert();
                for (var k = 0; k < 5; k++)
                {
                    var ix = (rng.NextDouble() * 3 - 1) * imgW;
                    var iy = (rng.NextDouble() * 3 - 1) * imgH;
                    var ps = m.Transform(new Point(ix, iy));
                    var xs = x.ToScreen(ix, iy);
                    SameBits(ps.X, xs.X, "ToScreen x");
                    SameBits(ps.Y, xs.Y, "ToScreen y");

                    var sx = (rng.NextDouble() * 1.4 - 0.2) * viewW;
                    var sy = (rng.NextDouble() * 1.4 - 0.2) * viewH;
                    var pi = inv.Transform(new Point(sx, sy));
                    var xi = x.ToImage(sx, sy);
                    SameBits(pi.X, xi.X, "ToImage x");
                    SameBits(pi.Y, xi.Y, "ToImage y");

                    // 대조군 — 수학적으로 같은 다른 식. 끝자리가 갈려야 정상이다.
                    var altX = (sx - x.OffsetX) / x.Scale;
                    var altY = (sy - x.OffsetY) / x.Scale;
                    if (!Same(pi.X, altX) || !Same(pi.Y, altY)) altDiffers++;
                    compared++;
                }
            }
        }

        output.WriteLine($"compared {compared}, zooms {zooms} ({clampedZooms} at a limit), control differs {altDiffers}, runs from Identity {identityRuns}");
        Assert.True(compared >= 50000, $"enough random points were compared ({compared})");
        Assert.True(identityRuns == 50, $"the runs that start from an unfitted Identity view ran ({identityRuns})");
        Assert.True(zooms > 1000 && clampedZooms > 0, $"the run covered zooms including ones stopped at the limits ({zooms} zooms, {clampedZooms} at a limit)");
        Assert.True(altDiffers > 0,
            $"the control formula (sx − ox) / s must differ from Matrix at least once — otherwise this test cannot tell formula orders apart ({altDiffers}/{compared})");
    }
}
