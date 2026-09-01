using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

/// <summary>
/// 극좌표 언랩 — 원형 부품의 회전을 가로 이동으로 바꾸는 공간과 그 공간의 기하.
/// 밴드 전개(<see cref="Band"/>), 각도 환산의 분모(<see cref="W360"/>), 원본 좌표 복원
/// (<see cref="ToSource"/>), 이음새 접기(<see cref="WrapPoseForLandmark"/>)가 한 체계다 —
/// 식이 갈리면 각도가 조용히 어긋나므로 여기 한 곳에만 둔다. 밴드·오버랩 값은
/// <see cref="CvUnwrapOpt"/> 가 갖는다.
/// </summary>
public static class CvUnwrapGeom
{
    /// <summary>환형 밴드 극좌표 가로 전개 + 이음새 오버랩 이어붙임 — 열=각도(0~360°+α), 행=반경(RMin 위~RMax 아래).
    /// WarpPolar 출력(행=각도)을 전치해 가로 띠로 — 티칭/패턴 편집이 가로 스크롤 감각과 일치.</summary>
    public static Mat? Band(Mat pre, double cx, double cy, int rMin, int rMax, int w360, int overlapCols)
    {
        try
        {
            using var polar = new Mat();
            Cv2.WarpPolar(pre, polar, new Size(rMax, w360),
                new Point2f((float)cx, (float)cy), rMax,
                InterpolationFlags.Linear, WarpPolarMode.Linear);

            // ColRange 는 dispose 필요한 서브매트릭스 뷰 — Transpose 가 뷰를 직접 읽으므로 Clone 불요.
            using var band = polar.ColRange(rMin, rMax);
            using var horizontal = new Mat();
            Cv2.Transpose(band, horizontal);

            if (overlapCols <= 0) return horizontal.Clone();

            var padded = new Mat();
            using var head = horizontal.ColRange(0, overlapCols);
            Cv2.HConcat(new[] { horizontal, head }, padded);
            return padded;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 언랩 360° 폭 — 밴드 중간 반경의 호 길이. 이 폭이 각도 환산의 분모라 언랩을 만드는 쪽과
    /// 각도를 읽는 쪽이 같은 식을 써야 한다. 밴드가 아주 좁아도 매칭이 성립할 폭은 남긴다.
    /// </summary>
    public static int W360(int rMin, int rMax) => Math.Max(64, (int)Math.Round(Math.PI * (rMin + rMax)));

    /// <summary>언랩 좌표(열=각도, 행=반경) → 언랩을 뜬 원본 이미지 공간의 점.
    /// 열은 +X축 0°에서 이미지 y-아래 방향(화면 시계방향)으로 돈다 — atan2 규약과 같다.</summary>
    public static (double X, double Y) ToSource(double ux, double uy, double cx, double cy, int rMin, int w360)
    {
        var phi = (ux % w360) * 2.0 * Math.PI / w360;
        var r = rMin + uy;
        return (cx + r * Math.Cos(phi), cy + r * Math.Sin(phi));
    }

    /// <summary>
    /// 언랩 공간 폴리곤 → 원본 공간 곡변 폴리곤. 꼭짓점만 <see cref="ToSource"/> 로 옮겨 직선으로
    /// 이으면 반경 일정한 변(호)이 현으로 주저앉아 사각이 사다리꼴로 그려진다 — 변을 각도 간격으로
    /// 잘게 나눠 표본 전부를 옮긴다. 반경 방향 변은 원본에서도 직선이라 표본이 늘어도 그림이 같다.
    /// </summary>
    public static (double X, double Y)[] PolyToSource(
        IReadOnlyList<(double X, double Y)> poly, double cx, double cy, int rMin, int w360, double stepDeg = 2.0)
    {
        var colsPerStep = Math.Max(1.0, w360 * stepDeg / 360.0);
        var pts = new List<(double X, double Y)>();
        for (var i = 0; i < poly.Count; i++)
        {
            var (ax, ay) = poly[i];
            var (bx, by) = poly[(i + 1) % poly.Count];
            var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(bx - ax) / colsPerStep), 1, 512);
            for (var s = 0; s < steps; s++)
            {
                var t = (double)s / steps;
                pts.Add(ToSource(ax + (bx - ax) * t, ay + (by - ay) * t, cx, cy, rMin, w360));
            }
        }
        return pts.ToArray();
    }

    /// <summary>
    /// 언랩 공간에서 발견된 패턴의 외곽 → 원본 공간 곡변 폴리곤. 외곽 규약은 발견 미리보기와 같다 —
    /// 학습영역 모양대로(사각/원), 중심 = 발견 자세, 크기 = 템플릿 × 발견 스케일, 사각은 발견각만큼 회전.
    /// 외곽의 가로 폭이 한 주기(w360) 이상이면 빈 폴리곤을 준다 — 되감으면 링이 제 위에 겹쳐
    /// 그려지고 반경 방향 끝 변이 경계 아닌 자리에 눈금처럼 남는다(전 주기를 학습한 템플릿).
    /// 그때는 그릴 것이 자리 십자뿐이다.
    /// </summary>
    public static (double X, double Y)[] FoundToSource(CvPose p, (int W, int H) templ, CvTrainShape shape,
        double cx, double cy, int rMin, int w360)
    {
        List<(double X, double Y)> outline;
        if (shape == CvTrainShape.Circle)
        {
            var r = Math.Min(templ.W, templ.H) / 2.0 * p.Scale;
            outline = new List<(double X, double Y)>(48);
            for (var i = 0; i < 48; i++)
            {
                var t = i * Math.PI * 2 / 48;
                outline.Add((p.FoundX + r * Math.Cos(t), p.FoundY + r * Math.Sin(t)));
            }
        }
        else
        {
            var hw = templ.W * p.Scale / 2.0;
            var hh = templ.H * p.Scale / 2.0;
            var rad = p.ThetaDeg * Math.PI / 180.0;
            var (cos, sin) = (Math.Cos(rad), Math.Sin(rad));
            outline = new List<(double X, double Y)>(4);
            foreach (var (dx, dy) in new[] { (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh) })
                outline.Add((p.FoundX + dx * cos - dy * sin, p.FoundY + dx * sin + dy * cos));
        }

        if (outline.Max(pt => pt.X) - outline.Min(pt => pt.X) >= w360) return [];

        return PolyToSource(outline, cx, cy, rMin, w360);
    }

    /// <summary>
    /// 언랩 공간 랜드마크 대조용 자세 접기. 언랩은 가로가 주기(w360)인데
    /// <see cref="CvInspGeom.VerifyLandmark"/> 의 기대 자리 이동은 선형이라, 기대 창이 이음새를
    /// 넘으면 잘려서 마크가 실재해도 0점이 된다. 기대 창의 여유가 가장 큰 사본(자세 X 그대로 /
    /// ±한 주기)을 골라 자세를 옮긴다. 세로(반경)는 주기가 없어 손대지 않는다.
    /// 자세 X 를 옮겨도 안전한 것은 언랩 공간 매칭이 회전 탐색 없이 돌아 대조 회전각이 0 이기
    /// 때문이다 — 회전이 있으면 회전 중심까지 함께 옮겨져 기하가 틀어진다.
    /// </summary>
    public static CvPose WrapPoseForLandmark(
        CvPose p, CvPatternOpt pat, CvPatternOpt landmark, int stripCols, int w360, double searchPx)
    {
        // 기대 창의 중심 오프셋과 절반 폭 — VerifyLandmark 가 창을 세우는 규칙을 그대로 따른다.
        var templHalfW = 0.0;
        if (landmark.TemplatePng is not null)
        {
            using var t = Cv2.ImDecode(landmark.TemplatePng, ImreadModes.Grayscale);
            if (!t.Empty()) templHalfW = t.Cols / 2.0;
        }

        double centerDx, halfW;
        if (landmark.UseSearchRegion)
        {
            centerDx = landmark.SearchX + landmark.SearchW / 2.0 - pat.TrainedOriginX;
            halfW = Math.Max(landmark.SearchW / 2.0, templHalfW);
        }
        else
        {
            centerDx = landmark.TrainedOriginX - pat.TrainedOriginX;
            halfW = templHalfW + Math.Max(8, searchPx);
        }

        var bestShift = 0.0;
        var bestMargin = double.NegativeInfinity;
        foreach (var shift in new[] { 0.0, -w360, (double)w360 })
        {
            var cx = p.FoundX + shift + centerDx;
            var margin = Math.Min(cx - halfW, stripCols - (cx + halfW));
            if (margin > bestMargin)
            {
                bestMargin = margin;
                bestShift = shift;
            }
        }

        return bestShift == 0 ? p : p with { FoundX = p.FoundX + bestShift };
    }
}
