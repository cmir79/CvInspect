using OpenCvSharp;

namespace CvInspect.Vision.Overlay;

/// <summary>
/// 오버레이 기하 보조 — 한 점을 지나는 방향선을 이미지 전체 관통으로 발행 (원본 이미지 공간, 호스트 공용).
/// 렌더러가 이미지 경계 클립을 안 하므로 세그먼트를 경계로 직접 잘라 발행. 화살촉 = + 방향 끝
/// (각도 부호 시인). 축소 화면에서도 각도선이 한눈에 보이게 — 짧은 타겟 선분은 멀리서 안 보인다.
/// </summary>
public static class ViDraw
{
    public static void AddFullLine(ViOverlay overlay, double x, double y, double angleDeg, int imgW, int imgH, ViOverlayColor color)
    {
        var rad = angleDeg * Math.PI / 180.0;
        var len = Math.Sqrt((double)imgW * imgW + (double)imgH * imgH);
        var p1 = new OpenCvSharp.Point((int)Math.Round(x - Math.Cos(rad) * len), (int)Math.Round(y - Math.Sin(rad) * len));
        var p2 = new OpenCvSharp.Point((int)Math.Round(x + Math.Cos(rad) * len), (int)Math.Round(y + Math.Sin(rad) * len));
        if (!Cv2.ClipLine(new OpenCvSharp.Rect(0, 0, imgW, imgH), ref p1, ref p2)) return;

        overlay.Add(new ViOverlaySeg
        {
            Color = color,
            X1 = p1.X,
            Y1 = p1.Y,
            X2 = p2.X,
            Y2 = p2.Y,
            HasEndArrow = true,
        });
    }

    /// <summary>기준각 → 측정각 스윕 아크 + 진행 방향 화살촉 + 텍스트 — 회전 편차(Δ)의 방향·크기 시인.
    /// sweepDeg 는 ±180 정규화 부호각 전제(짧은 쪽 스윕 — 180° 초과 아크를 그리지 않는다).
    /// 이미지 규약(y-아래)에서 + = 화면 시계방향. 텍스트 앵커는 스윕 중간각 반경 바깥, 경계 잘림 방지 클램프.</summary>
    public static void AddAngleArc(ViOverlay overlay, double cx, double cy, double radius, double fromDeg, double sweepDeg, int imgW, int imgH, ViOverlayColor color, string text)
    {
        if (Math.Abs(sweepDeg) > 1e-6)
        {
            var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweepDeg) / 3.0));
            var pts = new (double X, double Y)[steps + 1];
            for (var i = 0; i <= steps; i++)
            {
                var rad = (fromDeg + sweepDeg * i / steps) * Math.PI / 180.0;
                pts[i] = (cx + Math.Cos(rad) * radius, cy + Math.Sin(rad) * radius);
            }
            overlay.Add(new ViOverlayPoly { Color = color, Points = pts, IsClosed = false });

            // 진행 방향 화살촉 — 화살촉 렌더는 세그먼트 전용이라 끝 구간(≥10px 확보)을 접선 세그먼트로 겹쳐 발행.
            var tail = pts.Length - 2;
            while (tail > 0 && Dist(pts[tail], pts[^1]) < 10) tail--;
            overlay.Add(new ViOverlaySeg
            {
                Color = color,
                X1 = pts[tail].X,
                Y1 = pts[tail].Y,
                X2 = pts[^1].X,
                Y2 = pts[^1].Y,
                HasEndArrow = true,
            });
        }

        // 텍스트 — 스윕 중간각 방향 반경 바깥. 위쪽 반원이면 앵커 위로 그려 아크와 겹침 방지.
        var midRad = (fromDeg + sweepDeg / 2.0) * Math.PI / 180.0;
        var lx = Math.Clamp(cx + Math.Cos(midRad) * (radius + 28), 60, Math.Max(60, imgW - 60.0));
        var ly = Math.Clamp(cy + Math.Sin(midRad) * (radius + 28), 24, Math.Max(24, imgH - 24.0));
        overlay.Add(new ViOverlayLabel
        {
            Text = text,
            FontSize = 15f,
            Color = color,
            X = lx,
            Y = ly,
            Align = Math.Sin(midRad) < 0 ? ViOverlayAlign.BottomCenter : ViOverlayAlign.TopCenter,
        });
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
