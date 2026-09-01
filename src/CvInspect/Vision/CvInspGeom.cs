using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

/// <summary>
/// 검사 공통 순수 기하 — 각도 정규화 · 포즈 강체 변환 · 템플릿 매칭 · 랜드마크 국소 검증 · 극좌표 언랩.
/// 검사들이 공유하는 수치 산출 경로 — 각 검사 파일에는 그 검사 고유 로직만 남긴다.
/// </summary>
public static class CvInspGeom
{
    public static double Mod360(double deg) => ((deg % 360) + 360) % 360;

    public static double Wrap180(double deg)
    {
        var d = Mod360(deg);
        return d > 180 ? d - 360 : d;
    }

    /// <summary>티칭 좌표 → 런타임 좌표 유사(similarity) 변환 — 패턴 학습 원점 기준 회전(dθ)·스케일 후
    /// 발견 위치로 평행이동. 부품이 발견 스케일만큼 크게/작게 보이면 티칭 오프셋도 같은 비율로 늘어야
    /// 추종 영역이 제자리에 붙는다. 스케일 탐색을 안 쓰면 p.Scale=1.0 이라 종전 강체 변환과 동일.</summary>
    public static (double X, double Y) XformByPose(CvPatternOpt pat, CvPose p, double x, double y)
    {
        var dTheta = (p.ThetaDeg - pat.TrainedAngleDeg) * Math.PI / 180.0;
        var cos = Math.Cos(dTheta);
        var sin = Math.Sin(dTheta);
        var dx = x - pat.TrainedOriginX;
        var dy = y - pat.TrainedOriginY;
        return (p.FoundX + (dx * cos - dy * sin) * p.Scale, p.FoundY + (dx * sin + dy * cos) * p.Scale);
    }

    /// <summary>패턴 템플릿 매칭 — 발견 여부/스코어/포즈/템플릿 크기. 입력 공간은 호출측 소관(축소/에지/언랩).
    /// 스코어는 미발견이어도 최고 후보 값 (진단·크롭 파일명 소비용).</summary>
    public static (bool Present, double Score, CvPose? Pose, (int W, int H) Templ) MatchPattern(Mat img, CvPatternOpt pat)
    {
        using var template = Cv2.ImDecode(pat.TemplatePng!, ImreadModes.Grayscale);
        if (template.Empty()) return (false, 0, null, (0, 0));

        var found = CvPatternFinder.Match(img, template, pat);
        var score = found?.Score ?? 0;
        return (found is not null && score >= pat.AcceptScore, score, found, (template.Cols, template.Rows));
    }

    /// <summary>
    /// 패턴 템플릿 다중 매칭 — <see cref="MatchPattern"/> 과 같은 계약에 스코어 내림차순 검출 목록(All)을 더한 것.
    /// 라이브러리의 다중 검출(<see cref="CvPatternOpt.MaxCount"/>)을 태운다. <b>Present/Pose 는 언제나 최고
    /// 스코어 하나</b> — 대상 하나를 보는 검사는 검출 수 설정으로 계약이 바뀌지 않고, 나머지 검출은 "비슷한
    /// 자리가 또 있다" 를 화면에 보여 주는 진단용(수락 스코어·탐색 영역을 어디에 둘지 판단할 근거)이다.
    /// All 은 요청 수보다 적을 수 있다(그만큼만 잡혔다는 뜻 — 정상).
    ///
    /// 다만 MaxCount 를 올리면 채택 자리가 바뀔 수 있다: 목록은 원본 해상도 정밀화까지 마친 스코어로 다시
    /// 정렬되므로 거친 단계 2등이 정밀화 후 1등이 되면 그쪽이 채택된다. 더 높은 스코어를 고르는 방향이라
    /// 개악은 아니지만 값이 달라지는 변화다. MaxCount=1(기본)이면 추가 스윕 자체를 건너뛰어 종전과 같다.
    /// </summary>
    public static (bool Present, double Score, CvPose? Pose, IReadOnlyList<CvPose> All, (int W, int H) Templ)
        MatchPatternAll(Mat img, CvPatternOpt pat)
    {
        using var template = Cv2.ImDecode(pat.TemplatePng!, ImreadModes.Grayscale);
        if (template.Empty()) return (false, 0, null, [], (0, 0));

        var all = CvPatternFinder.MatchAll(img, template, pat);
        var best = all.Count > 0 ? all[0] : (CvPose?)null;
        var score = best?.Score ?? 0;
        return (best is not null && score >= pat.AcceptScore, score, best, all, (template.Cols, template.Rows));
    }

    /// <summary>
    /// 정규화 국소 검증 — 발견 포즈(+extraDeg 후보 가산)로 역회전한 이미지에서 랜드마크 템플릿이
    /// 티칭 상대 오프셋 위치에 있는지 국소 매칭(NCC 최대값). 준대칭 후보 판별과
    /// 미러(대칭 반전) 검출에 공용 — 미러/오포즈 제품은 랜드마크가 기대 위치에 없어 스코어가 폭락한다.
    ///
    /// 뒤지는 범위는 두 갈래다. 랜드마크가 <see cref="CvPatternOpt.UseSearchRegion"/> 을 켜 두면
    /// 티칭한 탐색 사각을 포즈로 따라 옮겨 그 안을 뒤지고, 안 켜면 기대 위치 ±<paramref name="searchPx"/>
    /// 정사각 창을 뒤진다. 대칭으로 자리가 갈리는 특징은 사각을 길쭉하게 잡아 두 자리를 한 번에
    /// 덮는 편이 낫다 — 반경으로 같은 폭을 얻으려면 원이 훨씬 넓어져 엉뚱한 곳에 맞을 여지가 는다.
    ///
    /// 반환 기대 좌표·검색된 템플릿 사각(매치 위치의 템플릿 박스)은 pre 공간 환산치(표시용) —
    /// 정규화/펴기 회전의 역변환 체인 적용.
    /// </summary>
    public static (double Score, double ExpX, double ExpY, (double X, double Y)[] Found) VerifyLandmark(
        Mat pre, CvPatternOpt pat, CvPose p, double extraDeg, CvPatternOpt landmark, double searchPx)
    {
        using var templ = Cv2.ImDecode(landmark.TemplatePng!, ImreadModes.Grayscale);
        if (templ.Empty()) return (0, p.FoundX, p.FoundY, []);

        // 발견 포즈(+후보 가산각) 역회전 — 정규화 공간에서는 모든 티칭 특징이 티칭 상대 오프셋 그대로.
        var dTheta = p.ThetaDeg + extraDeg - pat.TrainedAngleDeg;
        using var norm = new Mat();
        using (var m = Cv2.GetRotationMatrix2D(new Point2f((float)p.FoundX, (float)p.FoundY), dTheta, 1.0))
            Cv2.WarpAffine(pre, norm, m, pre.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);

        var expX = p.FoundX + (landmark.TrainedOriginX - pat.TrainedOriginX);
        var expY = p.FoundY + (landmark.TrainedOriginY - pat.TrainedOriginY);

        // 회전 티칭 랜드마크 — 템플릿은 학습각을 편(unrotate) 내용으로 저장되므로 비교 창도 같은 각으로
        // 펴야 정합한다 (안 펴면 티칭 각도만큼 틀어져 스코어 폭락). 펴는 회전의 모서리 잘림 방지로
        // 창 여유를 템플릿 대각선분만큼 확장.
        var rotated = Math.Abs(landmark.TrainedAngleDeg) > 1e-6;
        var rotPad = rotated
            ? (int)Math.Ceiling((Math.Sqrt((double)templ.Cols * templ.Cols + templ.Rows * templ.Rows)
                                 - Math.Min(templ.Cols, templ.Rows)) / 2.0) + 2
            : 0;

        Rect? roi;
        if (landmark.UseSearchRegion)
        {
            // 티칭한 탐색 사각을 정규화 공간으로 옮긴다 — 학습 원점 대비 상대 위치가 그대로 유지되므로
            // 패턴 원점만큼 빼고 발견 위치를 더하면 된다.
            var scx = p.FoundX + (landmark.SearchX + landmark.SearchW / 2.0 - pat.TrainedOriginX);
            var scy = p.FoundY + (landmark.SearchY + landmark.SearchH / 2.0 - pat.TrainedOriginY);

            // 탐색 사각은 학습 영역의 회전을 함께 받는다. 잘라 오는 것은 축 정렬 상자라, 돌아간
            // 사각을 감싸도록 폭·높이를 키운다 — 창은 바로 아래에서 학습각만큼 다시 펴진다.
            var radS = landmark.TrainedAngleDeg * Math.PI / 180.0;
            var absCos = Math.Abs(Math.Cos(radS));
            var absSin = Math.Abs(Math.Sin(radS));
            var boundW = landmark.SearchW * absCos + landmark.SearchH * absSin;
            var boundH = landmark.SearchW * absSin + landmark.SearchH * absCos;

            // 템플릿보다 작으면 매칭이 성립하지 않아 최소한 템플릿 크기는 확보한다.
            var w = Math.Max(boundW, templ.Cols + rotPad * 2.0);
            var h = Math.Max(boundH, templ.Rows + rotPad * 2.0);
            roi = CvImageOps.ClipRect(scx - w / 2.0, scy - h / 2.0, w, h, norm.Cols, norm.Rows);
        }
        else
        {
            var margin = (int)Math.Max(8, searchPx) + rotPad;
            roi = CvImageOps.ClipRect(
                expX - templ.Cols / 2.0 - margin, expY - templ.Rows / 2.0 - margin,
                templ.Cols + margin * 2.0, templ.Rows + margin * 2.0, norm.Cols, norm.Rows);
        }

        if (roi is null || roi.Value.Width < templ.Cols || roi.Value.Height < templ.Rows)
            return (0, expX, expY, []);

        using var window = norm[roi.Value];
        Mat cmp = window;
        Mat? unrot = null;
        if (rotated)
        {
            unrot = new Mat();
            var rcx = expX - roi.Value.X;   // 기대점의 창 내 좌표 — 회전 중심 (기대 위치 불변 유지)
            var rcy = expY - roi.Value.Y;
            using var wm = Cv2.GetRotationMatrix2D(new Point2f((float)rcx, (float)rcy), landmark.TrainedAngleDeg, 1.0);
            Cv2.WarpAffine(window, unrot, wm, window.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);
            cmp = unrot;
        }

        double maxVal;
        Point maxLoc;
        try
        {
            using var result = new Mat();
            Cv2.MatchTemplate(cmp, templ, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out maxVal, out _, out maxLoc);
        }
        finally
        {
            unrot?.Dispose();
        }

        // 표시용 역변환 — 기대점은 norm→pre(발견 위치 중심 +dθ 회전), 검색된 템플릿 박스는
        // cmp(펴진 창)→창(+τ랜드마크 재회전, 기대점 중심)→norm(roi 오프셋)→pre 체인.
        var rad = dTheta * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var radL = (rotated ? landmark.TrainedAngleDeg : 0) * Math.PI / 180.0;
        var cosL = Math.Cos(radL);
        var sinL = Math.Sin(radL);
        var rcx0 = expX - roi.Value.X;
        var rcy0 = expY - roi.Value.Y;

        (double X, double Y) CmpToPre(double x, double y)
        {
            var wx = rcx0 + (x - rcx0) * cosL - (y - rcy0) * sinL;
            var wy = rcy0 + (x - rcx0) * sinL + (y - rcy0) * cosL;
            var dx = roi.Value.X + wx - p.FoundX;
            var dy = roi.Value.Y + wy - p.FoundY;
            return (p.FoundX + dx * cos - dy * sin, p.FoundY + dx * sin + dy * cos);
        }

        var found = new[]
        {
            CmpToPre(maxLoc.X, maxLoc.Y),
            CmpToPre(maxLoc.X + templ.Cols, maxLoc.Y),
            CmpToPre(maxLoc.X + templ.Cols, maxLoc.Y + templ.Rows),
            CmpToPre(maxLoc.X, maxLoc.Y + templ.Rows),
        };
        var ddx = expX - p.FoundX;
        var ddy = expY - p.FoundY;
        return (maxVal, p.FoundX + ddx * cos - ddy * sin, p.FoundY + ddx * sin + ddy * cos, found);
    }

}
