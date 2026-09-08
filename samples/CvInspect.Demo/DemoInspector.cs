using CvInspect.Vision;
using CvInspect.Vision.Opts;
using CvInspect.Vision.Overlay;
using OpenCvSharp;

namespace CvInspect.Demo;

/// <summary>한 번의 검사 결과 — 픽스처 포즈, 세 툴의 결과, 화면에 얹을 그래픽.</summary>
public sealed record DemoResult(bool IsOk, CvPose? Pose, double PatternScore, CvLineFit? Line, CvCircleFit? Circle, CvBlobSet Blobs, ViOverlay Overlay);

/// <summary>
/// 기본 검사기 — 패턴(픽스처) + 라인 + 원 + 블랍. 파라미터 POCO 를 들고 프레임 하나를 받아 툴을 돌리고 결과를 오버레이로 그린다.
/// 패턴이 학습돼 있고 발견되면 그 <see cref="CvPose"/> 로 나머지 툴의 티칭 기하(세그먼트·원 중심·탐색 사각)를 부품이
/// 발견된 자리로 옮겨 실행한다 — 좌표공간 트리 없이 픽스처를 하는 방식이다. 학습 전이거나 못 찾으면 티칭 자리 그대로 돌린다.
/// 파라미터는 편집기가 직접 고치고 도형 바인더가 탐색 기하를 되쓰므로, 이 클래스는 "학습·돌리고·그리는 것" 만 한다.
/// 순수 계산이라 같은 입력에 같은 결과가 나온다 — 테스트가 이 성질에 기댄다.
/// </summary>
public sealed class DemoInspector
{
    /// <summary>판의 왼쪽 위 모서리와 L 표식을 함께 담는 학습 영역 — 모서리의 긴 에지 둘이 각도를, L 이 방향(180° 구분)을 준다.
    /// L 만 담으면 44px 특징에서 각도를 재는 셈이라 1° 안팎이 흔들린다.</summary>
    public CvPatternOpt Pattern { get; } = new()
    {
        TrainShape = CvTrainShape.Rect,
        TrainX = DemoImage.PlateX - 24,
        TrainY = DemoImage.PlateY - 24,
        TrainW = 120,
        TrainH = 120,
        UseAngleSearch = true,
        AngleZoneDeg = 20,
        CoarseStepDeg = 4,
        FineStepDeg = 1,
        AcceptScore = 0.6,
    };

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
    /// 표식(L)도 어두운 블랍이라 MinArea 로 거른다(L 면적 ≈ 1,000, 구멍 ≈ 6,360).</summary>
    public CvBlobOpt Blob { get; } = new()
    {
        Polarity = CvBlobPolarity.Dark,
        MinArea = 2000,
        UseSearchRegion = true,
        SearchX = DemoImage.PlateX + 10,
        SearchY = DemoImage.PlateY + 10,
        SearchW = DemoImage.PlateW - 20,
        SearchH = DemoImage.PlateH - 20,
    };

    /// <summary>편집기의 Train 버튼이 학습할 프레임을 어디서 가져올지 — 호스트(VM)가 현재 프레임을 꽂는다.
    /// 반환 Mat 은 이쪽이 dispose 한다.</summary>
    public Func<Mat?>? FrameProvider { get; set; }

    public DemoInspector()
    {
        Pattern.TrainHook = () =>
        {
            using var frame = FrameProvider?.Invoke();
            return frame is not null && Train(frame);
        };
    }

    /// <summary>학습 — 학습 영역을 잘라 템플릿으로 삼고 원점을 기억한다. 영역이 이미지 밖이거나 특징이 없으면 false.
    /// 학습 영역의 회전은 지원하지 않는다(부품을 돌리는 것이지 학습 상자를 돌리는 것이 아니다).</summary>
    public bool Train(Mat frame)
    {
        if (Math.Abs(Pattern.TrainAngleDeg) > 1e-9) return false;
        using var gray = ToGray(frame);
        using var template = CvPatternTeach.CropRect(gray, Pattern.TrainX, Pattern.TrainY, Pattern.TrainW, Pattern.TrainH, out var ox, out var oy);
        if (template is null || !CvPatternTeach.HasFeature(template, Pattern.TrainShape)) return false;
        Cv2.ImEncode(".png", template, out var png);
        Pattern.TemplatePng = png;
        Pattern.TrainedOriginX = ox;
        Pattern.TrainedOriginY = oy;
        Pattern.TrainedAngleDeg = 0;
        Pattern.TrainedShape = Pattern.TrainShape;
        Pattern.Trained = true;
        return true;
    }

    public DemoResult Run(Mat frame)
    {
        using var gray = ToGray(frame);
        var overlay = new ViOverlay();

        // 1) 픽스처 — 학습돼 있으면 패턴을 찾고, 찾았으면 그 포즈로 나머지 툴의 기하를 옮긴다.
        CvPose? pose = null;
        var score = 0.0;
        (int W, int H) templ = (0, 0);
        if (Pattern.Trained && Pattern.TemplatePng is not null)
        {
            var m = CvInspGeom.MatchPattern(gray, Pattern);
            score = m.Score;
            templ = m.Templ;
            if (m.Present) pose = m.Pose;
        }
        (double X, double Y) F(double x, double y) => pose is { } p ? CvInspGeom.XformByPose(Pattern, p, x, y) : (x, y);

        // 2) 라인 — 티칭 세그먼트를 옮겨 캘리퍼를 세운다.
        var (sx, sy) = F(Line.StartX, Line.StartY);
        var (ex, ey) = F(Line.EndX, Line.EndY);
        var line = CvLineFinder.FindWithRefit(gray, sx, sy, ex, ey, Line);

        // 3) 원 — 기대 중심만 옮긴다(반경은 스케일 탐색을 안 쓰므로 그대로).
        var (cx, cy) = F(Circle.CenterX, Circle.CenterY);
        var circle = CvCircleFinder.FindWithRefit(gray, cx, cy, Circle);

        // 4) 블랍 — 탐색 사각은 축 정렬이라 그대로 돌릴 수 없다. 회전한 사각 안에 들어가는 최대 축 정렬 사각을 쓴다.
        var blobOpt = MovedBlobOpt(pose);
        var blobs = CvBlobFinder.FindAll(gray, blobOpt, withContours: true);

        // --- 그래픽 ---
        if (pose is { } pp)
        {
            // 발견된 패턴 자리(템플릿 크기의 회전 사각)와 학습 원점이 옮겨 간 자리.
            overlay.Add(new ViOverlayRect { CenterX = pp.FoundX, CenterY = pp.FoundY, Width = templ.W, Height = templ.H, AngleDeg = pp.ThetaDeg, Color = ViOverlayColor.Cyan });
            overlay.Add(new ViOverlayLabel { Text = $"pattern {score:F2}  {pp.ThetaDeg:+0.0;-0.0}°", X = pp.FoundX, Y = pp.FoundY - templ.H / 2.0 - 4, FontSize = 12f, Align = ViOverlayAlign.BottomCenter, Color = ViOverlayColor.Cyan });
            // 옮겨진 탐색 기하 — 점선. 티칭 도형(노랑)은 제자리에 있고, 실제로 캘리퍼가 선 자리는 이쪽이다.
            overlay.Add(new ViOverlaySeg { X1 = sx, Y1 = sy, X2 = ex, Y2 = ey, IsDashed = true, Color = ViOverlayColor.Cyan });
            overlay.Add(new ViOverlayRect { CenterX = blobOpt.SearchX + blobOpt.SearchW / 2, CenterY = blobOpt.SearchY + blobOpt.SearchH / 2, Width = blobOpt.SearchW, Height = blobOpt.SearchH, IsDashed = true, Color = ViOverlayColor.Cyan });
        }
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
        for (var i = 0; i < blobs.Hits.Count; i++)
        {
            var b = blobs.Hits[i];
            if (i < blobs.Contours.Count)
                overlay.Add(new ViOverlayPoly { Points = blobs.Contours[i], IsClosed = true, Color = ViOverlayColor.Orange });
            overlay.Add(new ViOverlaySeg { X1 = b.X - 5, Y1 = b.Y - 5, X2 = b.X + 5, Y2 = b.Y + 5, Color = ViOverlayColor.Orange });
            overlay.Add(new ViOverlaySeg { X1 = b.X - 5, Y1 = b.Y + 5, X2 = b.X + 5, Y2 = b.Y - 5, Color = ViOverlayColor.Orange });
            overlay.Add(new ViOverlayLabel { Text = $"A {b.Area:F0}", X = b.X, Y = b.Y + 12, FontSize = 12f, Color = ViOverlayColor.Orange });
        }

        var ok = line is not null && circle is not null && blobs.Hits.Count > 0 && (!Pattern.Trained || pose is not null);
        overlay.AddSummary(ok, ok ? "OK" : "NG",
            !Pattern.Trained ? "pattern  not trained — press Train" : pose is { } pq ? $"pattern  score {score:F2}  angle {pq.ThetaDeg,6:F2}°  at ({pq.FoundX:F1}, {pq.FoundY:F1})" : $"pattern  not found (best {score:F2})",
            line is { } lf ? $"line  angle {lf.AngleDeg,6:F2}°  rms {lf.RmsPx:F2} px  pts {lf.PointCount}" : "line  not found",
            circle is { } cf ? $"circle  center ({cf.CenterX:F1}, {cf.CenterY:F1})  r {cf.Radius:F1} px  rms {cf.RmsPx:F2} px" : "circle  not found",
            blobs.Hits.Count > 0 ? $"blob  n {blobs.Hits.Count}  area {blobs.TotalArea:F0} px²  threshold {blobs.ThresholdUsed:F0}" : "blob  none");
        return new DemoResult(ok, pose, score, line, circle, blobs, overlay);
    }

    /// <summary>블랍 옵션 사본 — 탐색 사각만 포즈로 옮긴 것. 티칭 값(원본 Blob)은 건드리지 않는다.
    /// 회전한 사각을 축 정렬로 되돌리는 방법이 둘인데 외접 사각은 판 밖(어두운 배경)을 끌어들여 배경 모서리가 블랍으로
    /// 잡힌다. 그래서 같은 중심에서 회전 사각 안에 들어가는 최대 축 정렬 사각을 쓴다 — 반폭 (a·cosθ − b·sinθ, b·cosθ − a·sinθ).
    /// 부품이 크게 돌면 이 사각이 작아진다: 회전을 완전히 따라가려면 블랍 툴에도 회전 영역이 있어야 한다는 뜻이다.</summary>
    private CvBlobOpt MovedBlobOpt(CvPose? pose)
    {
        var copy = new CvBlobOpt
        {
            Polarity = Blob.Polarity,
            MinArea = Blob.MinArea,
            FillHoles = Blob.FillHoles,
            UseOtsu = Blob.UseOtsu,
            Threshold = Blob.Threshold,
            UseSearchRegion = Blob.UseSearchRegion,
            SearchX = Blob.SearchX,
            SearchY = Blob.SearchY,
            SearchW = Blob.SearchW,
            SearchH = Blob.SearchH,
        };
        if (pose is not { } p) return copy;

        var (cx, cy) = CvInspGeom.XformByPose(Pattern, p, Blob.SearchX + Blob.SearchW / 2, Blob.SearchY + Blob.SearchH / 2);
        var th = Math.Abs((p.ThetaDeg - Pattern.TrainedAngleDeg) * Math.PI / 180.0);
        var c = Math.Cos(th);
        var s = Math.Sin(th);
        var a = Blob.SearchW / 2 * p.Scale;
        var b = Blob.SearchH / 2 * p.Scale;
        var u = Math.Max(8, a * c - b * s);
        var v = Math.Max(8, b * c - a * s);
        copy.SearchX = cx - u;
        copy.SearchY = cy - v;
        copy.SearchW = 2 * u;
        copy.SearchH = 2 * v;
        return copy;
    }

    /// <summary>툴은 8비트 1채널을 본다 — 불러온 컬러 파일은 여기서 접는다. 반환은 항상 새 Mat(호출자가 dispose).</summary>
    private static Mat ToGray(Mat frame) => frame.Channels() == 1 ? frame.Clone() : frame.CvtColor(ColorConversionCodes.BGR2GRAY);
}
