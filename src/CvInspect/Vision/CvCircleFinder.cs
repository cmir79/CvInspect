using System.ComponentModel;
using System.Text.Json.Serialization;
using OpenCvSharp;
using CvInspect.Vision.Opts;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Vision;

/// <summary>
/// 원 검출 파라미터 — 기대 원(중심·반경·원호 범위)을 따라 캘리퍼를 방사형 배치.
/// 프로파일 축 = 반경 바깥(+s) 방향 — 극성 기준 (DarkToLight = 안→밖 어두움→밝음).
/// 좌표는 대상 이미지 픽셀 공간.
/// </summary>
public sealed class CvFindCircleOpt : ICvShapeSource
{
    // 기대 원 — 디스플레이 도형(중심/반경/시작각/스팬 그립) 드래그로 편집 (PG 미노출 — 정적 스냅샷이라 실시간 미반영).
    [Browsable(false)] public double CenterX { get; set; } = 100;
    [Browsable(false)] public double CenterY { get; set; } = 100;
    [Browsable(false)] public double Radius { get; set; } = 50;
    [Browsable(false)] public double AngleStartDeg { get; set; }
    [Browsable(false)] public double AngleSpanDeg { get; set; } = 360;

    /// <summary>코드 경로 편집(ToggleSpan 등) → 편집 도형 재동기화 훅 — 표시 계층의 도형 바인더가 배선.
    /// 도형은 바인딩 시점 스냅샷이라 Opt 직접 변경은 이 훅 없이는 화면에 반영되지 않는다.</summary>
    [Browsable(false)]
    [JsonIgnore]
    public Action? EditShapeSync { get; set; }

    [CvCategory("cv:CatCircle", 1)]
    [CvName("cv:ToggleSpan")]
    [CvDesc("cv:ToggleSpanDesc")]
    [JsonIgnore]
    public Action ToggleSpan
    {
        get => () =>
        {
            // 예상 원호 스팬 180° ↔ 360° 전환 — 현재 스팬 270° 초과면 360° 상태로 보고 180° 로,
            // 이하면 360° 로. 전환 시 시작각은 0 으로 초기화 (전체 원/반원 기준 자세 통일).
            var isFull = Math.Abs(AngleSpanDeg) > 270;
            AngleSpanDeg = isFull ? 180 : 360;
            AngleStartDeg = 0;
            EditShapeSync?.Invoke();   // 편집 도형 재동기화 — 없으면 내부 수치만 바뀌고 화면 미갱신
        };
    }

    [CvCategory("cv:CatCaliper", 2)]
    [CvName("cv:NumCalipers")]
    [CvDesc("cv:NumCalipersDesc")]
    public int NumCalipers { get; set; } = 12;

    [CvCategory("cv:CatCaliper", 2)]
    [CvName("cv:SearchLength")]
    [CvDesc("cv:RadialSearchLengthDesc")]
    public int SearchLength { get; set; } = 40;

    [CvCategory("cv:CatCaliper", 2)]
    [CvName("cv:ProjectionLength")]
    [CvDesc("cv:TangentProjectionLengthDesc")]
    public int ProjectionLength { get; set; } = 5;

    [CvCategory("cv:CatEdge", 3)]
    [CvName("cv:Polarity")]
    [CvDesc("cv:RadialPolarityDesc")]
    public CvEdgePolarity Polarity { get; set; } = CvEdgePolarity.Either;

    [CvCategory("cv:CatEdge", 3)]
    [CvName("cv:ContrastThreshold")]
    [CvDesc("cv:ContrastThresholdDesc")]
    public double ContrastThreshold { get; set; } = 10;

    [CvCategory("cv:CatEdge", 3)]
    [CvName("cv:EdgeSelect")]
    [CvDesc("cv:RadialEdgeSelectDesc")]
    public CvEdgeSelect EdgeSelect { get; set; } = CvEdgeSelect.Best;

    [CvCategory("cv:CatFit", 4)]
    [CvName("cv:NumToIgnore")]
    [CvDesc("cv:CircleNumToIgnoreDesc")]
    public int NumToIgnore { get; set; }

    [CvCategory("cv:CatFit", 4)]
    [CvName("cv:UseRefit")]
    [CvDesc("cv:UseRefitDesc")]
    public bool UseRefit { get; set; }

    [CvCategory("cv:CatQuality", 5)]
    [CvName("cv:UseRmsGate")]
    [CvDesc("cv:UseRmsGateDesc")]
    public bool UseRmsGate { get; set; }

    [CvCategory("cv:CatQuality", 5)]
    [CvName("cv:MaxRmsPx")]
    [CvDesc("cv:CircleMaxRmsPxDesc")]
    public double MaxRmsPx { get; set; } = 2.0;

    /// <summary>편집 도형 — 기대 원호(중심·반경·시작각·스팬). 코드 경로로 값을 고쳤을 때(스팬 전환 버튼 등)는
    /// <see cref="EditShapeSync"/> 가 도형을 다시 맞춘다 — Set 이 Changed 를 되쏴 되쓰기와 화면 갱신까지 이어진다.</summary>
    public IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited)
    {
        var arc = new CvEditArc { Color = ViOverlayColor.Yellow, Label = "Circle" };
        arc.Set(CenterX, CenterY, Radius, AngleStartDeg, AngleSpanDeg);
        arc.Changed += (_, _) =>
        {
            CenterX = arc.CenterX;
            CenterY = arc.CenterY;
            Radius = arc.Radius;
            AngleStartDeg = arc.StartDeg;
            AngleSpanDeg = arc.SpanDeg;
            onEdited();
        };
        EditShapeSync = () => arc.Set(CenterX, CenterY, Radius, AngleStartDeg, AngleSpanDeg);
        return [arc];
    }
}

/// <summary>원 피팅 결과 — 중심/반경(입력 이미지 공간)과 사용 점수.
/// <paramref name="RmsPx"/> 는 아웃라이어 제외 후 최종 피팅의 반경 잔차
/// RMS(|중심거리 − R|, 입력 이미지 픽셀) — 피팅 품질 지표다. 검출점이 한 원에 잘 놓였으면 작고,
/// 캘리퍼가 다른 구조를 물거나 대상이 원이 아니면 커진다.</summary>
public readonly record struct CvCircleFit(double CenterX, double CenterY, double Radius, int PointCount, double RmsPx);

/// <summary>
/// 서클 파인더 — 기대 원호를 따라 캘리퍼(CvCaliper.DetectEdge)를 방사형 배열하고 검출점에 Kåsa 원 피팅.
/// 라인 파인더와 동일 골격: 배치 기하(원호·반경 방향)와 피팅(원)만 다르다.
/// </summary>
public static class CvCircleFinder
{
    /// <summary>티칭 좌표 그대로 검출.</summary>
    public static CvCircleFit? Find(Mat img, CvFindCircleOpt opt)
        => Find(img, opt.CenterX, opt.CenterY, opt.Radius, opt);

    /// <summary>
    /// 수렴 재피팅 2패스 — 캘리퍼 원 피팅 후 피팅 중심으로 재배치해 한 번 더 (라인 파인더
    /// FindWithRefit 와 동일 규약). 주입 중심이 틀어진 채 1차가 잡혀도(비스듬한 캘리퍼 왜곡)
    /// 2차에서 법선이 에지와 직교 정렬되며 수렴. 재피팅 실패 시 1차 결과 유지.
    /// 캘리퍼 배치 반경은 티칭 값 유지 — 위치 편차는 중심만 이동 (반경은 강체 불변).
    /// 대편차 위치 보정은 선행 선탐지(CvBlobFinder 등)로 중심을 주입하는 오버로드 사용.
    /// </summary>
    public static CvCircleFit? FindWithRefit(Mat img, CvFindCircleOpt opt)
        => FindWithRefit(img, opt.CenterX, opt.CenterY, opt);

    /// <summary>중심 주입 수렴 재피팅 — 선탐지(블랍 무게중심 등)로 끌어온 중심을 넣는 용도.</summary>
    public static CvCircleFit? FindWithRefit(Mat img, double centerX, double centerY, CvFindCircleOpt opt)
    {
        var fit = FindOnce(img, centerX, centerY, opt.Radius, opt);
        if (fit is null) return null;

        // 수렴 재피팅 — 1차 피팅 중심으로 캘리퍼 재배치. 실패 시 1차 결과 유지.
        return Gate(FindOnce(img, fit.Value.CenterX, fit.Value.CenterY, opt.Radius, opt) ?? fit, opt);
    }

    /// <summary>
    /// 품질 게이트 — 잔차가 임계를 넘으면 미검출로 돌린다. <b>최종 결과에만 한 번</b> 적용한다:
    /// 재피팅은 "1차가 어긋난 중심으로 배치돼 왜곡될 수 있다"는 전제로 있는 기능이라, 1차 잔차로
    /// 미리 자르면 스스로 고칠 기회를 뺏는다. 2차까지 가고도 잔차가 크면 그때는 값을 믿을 수 없다.
    /// </summary>
    private static CvCircleFit? Gate(CvCircleFit? fit, CvFindCircleOpt opt)
        => fit is { } f && opt.UseRmsGate && f.RmsPx > Math.Abs(opt.MaxRmsPx) ? null : fit;

    /// <summary>
    /// 중심/반경 주입 검출 — 패턴 포즈 추종 시 변환된 중심을 넣는 용도 (반경은 강체변환 불변).
    /// 에지점 부족(&lt;3) 또는 NumToIgnore 를 채울 수 없으면 null.
    /// </summary>
    public static CvCircleFit? Find(Mat img, double centerX, double centerY, double radius, CvFindCircleOpt opt)
    {
        var fit = FindOnce(img, centerX, centerY, radius, opt);
        if (fit is null) return null;

        // 옵션 재피팅 — FindWithRefit 과 같은 2패스를 티칭 설정으로 켜는 경로.
        // 2패스가 검출에 실패하면 1패스 결과를 유지한다(기존 규약).
        if (opt.UseRefit)
            fit = FindOnce(img, fit.Value.CenterX, fit.Value.CenterY, radius, opt) ?? fit;

        return Gate(fit, opt);
    }

    /// <summary>단일 패스 검출 — 재피팅 없이 주어진 중심/반경에서 한 번만 캘리퍼를 세운다.</summary>
    private static CvCircleFit? FindOnce(Mat img, double centerX, double centerY, double radius, CvFindCircleOpt opt)
    {
        if (radius < 3) return null;

        var count = Math.Max(3, opt.NumCalipers);
        var startRad = opt.AngleStartDeg * Math.PI / 180.0;
        var spanRad = opt.AngleSpanDeg * Math.PI / 180.0;

        var pts = new List<(double X, double Y)>();
        for (var i = 0; i < count; i++)
        {
            var theta = startRad + spanRad * (i + 0.5) / count;
            var radX = Math.Cos(theta);    // 프로파일(+s) 축 — 반경 바깥
            var radY = Math.Sin(theta);
            var tanX = -radY;              // 투영(평균) 축 — 접선
            var tanY = radX;

            var cx = centerX + radius * radX;
            var cy = centerY + radius * radY;

            var hit = CvCaliper.DetectEdge(img, cx, cy, radX, radY, tanX, tanY,
                opt.SearchLength, opt.ProjectionLength, opt.Polarity, opt.ContrastThreshold, opt.EdgeSelect);
            if (hit is null) continue;

            pts.Add((cx + radX * hit.Value.S, cy + radY * hit.Value.S));
        }

        if (pts.Count < 3) return null;
        // NumToIgnore 미충족 = 검출 실패 — 조용한 미적용이 노이즈 낀 중심/반경을 내보내는 것 방지 (라인 파인더와 동일 규약)
        if (opt.NumToIgnore > 0 && pts.Count - opt.NumToIgnore < 3) return null;

        var fit = CvFit.Circle(pts);
        if (fit is null) return null;

        // 아웃라이어 제외 후 재피팅 — 반경 잔차 |dist−r| 큰 점부터 NumToIgnore 개
        if (opt.NumToIgnore > 0)
        {
            var (fcx, fcy, fr) = fit.Value;
            pts = pts
                .OrderBy(p =>
                {
                    var dx = p.X - fcx;
                    var dy = p.Y - fcy;
                    return Math.Abs(Math.Sqrt(dx * dx + dy * dy) - fr);
                })
                .Take(pts.Count - opt.NumToIgnore)
                .ToList();
            fit = CvFit.Circle(pts);
            if (fit is null) return null;
        }

        // 피팅 품질 — 최종 점집합의 반경 잔차 RMS (라인 파인더와 같은 규약).
        var (rcx, rcy, rr) = fit.Value;
        double sqSum = 0;
        foreach (var (px, py) in pts)
        {
            var dx = px - rcx;
            var dy = py - rcy;
            var d = Math.Sqrt(dx * dx + dy * dy) - rr;
            sqSum += d * d;
        }
        var rms = Math.Sqrt(sqSum / pts.Count);
        return new CvCircleFit(rcx, rcy, rr, pts.Count, rms);
    }
}
