using CvInspect.Vision;
using CvInspect.Vision.Opts;
using CvInspect.Vision.Overlay;
using OpenCvSharp;

namespace CvInspect.Demo;

/// <summary>한 번의 검사 결과 — 두 툴의 피팅과, 화면에 얹을 그래픽.</summary>
public sealed record DemoResult(bool IsOk, CvLineFit? Line, CvCircleFit? Circle, CvBlobSet Blobs, ViOverlay Overlay);

/// <summary>
/// 기본 검사기 — 파라미터 POCO 둘(라인·원)을 들고, 프레임 하나를 받아 두 툴을 돌리고 결과를 오버레이로 그린다.
/// 파라미터는 편집기가 직접 고치고 도형 바인더가 탐색 기하를 되쓰므로, 이 클래스는 "돌리고 그리는 것" 만 한다.
/// 순수 계산이라 같은 입력에 같은 결과가 나온다 — 테스트가 이 성질에 기댄다.
/// </summary>
public sealed class DemoInspector
{
    /// <summary>판의 윗변을 따라 놓은 탐색 세그먼트 — 캘리퍼가 이 선의 법선 방향으로 에지를 찾는다.</summary>
    public CvFindLineOpt Line { get; } = new()
    {
        StartX = DemoImage.PlateX + 40,
        StartY = DemoImage.PlateY,
        EndX = DemoImage.PlateX + DemoImage.PlateW - 40,
        EndY = DemoImage.PlateY,
    };

    /// <summary>구멍 자리에 놓은 기대 원 — 캘리퍼가 반경 방향으로 에지를 찾는다.</summary>
    public CvFindCircleOpt Circle { get; } = new()
    {
        CenterX = DemoImage.HoleCenterX,
        CenterY = DemoImage.HoleCenterY,
        Radius = DemoImage.HoleRadius,
    };

    /// <summary>판 안쪽의 어두운 블랍 — 탐색 영역을 판으로 좁히지 않으면 어두운 배경이 가장 큰 블랍으로 잡힌다.
    /// 영역 사각형은 드래그 도형으로 편집된다.</summary>
    public CvBlobOpt Blob { get; } = new()
    {
        Polarity = CvBlobPolarity.Dark,
        UseSearchRegion = true,
        SearchX = DemoImage.PlateX + 10,
        SearchY = DemoImage.PlateY + 10,
        SearchW = DemoImage.PlateW - 20,
        SearchH = DemoImage.PlateH - 20,
    };

    public DemoResult Run(Mat frame)
    {
        // 툴은 8비트 1채널을 본다 — 불러온 컬러 파일은 여기서 접는다.
        using var gray = frame.Channels() == 1 ? frame.Clone() : frame.CvtColor(ColorConversionCodes.BGR2GRAY);

        var line = CvLineFinder.FindWithRefit(gray, Line.StartX, Line.StartY, Line.EndX, Line.EndY, Line);
        var circle = CvCircleFinder.FindWithRefit(gray, Circle);

        var overlay = new ViOverlay();
        if (line is { } l)
        {
            overlay.Add(new ViOverlaySeg { X1 = l.StartX, Y1 = l.StartY, X2 = l.EndX, Y2 = l.EndY, HasEndArrow = true, Color = ViOverlayColor.Green });
            ViDraw.AddFullLine(overlay, (l.StartX + l.EndX) / 2, (l.StartY + l.EndY) / 2, l.AngleDeg, gray.Width, gray.Height, ViOverlayColor.Teal);
        }
        if (circle is { } c)
        {
            var pts = new List<(double X, double Y)>(48);
            for (var i = 0; i < 48; i++)
            {
                var a = i * Math.PI * 2 / 48;
                pts.Add((c.CenterX + c.Radius * Math.Cos(a), c.CenterY + c.Radius * Math.Sin(a)));
            }
            overlay.Add(new ViOverlayPoly { Points = pts, IsClosed = true, Color = ViOverlayColor.Green });
            overlay.Add(new ViOverlaySeg { X1 = c.CenterX - 6, Y1 = c.CenterY, X2 = c.CenterX + 6, Y2 = c.CenterY, Color = ViOverlayColor.Green });
            overlay.Add(new ViOverlaySeg { X1 = c.CenterX, Y1 = c.CenterY - 6, X2 = c.CenterX, Y2 = c.CenterY + 6, Color = ViOverlayColor.Green });
        }

        // 블랍은 전부 센다 — 조각난 대상도 놓치지 않게. 컨투어는 입력 이미지 공간이라 그대로 폴리라인으로 그린다.
        var blobs = CvBlobFinder.FindAll(gray, Blob, withContours: true);
        for (var i = 0; i < blobs.Hits.Count; i++)
        {
            var b = blobs.Hits[i];
            if (i < blobs.Contours.Count)
                overlay.Add(new ViOverlayPoly { Points = blobs.Contours[i], IsClosed = true, Color = ViOverlayColor.Orange });
            overlay.Add(new ViOverlaySeg { X1 = b.X - 5, Y1 = b.Y - 5, X2 = b.X + 5, Y2 = b.Y + 5, Color = ViOverlayColor.Orange });
            overlay.Add(new ViOverlaySeg { X1 = b.X - 5, Y1 = b.Y + 5, X2 = b.X + 5, Y2 = b.Y - 5, Color = ViOverlayColor.Orange });
            overlay.Add(new ViOverlayLabel { Text = $"A {b.Area:F0}", X = b.X, Y = b.Y + 12, FontSize = 12f, Color = ViOverlayColor.Orange });
        }

        var ok = line is not null && circle is not null && blobs.Hits.Count > 0;
        overlay.AddSummary(ok, ok ? "OK" : "NG",
            line is { } lf ? $"line  angle {lf.AngleDeg,6:F2}°  rms {lf.RmsPx:F2} px  pts {lf.PointCount}" : "line  not found",
            circle is { } cf ? $"circle  center ({cf.CenterX:F1}, {cf.CenterY:F1})  r {cf.Radius:F1} px  rms {cf.RmsPx:F2} px" : "circle  not found",
            blobs.Hits.Count > 0 ? $"blob  n {blobs.Hits.Count}  area {blobs.TotalArea:F0} px²  threshold {blobs.ThresholdUsed:F0}" : "blob  none");
        return new DemoResult(ok, line, circle, blobs, overlay);
    }
}
