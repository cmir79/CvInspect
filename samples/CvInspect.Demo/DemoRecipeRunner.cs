using CvInspect.Vision;
using CvInspect.Vision.Opts;
using CvInspect.Vision.Overlay;
using OpenCvSharp;

namespace CvInspect.Demo;

/// <summary>툴 하나의 실행 결과 — 판정, 한 줄 요약, 그 툴의 단계 공간에 그린 그래픽, 그 단계 → 원본 환산.</summary>
public sealed record DemoToolResult(DemoToolEntry Tool, bool Ok, string Summary, ViOverlay Graphic, CvSpaceMap Map);

/// <summary>
/// 한 번의 레시피 실행 결과. 단계 이미지(툴 i 의 입력)는 전처리가 만든 것만 새 Mat 이고 나머지는 앞 단계를 그대로 가리킨다 —
/// 이 객체가 그것들을 소유하므로 다음 실행으로 바꿀 때 dispose 한다. 표시는 <see cref="OverlayFor"/> 로 원하는 단계 공간에 모은다.
/// </summary>
public sealed class DemoRunResult : IDisposable
{
    private readonly List<Mat> _owned;
    private readonly List<Mat> _stages;
    private readonly List<CvSpaceMap> _maps;

    internal DemoRunResult(Mat original, List<DemoToolResult> tools, List<Mat> stages, List<CvSpaceMap> maps, List<Mat> owned)
    {
        Original = original;
        Tools = tools;
        _stages = stages;
        _maps = maps;
        _owned = owned;
    }

    public Mat Original { get; }
    public IReadOnlyList<DemoToolResult> Tools { get; }
    public bool IsOk => Tools.All(t => t.Ok);

    /// <summary>툴 index 의 입력 이미지 — 소유는 이 객체. index 가 범위 밖이면 원본.</summary>
    public Mat StageOf(int index) => index >= 0 && index < _stages.Count ? _stages[index] : Original;
    public CvSpaceMap MapOf(int index) => index >= 0 && index < _maps.Count ? _maps[index] : CvSpaceMap.Identity;
    public bool IsOriginalStage(int index) => ReferenceEquals(StageOf(index), Original);

    /// <summary>모든 툴의 그래픽을 index 툴의 단계 공간으로 옮겨 모은 오버레이 + 요약 HUD. 그 단계 이미지 위에 얹는다.</summary>
    public ViOverlay OverlayFor(int index)
    {
        var display = MapOf(index);
        var overlay = new ViOverlay();
        foreach (var t in Tools)
        {
            // 툴 공간 → 원본 → 표시 공간. 배율은 회전 사각의 폭·높이에만 쓴다(비등방 축소는 근사).
            var scale = t.Map.Sx / display.Sx;
            foreach (var item in t.Graphic.Items)
                overlay.Add(Remap(item, (x, y) => Inv(display, t.Map.Apply(x, y)), scale));
        }
        overlay.AddSummary(IsOk, IsOk ? "OK" : "NG", Tools.Select(t => $"{t.Tool.Key}  {t.Summary}").ToArray());
        return overlay;
    }

    private static (double X, double Y) Inv(CvSpaceMap m, (double X, double Y) p) => ((p.X - m.Ox) / m.Sx, (p.Y - m.Oy) / m.Sy);

    private static ViOverlayItem Remap(ViOverlayItem item, Func<double, double, (double X, double Y)> f, double scale)
    {
        switch (item)
        {
            case ViOverlaySeg s:
            {
                var a = f(s.X1, s.Y1);
                var b = f(s.X2, s.Y2);
                return new ViOverlaySeg { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, HasEndArrow = s.HasEndArrow, Color = s.Color, IsDashed = s.IsDashed };
            }
            case ViOverlayLabel l:
            {
                var p = f(l.X, l.Y);
                return new ViOverlayLabel { Text = l.Text, X = p.X, Y = p.Y, FontSize = l.FontSize, Align = l.Align, HasBackground = l.HasBackground, LineColors = l.LineColors, Color = l.Color, IsDashed = l.IsDashed };
            }
            case ViOverlayRect r:
            {
                var c = f(r.CenterX, r.CenterY);
                return new ViOverlayRect { CenterX = c.X, CenterY = c.Y, Width = r.Width * scale, Height = r.Height * scale, AngleDeg = r.AngleDeg, Color = r.Color, IsDashed = r.IsDashed };
            }
            case ViOverlayPoly p:
                return new ViOverlayPoly { Points = p.Points.Select(q => f(q.X, q.Y)).ToList(), IsClosed = p.IsClosed, Color = p.Color, IsDashed = p.IsDashed };
            default:
                return item;
        }
    }

    public void Dispose()
    {
        foreach (var m in _owned) m.Dispose();
        _owned.Clear();
    }
}

/// <summary>
/// 레시피 실행기 — 툴을 순서대로 돌린다. 전처리는 단계 이미지와 공간 환산을 바꾸고, 패턴은 이후 툴의 티칭 기하를
/// 발견 포즈로 옮기는 픽스처가 된다. 기하는 각 툴의 <b>자기 단계 공간</b>에 적혀 있으므로, 픽스처 적용은
/// 툴 공간 → 원본 → 패턴 단계 공간 → 포즈 → 원본 → 툴 공간 순으로 환산한다(단계 맵이 축소·자르기라 역환산이 닫힌다).
/// 순수 계산이라 같은 입력에 같은 결과가 나온다 — 테스트가 이 성질에 기댄다.
/// </summary>
public static class DemoRecipeRunner
{
    public static DemoRunResult Run(DemoRecipe recipe, Mat frame)
    {
        var gray0 = ToGray(frame);
        var owned = new List<Mat> { gray0 };
        var stage = gray0;
        var map = CvSpaceMap.Identity;
        CvPose? pose = null;
        CvPatternOpt? pat = null;
        var mapP = CvSpaceMap.Identity;
        var results = new List<DemoToolResult>();
        var stages = new List<Mat>();
        var maps = new List<CvSpaceMap>();

        foreach (var tool in recipe.Tools)
        {
            stages.Add(stage);
            maps.Add(map);
            var mapT = map;
            var g = new ViOverlay();
            bool ok;
            string summary;

            // 픽스처 적용 — 이 툴 공간의 점을 발견된 부품 자리로.
            (double X, double Y) F(double x, double y)
            {
                if (pose is not { } p || pat is null) return (x, y);
                var o = mapT.Apply(x, y);
                var q = CvInspGeom.XformByPose(pat, p, (o.X - mapP.Ox) / mapP.Sx, (o.Y - mapP.Oy) / mapP.Sy);
                var o2 = mapP.Apply(q.X, q.Y);
                return ((o2.X - mapT.Ox) / mapT.Sx, (o2.Y - mapT.Oy) / mapT.Sy);
            }

            switch (tool.Kind)
            {
                case DemoToolKind.Preprocess:
                {
                    var opt = (CvImageProcessOpt)tool.Opt;
                    var pre = CvImageOps.Preprocess(stage, opt);
                    var local = CvImageOps.MapOf(stage, pre, opt);   // pre → stage
                    map = new CvSpaceMap(map.Sx * local.Sx, map.Sy * local.Sy, map.Ox + map.Sx * local.Ox, map.Oy + map.Sy * local.Oy);
                    stage = pre;
                    owned.Add(pre);
                    ok = true;
                    summary = $"{pre.Cols}x{pre.Rows}  scale {local.Sx:F2}x{local.Sy:F2}" + (opt.UseCrop ? $"  crop@({local.Ox:F0},{local.Oy:F0})" : "");
                    break;
                }
                case DemoToolKind.Pattern:
                {
                    var opt = (CvPatternOpt)tool.Opt;
                    if (!opt.Trained || opt.TemplatePng is null)
                    {
                        ok = false;
                        summary = "not trained — press Train";
                        break;
                    }
                    var m = CvInspGeom.MatchPattern(stage, opt);
                    if (m.Present && m.Pose is { } found)
                    {
                        pose = found;
                        pat = opt;
                        mapP = map;
                        g.Add(new ViOverlayRect { CenterX = found.FoundX, CenterY = found.FoundY, Width = m.Templ.W, Height = m.Templ.H, AngleDeg = found.ThetaDeg, Color = ViOverlayColor.Cyan });
                        g.Add(new ViOverlayLabel { Text = $"{m.Score:F2}  {found.ThetaDeg:+0.0;-0.0}°", X = found.FoundX, Y = found.FoundY - m.Templ.H / 2.0 - 4, FontSize = 12f, Align = ViOverlayAlign.BottomCenter, Color = ViOverlayColor.Cyan });
                        ok = true;
                        summary = $"score {m.Score:F2}  angle {found.ThetaDeg,6:F2}°  at ({found.FoundX:F1}, {found.FoundY:F1})";
                    }
                    else
                    {
                        ok = false;
                        summary = $"not found (best {m.Score:F2})";
                    }
                    break;
                }
                case DemoToolKind.Line:
                {
                    var opt = (CvFindLineOpt)tool.Opt;
                    var (sx, sy) = F(opt.StartX, opt.StartY);
                    var (ex, ey) = F(opt.EndX, opt.EndY);
                    if (pose is not null) g.Add(new ViOverlaySeg { X1 = sx, Y1 = sy, X2 = ex, Y2 = ey, IsDashed = true, Color = ViOverlayColor.Cyan });
                    var line = CvLineFinder.FindWithRefit(stage, sx, sy, ex, ey, opt);
                    if (line is { } l)
                    {
                        g.Add(new ViOverlaySeg { X1 = l.StartX, Y1 = l.StartY, X2 = l.EndX, Y2 = l.EndY, HasEndArrow = true, Color = ViOverlayColor.Green });
                        ViDraw.AddFullLine(g, (l.StartX + l.EndX) / 2, (l.StartY + l.EndY) / 2, l.AngleDeg, stage.Width, stage.Height, ViOverlayColor.Teal);
                        ok = true;
                        summary = $"angle {l.AngleDeg,6:F2}°  rms {l.RmsPx:F2} px  pts {l.PointCount}";
                    }
                    else
                    {
                        ok = false;
                        summary = "not found";
                    }
                    break;
                }
                case DemoToolKind.Circle:
                {
                    var opt = (CvFindCircleOpt)tool.Opt;
                    var (cx, cy) = F(opt.CenterX, opt.CenterY);
                    var circle = CvCircleFinder.FindWithRefit(stage, cx, cy, opt);
                    if (circle is { } c)
                    {
                        var pts = new List<(double X, double Y)>(48);
                        for (var i = 0; i < 48; i++)
                        {
                            var a = i * Math.PI * 2 / 48;
                            pts.Add((c.CenterX + c.Radius * Math.Cos(a), c.CenterY + c.Radius * Math.Sin(a)));
                        }
                        g.Add(new ViOverlayPoly { Points = pts, IsClosed = true, Color = ViOverlayColor.Green });
                        g.Add(new ViOverlaySeg { X1 = c.CenterX - 6, Y1 = c.CenterY, X2 = c.CenterX + 6, Y2 = c.CenterY, Color = ViOverlayColor.Green });
                        g.Add(new ViOverlaySeg { X1 = c.CenterX, Y1 = c.CenterY - 6, X2 = c.CenterX, Y2 = c.CenterY + 6, Color = ViOverlayColor.Green });
                        ok = true;
                        summary = $"center ({c.CenterX:F1}, {c.CenterY:F1})  r {c.Radius:F1} px  rms {c.RmsPx:F2} px";
                    }
                    else
                    {
                        ok = false;
                        summary = "not found";
                    }
                    break;
                }
                case DemoToolKind.Blob:
                {
                    var opt = (CvBlobOpt)tool.Opt;
                    var moved = MovedBlobOpt(opt, pose, pat, F);
                    if (pose is not null && moved.UseSearchRegion)
                        g.Add(new ViOverlayRect { CenterX = moved.SearchX + moved.SearchW / 2, CenterY = moved.SearchY + moved.SearchH / 2, Width = moved.SearchW, Height = moved.SearchH, IsDashed = true, Color = ViOverlayColor.Cyan });
                    var blobs = CvBlobFinder.FindAll(stage, moved, withContours: true);
                    for (var i = 0; i < blobs.Hits.Count; i++)
                    {
                        var b = blobs.Hits[i];
                        if (i < blobs.Contours.Count) g.Add(new ViOverlayPoly { Points = blobs.Contours[i], IsClosed = true, Color = ViOverlayColor.Orange });
                        g.Add(new ViOverlaySeg { X1 = b.X - 5, Y1 = b.Y - 5, X2 = b.X + 5, Y2 = b.Y + 5, Color = ViOverlayColor.Orange });
                        g.Add(new ViOverlaySeg { X1 = b.X - 5, Y1 = b.Y + 5, X2 = b.X + 5, Y2 = b.Y - 5, Color = ViOverlayColor.Orange });
                        g.Add(new ViOverlayLabel { Text = $"A {b.Area:F0}", X = b.X, Y = b.Y + 12, FontSize = 12f, Color = ViOverlayColor.Orange });
                    }
                    ok = blobs.Hits.Count > 0;
                    summary = ok ? $"n {blobs.Hits.Count}  area {blobs.TotalArea:F0} px²  threshold {blobs.ThresholdUsed:F0}" : "none";
                    break;
                }
                default:
                    ok = false;
                    summary = "unknown tool";
                    break;
            }
            results.Add(new DemoToolResult(tool, ok, summary, g, mapT));
        }
        return new DemoRunResult(gray0, results, stages, maps, owned);
    }

    /// <summary>패턴 툴 학습 — 그 툴의 단계 입력(앞선 전처리를 적용한 이미지)에서 학습 영역을 잘라 템플릿으로 삼는다.
    /// 영역이 이미지 밖이거나 특징이 없으면 false. 학습 영역의 회전은 지원하지 않는다(부품을 돌리는 것이지 상자를 돌리는 것이 아니다).</summary>
    public static bool Train(DemoRecipe recipe, DemoToolEntry patternTool, Mat frame)
    {
        if (patternTool.Opt is not CvPatternOpt opt) return false;
        if (Math.Abs(opt.TrainAngleDeg) > 1e-9) return false;
        var index = recipe.Tools.IndexOf(patternTool);
        if (index < 0) return false;

        using var gray0 = ToGray(frame);
        var owned = new List<Mat>();
        try
        {
            var stage = StageInputFor(recipe, gray0, index, owned);
            using var template = CvPatternTeach.CropRect(stage, opt.TrainX, opt.TrainY, opt.TrainW, opt.TrainH, out var ox, out var oy);
            if (template is null || !CvPatternTeach.HasFeature(template, opt.TrainShape)) return false;
            Cv2.ImEncode(".png", template, out var png);
            opt.TemplatePng = png;
            opt.TrainedOriginX = ox;
            opt.TrainedOriginY = oy;
            opt.TrainedAngleDeg = 0;
            opt.TrainedShape = opt.TrainShape;
            opt.Trained = true;
            return true;
        }
        finally
        {
            foreach (var m in owned) m.Dispose();
        }
    }

    /// <summary>편집기의 Train 버튼을 각 패턴 툴에 배선 — 호스트가 현재 프레임을 준다. 로드한 레시피에도 다시 걸어야 한다.</summary>
    public static void WireTrainHooks(DemoRecipe recipe, Func<Mat?> frameProvider)
    {
        foreach (var t in recipe.Tools)
            if (t.Opt is CvPatternOpt opt)
                opt.TrainHook = () =>
                {
                    using var frame = frameProvider();
                    return frame is not null && Train(recipe, t, frame);
                };
    }

    /// <summary>툴 index 의 입력 이미지 — 앞선 전처리 툴만 순서대로 적용한다. 만든 Mat 은 owned 에 담는다.</summary>
    private static Mat StageInputFor(DemoRecipe recipe, Mat gray0, int index, List<Mat> owned)
    {
        var stage = gray0;
        for (var i = 0; i < index; i++)
        {
            if (recipe.Tools[i].Opt is not CvImageProcessOpt opt) continue;
            var pre = CvImageOps.Preprocess(stage, opt);
            owned.Add(pre);
            stage = pre;
        }
        return stage;
    }

    /// <summary>블랍 옵션 사본 — 탐색 사각만 포즈로 옮긴 것. 회전한 사각을 축 정렬로 되돌릴 때 외접 사각은 판 밖(배경)을
    /// 끌어들이므로, 같은 중심에서 회전 사각 안에 들어가는 최대 축 정렬 사각(반폭 a·cosθ − b·sinθ, b·cosθ − a·sinθ)을 쓴다.
    /// 부품이 크게 돌면 이 사각이 작아진다 — 회전을 온전히 따라가려면 블랍 툴에도 회전 영역이 있어야 한다는 뜻이다.</summary>
    private static CvBlobOpt MovedBlobOpt(CvBlobOpt src, CvPose? pose, CvPatternOpt? pat, Func<double, double, (double X, double Y)> f)
    {
        var copy = new CvBlobOpt
        {
            Polarity = src.Polarity,
            MinArea = src.MinArea,
            FillHoles = src.FillHoles,
            UseOtsu = src.UseOtsu,
            Threshold = src.Threshold,
            UseSearchRegion = src.UseSearchRegion,
            SearchX = src.SearchX,
            SearchY = src.SearchY,
            SearchW = src.SearchW,
            SearchH = src.SearchH,
        };
        if (pose is not { } p || pat is null || !src.UseSearchRegion) return copy;

        var (cx, cy) = f(src.SearchX + src.SearchW / 2, src.SearchY + src.SearchH / 2);
        var th = Math.Abs((p.ThetaDeg - pat.TrainedAngleDeg) * Math.PI / 180.0);
        var c = Math.Cos(th);
        var s = Math.Sin(th);
        var a = src.SearchW / 2 * p.Scale;
        var b = src.SearchH / 2 * p.Scale;
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
