using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

/// <summary>
/// 패턴 파인더 — 각도(+옵션 스케일) 스텝 회전 템플릿 매칭.
/// 코스(축소·굵은 각도 스텝 × 스케일 그리드) → 파인(원본·최적 스케일에서 가는 각도 스텝) → 파라볼라 각도 보간.
/// 각도 스윕은 시도별 독립이라 병렬 실행 (Parallel.For).
/// 최적 후보를 스코어와 함께 반환 — AcceptScore 판정은 호출자 몫 (기각 스코어도 진단/티칭 참고에 쓰임).
/// null 은 구조적 불능(무특징 템플릿·탐색 공간 부족)만.
/// <b>회전 방식은 학습 모양에 따라 갈린다.</b> 사각은 <b>장면을 돌려</b> 무마스크로 매칭한다 —
/// 템플릿을 돌리면 회전 캔버스가 생기고 그 여백을 빼려고 마스크가 필요해지는데, 마스크드 매칭은
/// 훨씬 비싼 별도 경로다(실측 6배, GPU 커널도 없다). 원형은 마스크가 회전 여백이 아니라 외접 사각
/// 모서리(원 주변 배경)를 빼는 본질적 역할이라 종전대로 템플릿을 돌린다.
///
/// <b>UseSearchRegion 영역 = 패턴 중심 허용 범위</b> — 중심이 그린 사각 안이면 몸체가 사각 밖으로
/// 걸쳐도 잡는다(가장자리 대상을 버리지 않는다). 예전에는 이 사각을 템플릿 반치수만큼 넓혀 잡았는데,
/// 확장량은 <b>최대</b> 스케일 기준인데 중심 환산은 <b>선택된</b> 스케일 기준이라 둘이 어긋나면서 실효
/// 범위가 스케일 설정에 따라 흔들렸다(그린 사각보다 넓어졌다) — 지금은 유효 중심을 그린 사각 그대로
/// 건다. 영역을 안 그렸으면(전 이미지) 이미지 전체가 허용 범위다.
///
/// <b>비용 계약</b>: 대상이 없는(내용이 희소한) 장면은 코스 단계에서 유효 후보 없음으로 끝나고 즉시
/// 빈 목록을 돌려준다 — 파인 단계를 아예 밟지 않으므로 <b>발견되는 장면보다 빠르다</b>. 원본 해상도
/// 전면 스캔은 코스를 못 돌린 경우에만 쓰는 폴백이다(축소 후 치수 부족, 또는 후보가 단일이라
/// 코스를 건너뛴 경우).
///
/// <b>다중 검출</b>(<see cref="CvPatternOpt.MaxCount"/> &gt; 1): 최고 스코어 자리를 취한 뒤 그 주변을
/// 스코어 맵에서 지우고 다음 최고점을 취하는 식으로 겹치지 않는 검출을 차례로 뽑는다. 억제 반경은
/// <see cref="CvPatternOpt.MinSepPct"/>(템플릿 대각선 대비 %, 100 = 겹침 없음)가 정한다. 요청 수보다 적게 나오는 것은 정상이며(그만큼만 반환),
/// 겹침 판정은 <b>위치만</b> 본다 — 같은 자리의 다른 각도는 같은 검출로 본다.
/// </summary>
public static class CvPatternFinder
{
    /// <summary>코스 단계가 신뢰할 만한 최소 축소 해상도 — 축소 후 템플릿의 <b>기하평균 한 변</b>(픽셀,
    /// sqrt(W·H)·CoarseScale). 이보다 작으면 축소본의 정규화 상관이 사실상 노이즈라 (각도·스케일)
    /// 최고점이 엉뚱한 자리로 잡히고, 파인은 코스가 고른 자리 주변만 다시 보므로 <b>복구되지 않는다</b>.
    ///
    /// 합성 장면 실측(80시행/셀, 실제각 ±12°·존 ±15°, 성공 = 각오차 ≤2° 그리고 위치오차 ≤3px)으로
    /// 종횡비 1:1~10:1 을 훑으면 실패율이 10% 아래로 내려가는 지점이 <b>기하평균 기준으로만 일정</b>하다 —
    /// 40×40 12.0 / 56×28 11.9 / 80×20 10.0 / 120×12 11.4. 같은 자료를 짧은 변으로 재면 12.0/8.4/5.0/3.6
    /// 으로 5배 흩어지고(길쭉할수록 짧은 변이 작아도 견딘다), 대각선으로 재면 17~36 으로 흩어진다.
    /// 그래서 재는 값은 기하평균이고 문턱은 11 이다. 정사각·원형 템플릿에서는 이 값이 옛 규약(대각선
    /// 캔버스 16px)과 사실상 같아(16/√2 = 11.3) 동작이 바뀌지 않고, 길쭉한 템플릿에서만 경고가 새로 뜬다.
    ///
    /// 문턱 아래라고 해서 반드시 오검출은 아니다 — 축소 템플릿의 <b>어느 한 변이 4px 미만</b>이 되면
    /// 코스 스윕 자체가 성립하지 않아 원본 해상도로 전 각도를 훑는 경로로 넘어가고, 그쪽은 느릴 뿐
    /// 정확하다(실측 실패율 0~1%). 실제로 위험한 곳은 그 바닥과 이 문턱 사이의 좁은 띠다.
    /// 판정은 호출자 몫 — 파인더는 이 값으로 동작을 바꾸지 않는다(설정을 말없이 덮어쓰면 택트가
    /// 조용히 늘어난다).</summary>
    public const double MinReliableCoarsePx = 11.0;

    /// <summary>코스 단계가 <b>실제로 도는가</b>. 후보가 하나뿐이면(각도 존 0 + 스케일 탐색 끔) 코스는
    /// 무의미해서 통째로 건너뛰고 파인이 바로 평가한다 — 그때는 축소본이 아무리 작아도 위험하지 않다.
    /// 두 경로가 같은 조건을 쓴다(각도 후보 수 &gt; 1 또는 스케일 후보 수 &gt; 1).</summary>
    public static bool CoarseRuns(CvPatternOpt opt)
    {
        var zone = opt.UseAngleSearch ? Math.Abs(opt.AngleZoneDeg) : 0.0;
        var step = Math.Max(0.5, opt.CoarseStepDeg);
        var angles = (int)Math.Floor(2 * zone / step) + 1;   // centerDeg−zone 부터 step 간격
        if (angles > 1) return true;
        if (!opt.UseScaleSearch) return false;
        var sZone = Math.Clamp(Math.Abs(opt.ScaleZonePct), 0, 50) / 100.0;
        var sStep = Math.Clamp(opt.ScaleStepPct, 0.5, 25) / 100.0;
        return (int)Math.Round(sZone / sStep) >= 1;
    }

    /// <summary>코스 해상도 점검 — (코스가 보는 <b>최소</b> 템플릿 한 변 px, 최소선을 채우는 권장 CoarseScale).
    /// 미학습·템플릿 디코드 실패·<b>코스가 안 도는 설정</b>이면 null(점검 대상 아님).
    ///
    /// 재는 값은 경로와 무관하게 템플릿 기하평균이다 — 사각은 장면을 돌려 템플릿을 그대로 쓰고, 원형은
    /// 템플릿을 돌리지만 상관에 실제로 기여하는 것은 마스크 안쪽 내용이라, 어느 쪽도 대각선 캔버스 치수가
    /// 재야 할 양은 아니다. 여기에 <b>스케일 탐색의 최소 배율</b>까지 곱한다 — 코스는 1.0 만 보는 것이 아니라
    /// (1−ScaleZonePct/100) 배까지 줄인 템플릿으로도 최고점을 고르므로, 그 가장 작은 것이 실효 해상도다.</summary>
    public static (double CoarsePx, double RecommendedCoarseScale)? CheckCoarseResolution(CvPatternOpt opt)
    {
        if (opt.TemplatePng is not { Length: > 0 } png) return null;
        if (!CoarseRuns(opt)) return null;
        using var templ = Cv2.ImDecode(png, ImreadModes.Grayscale);
        if (templ.Empty()) return null;

        var basePx = Math.Sqrt((double)templ.Cols * templ.Rows);
        if (basePx <= 0) return null;

        var cs = Math.Clamp(opt.CoarseScale, 0.1, 1.0);   // MatchCore 와 같은 클램프 — 실효값으로 재야 한다
        var minScale = opt.UseScaleSearch
            ? Math.Max(0.1, 1.0 - Math.Clamp(Math.Abs(opt.ScaleZonePct), 0, 50) / 100.0)
            : 1.0;
        var eff = basePx * minScale;
        // 백분 올림 — 이진 부동소수 오차로 딱 떨어지는 값이 한 칸 위로 올라가지 않게 미세 여유를 뺀다
        // (11/20 = 0.55 가 55.0000000000000007 로 나와 0.56 이 되던 자리).
        var need = Math.Min(1.0, Math.Ceiling(MinReliableCoarsePx / eff * 100 - 1e-9) / 100);
        return (eff * cs, need);
    }

    /// <summary>최고 스코어 하나 — 종전 규약 그대로 (MaxCount 와 무관하게 1개만 계산한다).</summary>
    public static CvPose? Match(Mat filtered, Mat template, CvPatternOpt opt)
    {
        var one = MatchCore(filtered, template, opt, 1);
        return one.Count > 0 ? one[0] : null;
    }

    /// <summary>겹치지 않는 검출을 스코어 내림차순으로 최대 <see cref="CvPatternOpt.MaxCount"/> 개.
    /// 빈 목록 = 구조적 불능 또는 유효 스코어 없음 (AcceptScore 판정은 호출자 몫).</summary>
    public static IReadOnlyList<CvPose> MatchAll(Mat filtered, Mat template, CvPatternOpt opt)
        => MatchCore(filtered, template, opt, Math.Max(1, opt.MaxCount));

    private static List<CvPose> MatchCore(Mat filtered, Mat template, CvPatternOpt opt, int want)
    {
        // 원형 학습 템플릿(외접 사각 크롭) — 마스크를 내접원으로 만들어 모서리 배경을 매칭에서 제외.
        // 학습 시점 모양 기준(TrainedShape) — 학습 후 토글 변경이 기존 템플릿 해석을 바꾸지 않게.
        var circular = opt.TrainedShape == CvTrainShape.Circle;

        // 무특징(상수) 템플릿 가드 — CCoeffNormed 는 분산 0 템플릿에서 전면 1.0 을 내 허위 매치가 되므로 불능 처리.
        // 원형은 매칭 영역(내접원)과 같은 마스크로 잰다 — 사각 전체로 재면 원 밖 모서리의 에지가
        // 가드를 통과시키고, 정작 매칭되는 원 안이 균일해 노이즈성 고득점 허위 발견이 가능해진다.
        Scalar tStd;
        if (circular)
        {
            using var guardMask = new Mat(template.Rows, template.Cols, MatType.CV_8UC1, Scalar.Black);
            FillInscribedEllipse(guardMask, template.Cols, template.Rows);
            Cv2.MeanStdDev(template, out _, out tStd, guardMask);
        }
        else
        {
            Cv2.MeanStdDev(template, out _, out tStd);
        }
        if (tStd.Val0 < 1.0) return [];

        // 사각은 <b>장면을 돌린다</b> — 템플릿을 돌리면 회전 캔버스가 생기고 그 여백을 빼려고 마스크가
        // 필요해지는데, 마스크드 매칭은 훨씬 비싼 별도 경로다(실측 6배). 장면을 대신 돌리면 여백 자체가
        // 없어 마스크도 게이트도 필요 없다. 원형은 마스크가 회전 여백이 아니라 <b>외접 사각 모서리</b>(원
        // 주변 배경)를 빼는 본질적 역할이라 그대로 둔다 — 빼면 실기에서 판정이 무너진다.
        if (!circular && MatchSceneRotate(filtered, template, opt, want) is { } sceneHits) return sceneHits;

        // 스케일 후보 — 1.0 중심 대칭 그리드 (탐색 끔이면 1.0 단독). 파인은 최적 스케일에서만 돌므로
        // 비용 증가는 코스 단계 × 스케일 수에 국한된다.
        var scaleList = new List<double> { 1.0 };
        if (opt.UseScaleSearch)
        {
            var sZone = Math.Clamp(Math.Abs(opt.ScaleZonePct), 0, 50) / 100.0;
            var sStep = Math.Clamp(opt.ScaleStepPct, 0.5, 25) / 100.0;
            var n = (int)Math.Round(sZone / sStep);
            scaleList.Clear();
            for (var k = -n; k <= n; k++) scaleList.Add(1.0 + k * sStep);
        }

        // 탐색 영역은 최대 스케일 캔버스 기준으로 한 번만 산출 — 전 스케일 매칭이 같은 searchMat 공유.
        var diag = Math.Sqrt((double)template.Cols * template.Cols + (double)template.Rows * template.Rows);
        var canvasMax = (int)Math.Ceiling(diag * scaleList.Max());

        // 탐색 작업영역 = 중심 허용 범위(UseSearchRegion 영역 또는 전체 이미지 — 영역은 패턴 전체가
        // 아니라 발견 중심을 제약한다)를 캔버스 반치수만큼 확장한 사각. 이미지와의 교집합만 크롭하고 부족분은 검정 패딩 —
        // 가장자리 데드존 제거(패딩 없이는 가장자리 canvas/2 이내 중심이 구조적으로 발견 불가) +
        // 필요 최소 면적 유지(영역 탐색 시 matchTemplate 비용 직결). 패딩은 마스크로 비교에서 제외되므로
        // 무해 — 패턴이 실제로 시야 밖에 걸치면 그 부분만 검정과 비교돼 스코어 자연 감점(부분 가시 규약).
        double cx0, cy0, cw, ch;
        if (opt.UseSearchRegion) { cx0 = opt.SearchX; cy0 = opt.SearchY; cw = opt.SearchW; ch = opt.SearchH; }
        else { cx0 = 0; cy0 = 0; cw = filtered.Cols; ch = filtered.Rows; }

        var half = canvasMax / 2 + 1;
        var dx0 = (int)Math.Floor(cx0) - half;
        var dy0 = (int)Math.Floor(cy0) - half;
        var dx1 = (int)Math.Ceiling(cx0 + cw) + half;
        var dy1 = (int)Math.Ceiling(cy0 + ch) + half;

        var ix0 = Math.Max(0, dx0);
        var iy0 = Math.Max(0, dy0);
        var ix1 = Math.Min(filtered.Cols, dx1);
        var iy1 = Math.Min(filtered.Rows, dy1);
        if (ix1 <= ix0 || iy1 <= iy0) return [];   // 허용 범위가 이미지 밖 — 구조적 불능

        using var searchMat = new Mat();
        using (var roi = filtered[new Rect(ix0, iy0, ix1 - ix0, iy1 - iy0)])
            Cv2.CopyMakeBorder(roi, searchMat, iy0 - dy0, dy1 - iy1, ix0 - dx0, dx1 - ix1, BorderTypes.Constant, Scalar.Black);

        // 각도 존은 "티칭 자세" 중심 — 템플릿은 학습각을 편 상태로 저장되므로 장면에서의 기대 자세는
        // TrainedAngleDeg 다. θ=0 중심으로 돌면 회전 티칭 + 좁은 존 조합에서 자기 자세조차 존 밖이 되어
        // 진짜 위치가 후보에서 빠진다 (존 0 = "자세 변동 없음"이라는 기대와 정합).
        var zone = opt.UseAngleSearch ? Math.Abs(opt.AngleZoneDeg) : 0.0;   // 앵글 탐색 끔 = 학습 자세 고정(존 0)
        var centerDeg = opt.TrainedAngleDeg;
        var coarseStep = Math.Max(0.5, opt.CoarseStepDeg);
        var fineStep = Math.Clamp(opt.FineStepDeg, 0.1, coarseStep);
        var cs = Math.Clamp(opt.CoarseScale, 0.1, 1.0);

        // 검출 억제 반경 — 기준은 <b>템플릿 대각선</b>이다. 중심 거리가 대각선 이상이면 두 템플릿 사각은
        // 어떤 각도로 돌아도 겹치지 않는다(각 사각이 반지름 대각선/2 인 원에 들어가고 그 두 원이 떨어진다).
        // 그래서 100% = "겹침 없음" 선이고, 그보다 작게 두면 겹친 자리도 별개 검출로 허용하는 뜻이 된다.
        // 짧은 변을 기준으로 삼으면 길쭉한 템플릿에서 대부분 겹친 자리가 통과해 버린다.
        var sepBase = diag * Math.Clamp(opt.MinSepPct, 5, 200) / 100.0;

        // 추가 검출 씨앗 — (스코어, searchMat 좌표계 좌상단, 각도). want==1 이면 채우지 않는다.
        var seeds = new List<(double Score, Point Loc, double Theta)>();

        // 코스 — 스케일별 캔버스를 만들어 축소본에서 굵은 각도 스텝 병렬 스윕. 축소 후 치수가
        // 매칭 불능인 스케일은 건너뛰고, 전 스케일 불능이면 파인이 스케일 1.0 전존을 커버
        // (작은 템플릿 + 작은 CoarseScale 조합 방어 — 기존 폴백 유지).
        var coarseBestScore = double.NegativeInfinity;
        var coarseBestTheta = centerDeg;
        var coarseBestScale = 1.0;
        var coarseTried = false;   // 코스가 실제로 매칭을 수행했는가 — 치수 불능으로 못 돈 것과 구분(아래 조기 반환의 근거)
        var coarseThetas = new List<double>();
        for (var t = centerDeg - zone; t <= centerDeg + zone + 1e-9; t += coarseStep) coarseThetas.Add(t);

        // 후보가 단일(존 0 + 스케일 탐색 끔)이면 코스는 축소 매칭 비용만 쓰는 무의미 단계 — 생략하고
        // 파인이 그 단일 후보를 원본 해상도로 바로 평가한다.
        var coarseBestLoc = default(Point);
        if (coarseThetas.Count > 1 || scaleList.Count > 1)
        {
            using var searchSmall = new Mat();
            if (cs < 1.0) Cv2.Resize(searchMat, searchSmall, default, cs, cs, InterpolationFlags.Area);
            else searchMat.CopyTo(searchSmall);

            // 중심 허용 범위(코스 좌표계) — 작업영역 원점 기준으로 옮기고 코스 축소를 곱한다.
            // 코스가 영역 밖을 짚으면 파인은 그 주변만 보므로 복구되지 않는다.
            var coarseCenter = new Rect(
                (int)Math.Floor((cx0 - dx0) * cs), (int)Math.Floor((cy0 - dy0) * cs),
                (int)Math.Ceiling(cw * cs), (int)Math.Ceiling(ch * cs));

            (Mat Templ, Mat Mask, Mat Gate)? BuildSmall(double scale)
            {
                using var pack = BuildCanvas(template, scale, circular);
                if (pack is null) return null;

                var templS = new Mat();
                var maskS = new Mat();
                if (cs < 1.0)
                {
                    Cv2.Resize(pack.Templ, templS, default, cs, cs, InterpolationFlags.Area);
                    Cv2.Resize(pack.Mask, maskS, default, cs, cs, InterpolationFlags.Nearest);   // 이진성 유지
                }
                else
                {
                    pack.Templ.CopyTo(templS);
                    pack.Mask.CopyTo(maskS);
                }

                if (templS.Cols < 4 || templS.Rows < 4
                    || searchSmall.Cols < templS.Cols || searchSmall.Rows < templS.Rows)
                {
                    templS.Dispose();
                    maskS.Dispose();
                    return null;
                }
                return (templS, maskS, BuildEnergyGate(searchSmall, templS, maskS));
            }

            void DisposeSmall((Mat Templ, Mat Mask, Mat Gate) s)
            {
                s.Templ.Dispose();
                s.Mask.Dispose();
                s.Gate.Dispose();
            }

            (double Score, double Theta, Point Loc) SweepTheta((Mat Templ, Mat Mask, Mat Gate) s, List<double> angles)
            {
                var arr = new (double Score, Point Loc)[angles.Count];
                System.Threading.Tasks.Parallel.For(0, angles.Count, i =>
                    arr[i] = TryAngle(searchSmall, s.Templ, s.Mask, s.Gate, angles[i], coarseCenter));
                var b = 0;
                for (var i = 1; i < arr.Length; i++)
                    if (arr[i].Score > arr[b].Score) b = i;
                return (arr[b].Score, angles[b], arr[b].Loc);
            }

            // 분리 스윕 — 전 그리드(각도×스케일) 대신 각도(기준 스케일 1.0) → 스케일(최적 각 고정) →
            // 각도 재확정(최적 스케일) 순으로 O(A+S) 회만 시도. 축소 해상도에선 한 축이 어긋나도
            // 다른 축의 최적이 유지되는 것을 전제로 한 근사 — 최종 검증은 파인(원본 해상도)이 담당.
            var s0 = BuildSmall(1.0);
            if (s0 is not null)
            {
                coarseTried = true;
                var r0 = SweepTheta(s0.Value, coarseThetas);
                coarseBestScore = r0.Score;
                coarseBestTheta = r0.Theta;
                coarseBestLoc = r0.Loc;
                DisposeSmall(s0.Value);
            }

            if (scaleList.Count > 1)
            {
                var others = scaleList.Where(v => Math.Abs(v - 1.0) > 1e-9).ToArray();
                var anchorTheta = coarseBestTheta;
                var sres = new (double Score, Point Loc)[others.Length];
                System.Threading.Tasks.Parallel.For(0, others.Length, i =>
                {
                    var sp = BuildSmall(others[i]);
                    if (sp is null) { sres[i] = (double.NegativeInfinity, default); return; }
                    sres[i] = TryAngle(searchSmall, sp.Value.Templ, sp.Value.Mask, sp.Value.Gate, anchorTheta, coarseCenter);
                    DisposeSmall(sp.Value);
                });
                // 이 스윕은 coarseTried 를 세우지 않는다 — 앵커각 하나만, 그것도 기준 아닌 스케일에서
                // 찔러 보는 정련 단계라 "코스가 장면을 판정했다"고 볼 수 없다. 템플릿 에너지는 면적에
                // 비례하는데 장면 잉크는 그대로라 큰 스케일일수록 게이트가 가혹해, 여기서 나온 −inf 로
                // 조기 종료하면 정작 기준 스케일(느슨한 게이트)에서 찾았을 패턴을 잃는다.
                for (var i = 0; i < others.Length; i++)
                {
                    if (sres[i].Score > coarseBestScore)
                    {
                        coarseBestScore = sres[i].Score;
                        coarseBestScale = others[i];
                        coarseBestLoc = sres[i].Loc;
                    }
                }

                if (Math.Abs(coarseBestScale - 1.0) > 1e-9)
                {
                    var s2 = BuildSmall(coarseBestScale);
                    if (s2 is not null)
                    {
                        // 각도 재확정은 C1 최적각 ±2 코스 스텝 창만 — 기준 스케일의 각도 추정은
                        // 스케일이 어긋나도 근방에 머무르므로 전존 재스캔 불필요 (비용 절감).
                        var reThetas = coarseThetas
                            .Where(t => Math.Abs(t - anchorTheta) <= 2 * coarseStep + 1e-9)
                            .ToList();
                        if (reThetas.Count > 0)
                        {
                            var r2 = SweepTheta(s2.Value, reThetas);
                            if (r2.Score >= coarseBestScore)
                            {
                                coarseBestScore = r2.Score;
                                coarseBestTheta = r2.Theta;
                                coarseBestLoc = r2.Loc;
                            }
                        }
                        DisposeSmall(s2.Value);
                    }
                }
            }

            // 추가 검출 — 확정된 스케일로 전 각도를 다시 훑되 각도마다 상위 want 개를 받는다.
            // 스케일은 대상 전체의 성질이라 검출마다 다시 고르지 않는다(최고 스코어가 정한 값 공유).
            // want==1 이면 이 스윕 자체를 건너뛰므로 기본 경로 비용은 종전과 같다.
            // 최고 후보조차 없으면(전 각도 무효) 같은 게이트를 쓰는 추가 검출도 있을 수 없어 스윕을 건너뛴다.
            if (want > 1 && !double.IsNegativeInfinity(coarseBestScore))
            {
                var sFinal = BuildSmall(coarseBestScale);
                if (sFinal is not null)
                {
                    var sepCoarse = Math.Max(2.0, sepBase * coarseBestScale * cs);
                    var peaks = new (double Score, Point Loc)[coarseThetas.Count][];
                    System.Threading.Tasks.Parallel.For(0, coarseThetas.Count, i =>
                        peaks[i] = TryAngleTop(searchSmall, sFinal.Value.Templ, sFinal.Value.Mask, sFinal.Value.Gate,
                            coarseThetas[i], coarseCenter, want, sepCoarse));
                    for (var i = 0; i < peaks.Length; i++)
                    {
                        foreach (var pk in peaks[i])
                        {
                            // 코스 좌표 → 작업영역 좌표. 씨앗은 파인 ROI 를 놓을 자리만 정하므로 반올림 오차는 무해하다.
                            seeds.Add((pk.Score,
                                new Point((int)Math.Round(pk.Loc.X / cs), (int)Math.Round(pk.Loc.Y / cs)),
                                coarseThetas[i]));
                        }
                    }
                    DisposeSmall(sFinal.Value);
                }
            }
        }
        // 코스가 <b>기준 스케일 전 각도를 훑었는데</b> 유효 후보가 하나도 없으면 그것이 결론이다 —
        // 미발견으로 끝낸다. 아래 파인 폴백(비국소)은 "코스를 <b>못 돌린</b>" 경우(축소 후 치수 부족)를
        // 메우려는 경로지 "코스가 돌았는데 아무것도 없다"를 재확인하는 경로가 아니다. 둘을 뭉뚱그리면
        // <b>내용이 없는 장면일수록 느려지는</b> 뒤집힌 비용 곡선이 생긴다: 폴백은 원본 해상도로
        // 작업영역 전면 × 존 전 각도를 훑어 국소 경로의 수백 배를 쓰는데, 창 에너지 게이트가
        // 코스·파인에서 거의 같은 비율로 걸려 결론이 뒤집히지 않기 때문이다. (템플릿·장면은 Area,
        // 마스크는 Nearest 로 줄고 두 리샘플 위상이 달라 실효 문턱이 수 % 어긋난다 — 그 폭에 걸치는
        // 장면은 잉크가 게이트 절벽에 놓인 것이라 프레임 노이즈로도 뒤집히는 자리다.)
        // 실측: 대상이 없는 장면 3.7초 → 0.05초.
        if (coarseTried && double.IsNegativeInfinity(coarseBestScore)) return [];

        // 여기 도달하는 false 는 코스를 못 돌린 경우뿐 — 축소 후 치수 부족(구조적 불능)이거나,
        // 후보가 단일이라 코스 단계를 설계상 건너뛴 경우(존 0 + 스케일 탐색 끔)다. 둘 다 파인이 맡는다.
        var coarseRan = !double.IsNegativeInfinity(coarseBestScore);

        // 파인 — 원본 해상도, 최적 스케일 고정. 코스가 돌았으면 최적 각도 ±코스스텝, 아니면 존 전체.
        var fineScale = coarseRan ? coarseBestScale : 1.0;
        using var fine = BuildCanvas(template, fineScale, circular);
        if (fine is null) return [];

        var sepFull = Math.Max(2.0, sepBase * fineScale);

        // 씨앗(창 위치 + 그 각도) 하나를 원본 해상도로 정밀화 — 각도 미세 스윕 + 파라볼라 보간.
        // localized=false 면 작업영역 전체를 존 전체 각도로 훑는다(코스 미실행 폴백 경로).
        CvPose? RefineAt(Point seedLoc, double seedTheta, bool localized)
        {
            var fineLo = localized ? Math.Max(centerDeg - zone, seedTheta - coarseStep) : centerDeg - zone;
            var fineHi = localized ? Math.Min(centerDeg + zone, seedTheta + coarseStep) : centerDeg + zone;
            var thetas = new List<double>();
            for (var t = fineLo; t <= fineHi + 1e-9; t += fineStep) thetas.Add(t);
            if (thetas.Count == 0) thetas.Add(seedTheta);

            // 파인 국소화 — 씨앗 주변만 원본 해상도로 재탐색 (전 영역을 각도마다 재스캔하는
            // 비용 제거 — 파인 면적이 캔버스+여유로 고정). 여유는 코스 축소 양자화·보간 오차 대비.
            var fineRoi = new Rect(0, 0, searchMat.Cols, searchMat.Rows);
            if (localized)
            {
                const int margin = 32;
                var size = fine.Canvas + 2 * margin;
                var bx = Math.Clamp(seedLoc.X - margin, 0, Math.Max(0, searchMat.Cols - size));
                var by = Math.Clamp(seedLoc.Y - margin, 0, Math.Max(0, searchMat.Rows - size));
                fineRoi = new Rect(bx, by,
                    Math.Min(size, searchMat.Cols - bx),
                    Math.Min(size, searchMat.Rows - by));
            }
            using var fineSearch = searchMat[fineRoi];

            // 중심 허용 범위(파인 좌표계) — 작업영역 원점과 파인 ROI 원점을 차례로 뺀다.
            // 확장분(캔버스 반치수)은 매칭을 위한 여유일 뿐이므로 최종 중심은 이 사각 안이어야 한다.
            var fineCenter = new Rect(
                (int)Math.Floor(cx0) - dx0 - fineRoi.X, (int)Math.Floor(cy0) - dy0 - fineRoi.Y,
                (int)Math.Ceiling(cw), (int)Math.Ceiling(ch));

            using var fineGate = BuildEnergyGate(fineSearch, fine.Templ, fine.Mask);
            var scores = new double[thetas.Count];
            var locs = new Point[thetas.Count];
            System.Threading.Tasks.Parallel.For(0, thetas.Count, i =>
                (scores[i], locs[i]) = TryAngle(fineSearch, fine.Templ, fine.Mask, fineGate, thetas[i], fineCenter));

            var best = 0;
            for (var i = 1; i < thetas.Count; i++)
                if (scores[i] > scores[best]) best = i;
            if (double.IsNegativeInfinity(scores[best])) return null;   // 전 각도 유효 스코어 없음

            // 파라볼라 보간 — 이웃 스코어로 서브스텝 각도
            // 이웃이 유한할 때만 보간한다 — 게이트가 막은 각도는 −무한대라 그대로 쓰면 NaN 각도가 된다.
            var theta0 = thetas[best];
            if (best > 0 && best < thetas.Count - 1
                && double.IsFinite(scores[best - 1]) && double.IsFinite(scores[best + 1]))
            {
                var sPrev = scores[best - 1];
                var s0 = scores[best];
                var sNext = scores[best + 1];
                var denom = sPrev - 2 * s0 + sNext;
                if (Math.Abs(denom) > 1e-12)
                {
                    var delta = 0.5 * (sPrev - sNext) / denom;
                    if (double.IsFinite(delta)) theta0 += Math.Clamp(delta, -0.5, 0.5) * fineStep;
                }
            }

            // 발견 중심 = 작업영역 원점 + 파인 ROI + 매치 좌상단 + <b>회전된 내용 중심</b>.
            // Rotate 는 캔버스 중심 C 를 축으로 도는데 내용 중심은 패딩 정수 나눗셈 탓에 C 에서 delta 만큼
            // 벗어나 있다(패딩차가 홀수면 −0.5). 회전 후 그 자리는 C + M(θ)·delta 이므로, delta 를 안 돌리고
            // 더하면 편차가 <b>각도와 함께 자란다</b> — 2·|delta|·sin(θ/2), 90°에서 1.0px, 180°에서 1.41px
            // (실측 확인). 옛 규약의 고정 0.707px 보다 큰 각도에서 오히려 나쁘다. 짝수 패딩(delta=0)에서는
            // 증상이 없어 템플릿 크기에 따라 나타났다 사라진다.
            var half = (fine.Canvas - 1) / 2.0;
            var dCx = fine.ContentCx - half;
            var dCy = fine.ContentCy - half;
            // warpAffine 은 <b>대상→원본</b> 사상이라 원본의 delta 는 대상에서 M(−θ)·delta 자리로 옮겨간다.
            var rotRad = thetas[best] * Math.PI / 180.0;   // 실제로 매칭한 각도(보간 전) — locs[best] 가 그 각의 결과다
            var rotCos = Math.Cos(rotRad);
            var rotSin = Math.Sin(rotRad);
            var foundX = dx0 + fineRoi.X + locs[best].X + half + (dCx * rotCos - dCy * rotSin);
            var foundY = dy0 + fineRoi.Y + locs[best].Y + half + (dCx * rotSin + dCy * rotCos);
            return new CvPose(theta0, opt.TrainedOriginX, opt.TrainedOriginY, foundX, foundY, scores[best], fineScale);
        }

        var poses = new List<CvPose>();

        // 1번 검출 — 종전 경로 그대로 (코스 최적 창 → 파인 정밀화).
        var firstSeed = coarseRan
            ? new Point((int)Math.Round(coarseBestLoc.X / cs), (int)Math.Round(coarseBestLoc.Y / cs))
            : default;
        if (RefineAt(firstSeed, coarseBestTheta, coarseRan) is not { } first) return [];
        poses.Add(first);
        if (want <= 1) return poses;

        // 코스가 못 돈 폴백 경로에선 씨앗이 없다 — 원본 해상도로 전 각도를 훑어 후보를 모은다.
        if (!coarseRan)
        {
            var fineCenterFull = new Rect(
                (int)Math.Floor(cx0) - dx0, (int)Math.Floor(cy0) - dy0,
                (int)Math.Ceiling(cw), (int)Math.Ceiling(ch));
            using var fullGate = BuildEnergyGate(searchMat, fine.Templ, fine.Mask);
            var thetasFull = new List<double>();
            for (var t = centerDeg - zone; t <= centerDeg + zone + 1e-9; t += fineStep) thetasFull.Add(t);
            if (thetasFull.Count == 0) thetasFull.Add(centerDeg);

            var pk = new (double Score, Point Loc)[thetasFull.Count][];
            System.Threading.Tasks.Parallel.For(0, thetasFull.Count, i =>
                pk[i] = TryAngleTop(searchMat, fine.Templ, fine.Mask, fullGate, thetasFull[i], fineCenterFull, want, sepFull));
            for (var i = 0; i < pk.Length; i++)
                foreach (var q in pk[i]) seeds.Add((q.Score, q.Loc, thetasFull[i]));
        }

        // 씨앗을 스코어 내림차순으로 훑으며 이미 확보한 검출과 겹치지 않는 것만 정밀화한다.
        // 겹침은 위치만 본다 — 같은 자리의 다른 각도는 같은 검출이므로 각도는 판정에 넣지 않는다.
        // 거리는 유클리드 — 스코어 맵 억제가 원(Cv2.Circle)이므로 같은 자로 재야 두 단계가 어긋나지 않는다.
        // 정밀화 전(씨앗 좌표)과 후(발견 중심) 두 번 걸러 낸다: 씨앗이 갈라져 있어도 파인에서
        // 같은 자리로 수렴할 수 있어서다.
        var canvasHalfX = fine.ContentCx;   // 좌상단 → 내용 중심 (발견 좌표와 같은 자)
        var canvasHalfY = fine.ContentCy;
        var sepSq = sepFull * sepFull;
        bool TooClose(double x, double y)
            => poses.Any(o => (o.FoundX - x) * (o.FoundX - x) + (o.FoundY - y) * (o.FoundY - y) < sepSq);

        // 정밀화 시도 상한 — 문턱을 넘는 자리가 없으면 씨앗 전부에 정밀화를 돌려 택트를 잡아먹는다.
        // 순수 비용 가드라 결과 품질에는 관여하지 않는다(넉넉히 잡는다).
        var maxTries = want * 4;
        var tries = 0;
        foreach (var seed in seeds.OrderByDescending(x => x.Score))
        {
            if (poses.Count >= want || tries >= maxTries) break;

            if (TooClose(dx0 + seed.Loc.X + canvasHalfX, dy0 + seed.Loc.Y + canvasHalfY)) continue;
            tries++;
            if (RefineAt(seed.Loc, seed.Theta, true) is not { } q) continue;

            // 문턱을 못 넘은 자리가 나오면 <b>거기서 멈춘다</b>. 안 자르면 대상이 둘뿐인 장면에서도
            // 요청 수를 채우려고 배경 아무 자리나 끌어와 반환하고, 그 쓸모없는 자리에 정밀화 비용까지 쓴다.
            //
            // 씨앗은 코스(축소) 스코어 내림차순인데 판정은 정밀화 후 스코어라 둘의 순위가 뒤집힐 수는
            // 있다. 다만 <b>실측하면 그 뒤집힘이 결과를 바꾸는 일이 거의 없다</b> — 두 스코어의 상관이
            // r=0.985 로 매우 높아, "앞선 씨앗이 기각인데 뒤쪽 씨앗은 통과"가 정밀화 982 회 중 1 회였고
            // 그 1 회도 문턱을 겨우 넘긴 자리였다(검출 수가 2→1 로 줄 뿐 판정은 그대로).
            // 반대로 계속 뒤지는 쪽의 대가는 크다: 검출을 want 만큼 못 채우는 장면이 대부분인데
            // 그때마다 maxTries 까지 정밀화를 돌아 택트를 통째로 잡아먹는다(같은 실측에서 정밀화
            // 횟수 982 → 154 회, 패턴 매칭 시간 약 1/6). 정밀화는 원본 해상도 매칭이라 검사에서
            // 가장 비싼 연산이고, 그래서 "검출이 적을수록 느려지는" 뒤집힌 비용 곡선이 나온다.
            if (q.Score < opt.AcceptScore) break;

            if (TooClose(q.FoundX, q.FoundY)) continue;
            poses.Add(q);
        }

        poses.Sort((a, b) => b.Score.CompareTo(a.Score));
        return poses;
    }

    /// <summary>원본 좌표 → 축소 좌표(픽셀 중심 사상). 씨앗 복원식 <c>(p+0.5)/cs − 0.5</c> 의 역이다 —
    /// 한쪽만 픽셀 중심을 쓰면 코스에서 고른 자리와 파인이 되돌린 자리가 반 픽셀씩 어긋난다.</summary>
    private static Point2d PivotSmall(Point2d p, double cs)
        => new((p.X + 0.5) * cs - 0.5, (p.Y + 0.5) * cs - 0.5);

    /// <summary>
    /// 사각 템플릿 경로 — <b>템플릿 대신 장면을 돌려</b> 매칭한다. 회전 캔버스가 없으니 여백도 마스크도
    /// 없고, 마스크드 매칭 특유의 병리(마스크 아래 에너지가 미소한 창의 허수 고득점, ±∞)도 없어
    /// 창 에너지 게이트가 필요 없다 — 내용이 없는 장면은 스스로 낮은 상관으로 걸러진다(실측 0.19~0.26).
    ///
    /// 장면 캔버스는 <b>중심 허용 범위의 중심 C</b> 를 축으로 필요한 반경만큼만 잡는다
    /// (R = 허용범위 반지름 + 템플릿 반대각 × 최대 스케일). C 를 축으로 도니 그 원 안의 내용은
    /// 어떤 각도에서도 캔버스 밖으로 나가지 않는다 — 탐색 영역을 좁게 티칭할수록 이 캔버스가 작아진다.
    /// 발견 좌표는 역회전으로 원래 공간에 되돌린다.
    /// </summary>
    private static List<CvPose>? MatchSceneRotate(Mat filtered, Mat template, CvPatternOpt opt, int want)
    {
        var scaleList = new List<double> { 1.0 };
        if (opt.UseScaleSearch)
        {
            var sZone = Math.Clamp(Math.Abs(opt.ScaleZonePct), 0, 50) / 100.0;
            var sStep = Math.Clamp(opt.ScaleStepPct, 0.5, 25) / 100.0;
            var n = (int)Math.Round(sZone / sStep);
            scaleList.Clear();
            for (var k = -n; k <= n; k++) scaleList.Add(1.0 + k * sStep);
        }

        double cx0, cy0, cw, ch;
        if (opt.UseSearchRegion) { cx0 = opt.SearchX; cy0 = opt.SearchY; cw = opt.SearchW; ch = opt.SearchH; }
        else { cx0 = 0; cy0 = 0; cw = filtered.Cols; ch = filtered.Rows; }
        if (cw <= 0 || ch <= 0) return [];

        var diag = Math.Sqrt((double)template.Cols * template.Cols + (double)template.Rows * template.Rows);

        // 탐색 영역 = <b>패턴 중심이 있을 수 있는 범위</b>. 몸체가 영역 밖으로 걸쳐도 중심만 안이면 잡는다
        // (가장자리 대상을 버리지 않는다). 캔버스를 템플릿 반대각만큼 더 넓게 잡는 것은 그 매칭을
        // 가능하게 하려는 <b>내부 사정</b>일 뿐, 허용 범위를 넓히지 않는다 — 유효 중심은 아래에서
        // 그린 사각 그대로 건다. 예전에는 이 확장량이 최대 스케일 기준인데 중심 환산은 선택 스케일
        // 기준이라 둘이 어긋나면서 실효 범위가 스케일 설정에 따라 흔들렸다(그린 사각보다 넓어졌다).
        var zone = opt.UseAngleSearch ? Math.Abs(opt.AngleZoneDeg) : 0.0;
        var centerDeg = opt.TrainedAngleDeg;

        // 캔버스 치수 — <b>실제로 쓰는 각도 범위</b>만큼만 돌렸을 때 필요한 사각을 낸다.
        // 대각선 정사각으로 잡으면(=±180° 를 가정) 길쭉한 영역에서 워프·매칭 면적이 영역의
        // 수십 배가 되어, 종횡비가 커질수록 마스크드 경로보다 오히려 느려진다.
        // 회전 후 좌표는 x(θ)=A·cos(θ−ψ), y(θ)=A·sin(ψ−θ) 라 극값 각도가 닫힌 형태로 나온다 —
        // 그 각도가 존 안이면 그 값을, 밖이면 존 끝값을 쓴다(정확, 표본화 아님).
        var margin = diag * scaleList.Max() / 2.0;   // 중심이 영역 끝이어도 템플릿이 담기게
        var halfEx = cw / 2.0 + margin;
        var halfEy = ch / 2.0 + margin;
        var loDeg = centerDeg - zone;
        var hiDeg = centerDeg + zone;
        double needX = 0, needY = 0;
        foreach (var (ex, ey) in new[] { (halfEx, halfEy), (-halfEx, halfEy), (halfEx, -halfEy), (-halfEx, -halfEy) })
        {
            var amp = Math.Sqrt(ex * ex + ey * ey);
            var psi = Math.Atan2(ey, ex) * 180.0 / Math.PI;
            foreach (var cand in new[] { loDeg, hiDeg, psi, psi + 180, psi - 180, psi - 90, psi + 90 })
            {
                if (cand < loDeg - 1e-9 || cand > hiDeg + 1e-9) continue;   // 존 밖 극값은 끝값이 대신한다
                var r = cand * Math.PI / 180.0;
                needX = Math.Max(needX, Math.Abs(amp * Math.Cos(r - psi * Math.PI / 180.0)));
                needY = Math.Max(needY, Math.Abs(amp * Math.Sin(psi * Math.PI / 180.0 - r)));
            }
        }
        // 캔버스는 <b>두 방향 소요를 모두</b> 담아야 한다 — 회전 후 내용이 잘리지 않을 크기(needX/needY)와,
        // 원본에서 축 정렬로 잘라 오는 입력 자체의 크기(halfEx/halfEy). 이 캔버스가 소스 크롭이자 워프
        // 대상이라 둘 중 큰 쪽을 써야 한다. needX 가 halfEx 를 덮는 것은 존이 0°(또는 180°)를 품을 때뿐이라,
        // 학습각이 축에서 멀고 존이 좁으면 캔버스가 <b>탐색 영역보다 좁아진다</b> — 영역 600×106·학습각 90°·
        // 존 ±5° 에서 캔버스 폭 228 로 영역의 38% 만 잘라 왔다(실측). 그러면 나머지는 아예 후보에서 빠져
        // 영역 안의 진짜 대상이 미검출되고(실측 스코어 0.999 → 0.18), 가장자리에 걸친 대상은 몸체가 잘려
        // 스코어만 깎인다(0.999 → 0.765 — 중심 허용 범위 규약이 깨진다).
        var sideW = (int)Math.Ceiling(Math.Max(needX, halfEx) * 2) + 2;
        var sideH = (int)Math.Ceiling(Math.Max(needY, halfEy) * 2) + 2;

        // 비용이 뒤집히면 <b>옛 마스크드 경로에 양보한다</b>(null 반환). 장면 회전은 캔버스가 "영역을
        // 각도 존만큼 돌린 사각"이라, 길쭉한 영역 + 넓은 존에서는 그 면적이 마스크드 작업영역보다
        // 훨씬 커진다 — 마스크드가 단위 면적당 몇 배 비싼 것을 면적으로 다 까먹는 지점이 있다.
        // 실측 역전점이 면적비 5 근처였다(±90° 존, 종횡비 7:1). 문턱은 3 으로 여유 있게 잡는다 —
        // 정사각에 가까운 영역은 2 안팎이라(전 이미지 탐색 = 2.1) 영향이 없고, 역전 구간만 걸러진다.
        var maskedArea = (cw + diag * scaleList.Max()) * (ch + diag * scaleList.Max());
        if ((double)sideW * sideH > maskedArea * 3) return null;

        // 장면 캔버스 — 영역 중심이 캔버스 중심에 오도록 원본에서 잘라 붙인다.
        var sx = (int)Math.Round(cx0 + cw / 2.0 - sideW / 2.0);
        var sy = (int)Math.Round(cy0 + ch / 2.0 - sideH / 2.0);
        var ix0 = Math.Max(0, sx);
        var iy0 = Math.Max(0, sy);
        var ix1 = Math.Min(filtered.Cols, sx + sideW);
        var iy1 = Math.Min(filtered.Rows, sy + sideH);
        if (ix1 <= ix0 || iy1 <= iy0) return [];   // 허용 범위가 이미지 밖 — 구조적 불능

        // 이미지 밖은 <b>가장자리 복제</b>로 채운다 — 검정으로 채우면 프레임 경계에 인위적인 계단이
        // 생기고, 마스크 없는 정규화 상관에서는 그 계단이 템플릿 자신의 대상/배경 계단과 강하게
        // 상관해 <b>대상이 없는 프레임이 고득점</b>을 받는다(균일 배경 평면에서 0.86~0.90 실측).
        // 예전에는 창 에너지 게이트가 그 자리를 걸렀지만 이 경로엔 게이트가 없다. 복제는 계단을
        // 만들지 않아 그 허위 상관이 서지 않는다.
        // 가로 순환 — 언랩 띠처럼 x 가 주기인 입력은 <b>복제 대신 감아</b> 채운다. 그러면 남는 자리에
        // 없는 내용이 아니라 <b>진짜 반대편 내용</b>이 들어와, 복제 영역이 망가진 진짜 대상을 이기는
        // 병리가 사라진다. 주기가 0 이하이거나 소스 폭보다 크면 끔(감을 것이 없다).
        var period = opt.WrapPeriodX > 0 && opt.WrapPeriodX <= filtered.Cols
            ? (int)Math.Round(opt.WrapPeriodX)
            : 0;

        using var scene = new Mat();
        if (period > 0)
        {
            // y 는 주기가 아니다(반경 축) — 세로만 가장자리 복제, 가로는 modulo 로 소스 열을 끌어온다.
            using var mid = new Mat(iy1 - iy0, sideW, filtered.Type());
            for (var cx = 0; cx < sideW;)
            {
                var srcX = ((sx + cx) % period + period) % period;
                var run = Math.Min(sideW - cx, period - srcX);
                run = Math.Min(run, filtered.Cols - srcX);   // 주기가 소스보다 짧을 때의 꼬리 보호
                if (run <= 0) break;
                using var srcCol = filtered[new Rect(srcX, iy0, run, iy1 - iy0)];
                using var dstCol = mid[new Rect(cx, 0, run, iy1 - iy0)];
                srcCol.CopyTo(dstCol);
                cx += run;
            }
            Cv2.CopyMakeBorder(mid, scene, iy0 - sy, sideH - (iy1 - sy), 0, 0, BorderTypes.Replicate);
        }
        else
        {
            // 이미지 밖은 <b>가장자리 복제</b>로 채운다 — 검정으로 채우면 프레임 경계에 인위적인 계단이
            // 생기고, 마스크 없는 정규화 상관에서는 그 계단이 템플릿 자신의 대상/배경 계단과 강하게
            // 상관해 <b>대상이 없는 프레임이 고득점</b>을 받는다(균일 배경 평면에서 0.86~0.90 실측).
            // 예전에는 창 에너지 게이트가 그 자리를 걸렀지만 이 경로엔 게이트가 없다. 복제는 계단을
            // 만들지 않아 그 허위 상관이 서지 않는다.
            using var src = filtered[new Rect(ix0, iy0, ix1 - ix0, iy1 - iy0)];
            Cv2.CopyMakeBorder(src, scene,
                iy0 - sy, sideH - (iy1 - sy), ix0 - sx, sideW - (ix1 - sx), BorderTypes.Replicate);
        }

        // 캔버스 안에서 <b>실제로 원본에서 복사해 온</b> 사각 — 나머지는 가장자리 복제라 상관에 쓰면 안 된다.
        // 감아 채운 열은 <b>진짜 픽셀</b>이므로 가로는 전 폭이 실제다 — 안 넓히면 커버리지 게이트가
        // 진짜 내용을 가짜로 세어 과잉 차단한다.
        var realRect = period > 0
            ? new Rect(0, iy0 - sy, sideW, iy1 - iy0)
            : new Rect(ix0 - sx, iy0 - sy, ix1 - ix0, iy1 - iy0);

        var coarseStep = Math.Max(0.5, opt.CoarseStepDeg);
        var fineStep = Math.Clamp(opt.FineStepDeg, 0.1, coarseStep);
        var cs = Math.Clamp(opt.CoarseScale, 0.1, 1.0);
        var sepBase = diag * Math.Clamp(opt.MinSepPct, 5, 200) / 100.0;

        var thetas = new List<double>();
        for (var t = centerDeg - zone; t <= centerDeg + zone + 1e-9; t += coarseStep) thetas.Add(t);
        if (thetas.Count == 0) thetas.Add(centerDeg);

        using var sceneSmall = new Mat();
        if (cs < 1.0) Cv2.Resize(scene, sceneSmall, default, cs, cs, InterpolationFlags.Area);
        else scene.CopyTo(sceneSmall);
        var realSmall = new Rect(
            (int)Math.Round(realRect.X * cs), (int)Math.Round(realRect.Y * cs),
            (int)Math.Round(realRect.Width * cs), (int)Math.Round(realRect.Height * cs));

        var bestScore = double.NegativeInfinity;
        var bestTheta = thetas[0];
        var bestScale = 1.0;
        var pivot = new Point2d(cx0 + cw / 2.0 - sx, cy0 + ch / 2.0 - sy);   // 캔버스 좌표의 영역 중심(회전축)
        var bestPt = pivot;
        var seeds = new List<(double Score, Point2d Pt, double Theta)>();

        // 코스 — 축소 장면에서 각도(기준 스케일) → 스케일(최적 각 고정) → 각도 재확정 순 분리 스윕.
        // 후보가 단일(존 0 + 스케일 탐색 끔)이면 코스는 무의미하므로 건너뛰고 파인이 바로 평가한다.
        var coarseRan = false;
        if (thetas.Count > 1 || scaleList.Count > 1)
        {
            (double Score, double Theta, Point2d Pt) Sweep(double scale, IReadOnlyList<double> angles)
            {
                using var templ = ScaledTemplate(template, scale * cs);
                if (templ.Cols < 4 || templ.Rows < 4 || sceneSmall.Cols < templ.Cols || sceneSmall.Rows < templ.Rows)
                    return (double.NegativeInfinity, angles[0], default);

                var arr = angles.ToArray();
                var res = new (double S, Point2d P)[arr.Length];
                System.Threading.Tasks.Parallel.For(0, arr.Length, i =>
                {
                    var top = TopAtAngle(sceneSmall, templ, arr[i], cw * cs, ch * cs, PivotSmall(pivot, cs), 1, 0, null, realSmall);
                    res[i] = top.Length > 0 ? top[0] : (double.NegativeInfinity, default);
                });
                var b = 0;
                for (var i = 1; i < res.Length; i++) if (res[i].S > res[b].S) b = i;
                return (res[b].S, arr[b], new Point2d((res[b].P.X + 0.5) / cs - 0.5, (res[b].P.Y + 0.5) / cs - 0.5));   // 축소 → 원본(픽셀중심 사상 역변환)
            }

            var r0 = Sweep(1.0, thetas);
            // 이 경로의 −inf 는 <b>구조적 불능</b>(축소 후 템플릿 4px 미만이거나 장면보다 큼)뿐이다 —
            // 게이트가 없어 "내용이 없어서 −inf" 가 나올 수 없다. 그래서 코스 전멸이면 파인이 원본
            // 해상도로 맡는 것이 맞다. 마스크드 경로가 같은 상황에서 즉시 빈 목록을 돌려주는 것과
            // 규약이 다른데, 그쪽 −inf 는 <b>창 에너지 게이트가 막은 것</b>이라 원본 해상도로 다시 봐도
            // 같은 게이트에 또 걸리는 헛일이기 때문이다. 두 경로의 "못 찾음"이 다른 뜻인 것은 의도다.
            if (!double.IsNegativeInfinity(r0.Score)) { bestScore = r0.Score; bestTheta = r0.Theta; bestPt = r0.Pt; coarseRan = true; }

            if (scaleList.Count > 1 && coarseRan)
            {
                // 스케일 후보는 <b>병렬로</b> 판다 — 각도가 하나(앵커각)라 Sweep 내부 병렬이 무효라서,
                // 직렬로 두면 스케일 종수가 많은 레시피에서 이 단계가 통째로 한 코어에 묶인다.
                var others = scaleList.Where(v => Math.Abs(v - 1.0) > 1e-9).ToArray();
                var sres = new (double Score, double Theta, Point2d Pt)[others.Length];
                var anchor = bestTheta;
                System.Threading.Tasks.Parallel.For(0, others.Length, i => sres[i] = Sweep(others[i], [anchor]));
                for (var i = 0; i < others.Length; i++)
                {
                    if (sres[i].Score > bestScore) { bestScore = sres[i].Score; bestScale = others[i]; bestPt = sres[i].Pt; }
                }
                if (Math.Abs(bestScale - 1.0) > 1e-9)
                {
                    // 각도 재확정은 최적각 ±2 코스 스텝만 — 스케일이 어긋나도 각도 추정은 근방에 머문다.
                    var re = thetas.Where(t => Math.Abs(t - bestTheta) <= 2 * coarseStep + 1e-9).ToList();
                    if (re.Count > 0)
                    {
                        var r2 = Sweep(bestScale, re);
                        if (r2.Score >= bestScore) { bestScore = r2.Score; bestTheta = r2.Theta; bestPt = r2.Pt; }
                    }
                }
            }

            // 추가 검출 씨앗 — 확정 스케일로 전 각도를 다시 훑되 각도마다 상위 want 개.
            if (want > 1 && coarseRan)
            {
                using var templ = ScaledTemplate(template, bestScale * cs);
                if (templ.Cols >= 4 && templ.Rows >= 4 && sceneSmall.Cols >= templ.Cols && sceneSmall.Rows >= templ.Rows)
                {
                    var sepCoarse = Math.Max(2.0, sepBase * bestScale * cs);
                    var arr = thetas.ToArray();
                    var peaks = new (double S, Point2d P)[arr.Length][];
                    System.Threading.Tasks.Parallel.For(0, arr.Length, i =>
                        peaks[i] = TopAtAngle(sceneSmall, templ, arr[i], cw * cs, ch * cs, PivotSmall(pivot, cs), want, sepCoarse, null, realSmall));
                    for (var i = 0; i < peaks.Length; i++)
                        foreach (var pk in peaks[i])
                            seeds.Add((pk.S, new Point2d((pk.P.X + 0.5) / cs - 0.5, (pk.P.Y + 0.5) / cs - 0.5), arr[i]));
                }
            }
        }

        // 파인 — 원본 해상도, 최적 스케일 고정. 코스가 돌았으면 씨앗 주변만(국소), 아니면 전 영역.
        using var fineTempl = ScaledTemplate(template, bestScale);
        if (fineTempl.Cols < 4 || fineTempl.Rows < 4) return [];
        var roiSide = Math.Max(fineTempl.Cols, fineTempl.Rows) + 64;   // 여유 32px — 코스 양자화·보간 오차 대비

        // 코스가 못 돈 경로(후보 단일)에선 씨앗이 없다 — 원본 해상도로 각도를 훑어 후보를 모은다.
        // 이걸 빠뜨리면 앵글·스케일 탐색을 둘 다 끈 레시피에서 다중 검출이 조용히 1개로 줄어든다.
        if (want > 1 && !coarseRan)
        {
            var sepFallback = Math.Max(2.0, sepBase);
            var ftFull = new List<double>();
            for (var t = centerDeg - zone; t <= centerDeg + zone + 1e-9; t += fineStep) ftFull.Add(t);
            if (ftFull.Count == 0) ftFull.Add(centerDeg);

            var arrFull = ftFull.ToArray();
            var pk = new (double S, Point2d P)[arrFull.Length][];
            System.Threading.Tasks.Parallel.For(0, arrFull.Length, i =>
                pk[i] = TopAtAngle(scene, fineTempl, arrFull[i], cw, ch, pivot, want, sepFallback, null, realRect));
            for (var i = 0; i < pk.Length; i++)
                foreach (var q in pk[i]) seeds.Add((q.S, q.P, arrFull[i]));
        }

        CvPose? Refine(Point2d seedPt, double seedTheta, bool localized)
        {
            var lo = localized ? Math.Max(centerDeg - zone, seedTheta - coarseStep) : centerDeg - zone;
            var hi = localized ? Math.Min(centerDeg + zone, seedTheta + coarseStep) : centerDeg + zone;
            var ft = new List<double>();
            for (var t = lo; t <= hi + 1e-9; t += fineStep) ft.Add(t);
            if (ft.Count == 0) ft.Add(seedTheta);

            var arr = ft.ToArray();
            var fs = new double[arr.Length];
            var fp = new Point2d[arr.Length];
            System.Threading.Tasks.Parallel.For(0, arr.Length, i =>
            {
                var top = TopAtAngle(scene, fineTempl, arr[i], cw, ch, pivot, 1, 0,
                    localized ? (seedPt, roiSide) : null, realRect);
                (fs[i], fp[i]) = top.Length > 0 ? top[0] : (double.NegativeInfinity, default);
            });

            var b = 0;
            for (var i = 1; i < arr.Length; i++) if (fs[i] > fs[b]) b = i;
            if (double.IsNegativeInfinity(fs[b])) return null;

            // 파라볼라 보간 — <b>이웃이 유한할 때만</b>. 무효 각도(−무한대)가 이웃이면 분자·분모가
            // 함께 발산해 NaN 이 나오고, Math.Clamp 는 NaN 을 걸러 주지 않아(비교가 모두 false)
            // 각도가 NaN 인 포즈가 그대로 나간다 — 그 포즈로 라인 탐색 좌표를 만들면 경계 검사가
            // NaN 을 통과시켜 네이티브 접근 위반으로 프로세스가 죽는다.
            var theta = arr[b];
            if (b > 0 && b < arr.Length - 1 && double.IsFinite(fs[b - 1]) && double.IsFinite(fs[b + 1]))
            {
                var denom = fs[b - 1] - 2 * fs[b] + fs[b + 1];
                if (Math.Abs(denom) > 1e-12)
                {
                    var delta = 0.5 * (fs[b - 1] - fs[b + 1]) / denom;
                    if (double.IsFinite(delta)) theta += Math.Clamp(delta, -0.5, 0.5) * fineStep;
                }
            }
            // 가로 순환이면 발견 X 를 주기로 접는다 — 캔버스가 여러 바퀴를 담을 수 있어 그대로 두면
            // 이미지 밖 좌표가 나간다. 소비자는 모두 좌표가 이미지 안이라고 가정한다.
            var fx = sx + fp[b].X;
            if (period > 0) fx = (fx % period + period) % period;
            return new CvPose(theta, opt.TrainedOriginX, opt.TrainedOriginY,
                fx, sy + fp[b].Y, Math.Min(fs[b], 1.0), bestScale);
        }

        var poses = new List<CvPose>();
        if (Refine(bestPt, bestTheta, coarseRan) is not { } first) return [];
        poses.Add(first);
        if (want <= 1) return poses;

        // 씨앗을 스코어 내림차순으로 훑으며 겹치지 않는 것만 정밀화 — 겹침은 위치만 본다.
        // 문턱을 못 넘는 자리가 나오면 거기서 멈춘다(배경 아무 자리나 끌어와 택트를 쓰지 않게).
        var sepFull = Math.Max(2.0, sepBase * bestScale);
        var sepSq = sepFull * sepFull;
        bool TooClose(double x, double y)
            => poses.Any(o => (o.FoundX - x) * (o.FoundX - x) + (o.FoundY - y) * (o.FoundY - y) < sepSq);

        var maxTries = want * 4;
        var tries = 0;
        foreach (var seed in seeds.OrderByDescending(s => s.Score))
        {
            if (poses.Count >= want || tries >= maxTries) break;
            if (TooClose(sx + seed.Pt.X, sy + seed.Pt.Y)) continue;
            tries++;
            if (Refine(seed.Pt, seed.Theta, true) is not { } q) continue;
            if (q.Score < opt.AcceptScore) break;
            if (TooClose(q.FoundX, q.FoundY)) continue;
            poses.Add(q);
        }

        poses.Sort((a, b) => b.Score.CompareTo(a.Score));
        return poses;
    }

    /// <summary>한 각도 매칭(장면 회전) — 장면을 돌려 무회전·무마스크 템플릿과 맞추고, 상위
    /// <paramref name="want"/> 개 자리를 <b>회전 전(캔버스) 좌표</b>로 되돌려 준다.
    /// <paramref name="localize"/> 를 주면 회전과 잘라내기를 한 변환으로 합쳐 씨앗 주변 ROI 만 만들어 낸다
    /// (각도마다 큰 캔버스를 통째로 돌리는 비용 제거).</summary>
    private static (double Score, Point2d Pt)[] TopAtAngle(Mat sceneMat, Mat templ, double thetaDeg,
        double allowW, double allowH, Point2d pivot, int want, double sepPx, (Point2d Seed, int Side)? localize,
        Rect realSrc)
    {
        var cx = pivot.X;
        var cy = pivot.Y;
        var rad = thetaDeg * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);

        using var m = Cv2.GetRotationMatrix2D(new Point2f((float)cx, (float)cy), thetaDeg, 1.0);
        var roiX = 0.0;
        var roiY = 0.0;
        var outSize = sceneMat.Size();
        if (localize is { } lz)
        {
            // 씨앗이 회전 좌표계에서 놓일 자리 — 회전 행렬의 평행이동에서 ROI 원점을 빼면
            // warpAffine 이 곧바로 잘라 낸 결과를 만든다.
            var dx0 = lz.Seed.X - cx;
            var dy0 = lz.Seed.Y - cy;
            roiX = Math.Round(cx + dx0 * cos + dy0 * sin - lz.Side / 2.0);
            roiY = Math.Round(cy - dx0 * sin + dy0 * cos - lz.Side / 2.0);
            m.Set(0, 2, m.Get<double>(0, 2) - roiX);
            m.Set(1, 2, m.Get<double>(1, 2) - roiY);
            outSize = new Size(lz.Side, lz.Side);
        }

        using var rotScene = new Mat();
        Cv2.WarpAffine(sceneMat, rotScene, m, outSize, InterpolationFlags.Linear, BorderTypes.Replicate);
        if (rotScene.Cols < templ.Cols || rotScene.Rows < templ.Rows) return [];

        using var result = new Mat();
        Cv2.MatchTemplate(rotScene, templ, result, TemplateMatchModes.CCoeffNormed);

        // 유효 중심 = <b>그린 사각 그대로</b>. 캔버스를 넓게 잡은 것은 매칭을 가능하게 하려는 내부
        // 사정일 뿐이라 허용 범위에 반영하지 않는다 — 템플릿 크기나 스케일 설정이 바뀌어도
        // 실제로 보는 범위는 그린 사각과 언제나 같다.
        var halfW = allowW / 2.0;
        var halfH = allowH / 2.0;

        // 그 사각을 회전 좌표계로 옮기면 기울어진 사각이 된다 → 네 꼭짓점 다각형으로 마스크를 만든다.
        // 좌표는 결과 맵 기준(loc = 중심 − 템플릿 반치수), ROI 원점도 함께 뺀다.
        var poly = new OpenCvSharp.Point[4];
        // 경계 여유 반 픽셀 — 마스크드 경로가 loc 범위를 +1px 넓혀 잡는 것과 같은 규약이다(반올림 손실 방지).
        // 두 경로가 다른 여유를 쓰면 같은 레시피가 비용 가드로 경로를 넘나들 때 가장자리 대상이 갈린다.
        var corners = new (double X, double Y)[] { (-halfW - 0.5, -halfH - 0.5), (halfW + 0.5, -halfH - 0.5), (halfW + 0.5, halfH + 0.5), (-halfW - 0.5, halfH + 0.5) };
        for (var i = 0; i < 4; i++)
        {
            var rx = corners[i].X * cos + corners[i].Y * sin;
            var ry = -corners[i].X * sin + corners[i].Y * cos;
            poly[i] = new OpenCvSharp.Point(
                (int)Math.Round(cx + rx - roiX - (templ.Cols - 1) / 2.0),
                (int)Math.Round(cy + ry - roiY - (templ.Rows - 1) / 2.0));
        }

        using var valid = new Mat(result.Size(), MatType.CV_8UC1, Scalar.Black);
        Cv2.FillConvexPoly(valid, poly, Scalar.White);

        // <b>창이 실제 픽셀 위에 얹혀 있어야 한다.</b> 캔버스 밖은 가장자리 복제로 채우는데, 복제 영역은
        // 한 축으로 상수라 그 축으로 퇴화한 템플릿과 상관이 높게 잡힌다 — 내용이 없는 창이 고득점한다
        // (실측: 창의 절반이 복제인 자리가 0.87. 같은 장면에서 보더 상수만 바꾸면 Replicate 0.947 /
        // Constant 0.654 로 갈려 원인이 복제 채움임이 분리된다). 마스크드 경로의 창 에너지 게이트가
        // 막던 자리인데 이 경로엔 게이트가 없어 구멍으로 남아 있었다.
        //
        // 추가 상관 없이 <b>기하로</b> 막는다 — 실제로 원본에서 복사해 온 사각을 같은 회전으로 옮겨
        // 유효 중심과 교집합한다. 중심 허용 범위 규약은 유지된다(몸체가 탐색 영역 밖으로 걸치는 것은
        // 여전히 허용) — 여기서 요구하는 것은 "창의 절반 이상이 진짜 픽셀"뿐이라, 사각을 회전 후 템플릿
        // 투영 반치수의 <b>절반</b>만큼 안으로 줄여 중심을 건다.
        if (realSrc.Width > 0 && realSrc.Height > 0)
        {
            var projX = (Math.Abs(cos) * templ.Cols + Math.Abs(sin) * templ.Rows) / 4.0;
            var projY = (Math.Abs(sin) * templ.Cols + Math.Abs(cos) * templ.Rows) / 4.0;
            double L = realSrc.X, T = realSrc.Y, R = realSrc.X + realSrc.Width - 1, B = realSrc.Y + realSrc.Height - 1;
            var rPoly = new OpenCvSharp.Point[4];
            var rc = new (double X, double Y)[] { (L, T), (R, T), (R, B), (L, B) };
            for (var i = 0; i < 4; i++)
            {
                var ex = rc[i].X - cx;
                var ey = rc[i].Y - cy;
                var rx = ex * cos + ey * sin;
                var ry = -ex * sin + ey * cos;
                rPoly[i] = new OpenCvSharp.Point(
                    (int)Math.Round(cx + rx - roiX - (templ.Cols - 1) / 2.0),
                    (int)Math.Round(cy + ry - roiY - (templ.Rows - 1) / 2.0));
            }
            // 회전된 사각을 축 정렬로 줄일 수는 없으므로, 중심 쪽으로 각 꼭짓점을 당겨 줄인다.
            var gx = (rPoly[0].X + rPoly[1].X + rPoly[2].X + rPoly[3].X) / 4.0;
            var gy = (rPoly[0].Y + rPoly[1].Y + rPoly[2].Y + rPoly[3].Y) / 4.0;
            for (var i = 0; i < 4; i++)
            {
                var vx = rPoly[i].X - gx;
                var vy = rPoly[i].Y - gy;
                var len = Math.Sqrt(vx * vx + vy * vy);
                if (len < 1e-9) continue;
                var cut = Math.Min(len * 0.9, Math.Sqrt(projX * projX + projY * projY));
                rPoly[i] = new OpenCvSharp.Point(
                    (int)Math.Round(gx + vx * (len - cut) / len),
                    (int)Math.Round(gy + vy * (len - cut) / len));
            }
            using var realMask = new Mat(result.Size(), MatType.CV_8UC1, Scalar.Black);
            Cv2.FillConvexPoly(realMask, rPoly, Scalar.White);
            Cv2.BitwiseAnd(valid, realMask, valid);
        }

        if (Cv2.CountNonZero(valid) == 0) return [];

        var hits = new List<(double Score, Point2d Pt)>(Math.Max(1, want));
        var r = (int)Math.Max(1, Math.Round(sepPx));
        for (var k = 0; k < Math.Max(1, want); k++)
        {
            if (Cv2.CountNonZero(valid) == 0) break;
            Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out var maxLoc, valid);
            if (double.IsNaN(maxVal) || double.IsInfinity(maxVal)) break;

            // 회전 좌표계 → 회전 전(캔버스) 좌표. getRotationMatrix2D(c, θ) 가
            // p' − c = [cos, sin; −sin, cos]·(p − c) 로 보내므로 되돌릴 때는 그 전치를 쓴다.
            // 반치수는 <b>(변−1)/2</b> — 학습이 GetRectSubPix(픽셀 중심 규약)로 크롭해 그 중심을
            // TrainedOrigin 에 적었으므로, 같은 물리 지점으로 되돌리려면 여기도 같은 자를 써야 한다.
            var px = roiX + maxLoc.X + (templ.Cols - 1) / 2.0;
            var py = roiY + maxLoc.Y + (templ.Rows - 1) / 2.0;
            var dx = px - cx;
            var dy = py - cy;
            hits.Add((Math.Min(maxVal, 1.0), new Point2d(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos)));

            if (k + 1 < want) Cv2.Circle(valid, maxLoc, r, Scalar.Black, -1);
        }
        return hits.ToArray();
    }

    /// <summary>스케일 적용 템플릿 — 1.0 이면 원본을 그대로 쓸 수 없으므로(호출측이 해제한다) 복제.</summary>
    private static Mat ScaledTemplate(Mat template, double scale)
    {
        if (Math.Abs(scale - 1.0) < 1e-9) return template.Clone();
        var m = new Mat();
        Cv2.Resize(template, m, default, scale, scale,
            scale < 1.0 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        return m;
    }

    /// <summary>스케일 적용 템플릿을 대각선 정사각 캔버스에 중앙 배치 — 회전 클리핑 방지.
    /// 캔버스 여백은 마스크로 매칭에서 완전 제외한다 — 여백을 스코어에 포함하면(검정 채움)
    /// 평균 대비 큰 음편차 픽셀이 패턴 본체보다 많아 상관을 지배, 실사 배경에서 "어두운 테두리를
    /// 두른 곳" 오매치를 만든다(실측 증상). 스케일 축소로 치수 불능이면 null.
    /// circular=원형 학습 템플릿 — 마스크를 사각 전면 대신 내접 타원(외접 사각 크롭이라 사실상 원)으로
    /// 채워 모서리 배경까지 제외한다. 원 마스크는 회전 불변이라 각도 스윕과 정합.</summary>
    private static CanvasPack? BuildCanvas(Mat template, double scale, bool circular)
    {
        Mat scaled;
        if (Math.Abs(scale - 1.0) < 1e-9)
        {
            scaled = template;
        }
        else
        {
            scaled = new Mat();
            Cv2.Resize(template, scaled, default, scale, scale,
                scale < 1.0 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        }

        try
        {
            if (scaled.Cols < 4 || scaled.Rows < 4) return null;

            var canvas = (int)Math.Ceiling(Math.Sqrt(
                (double)scaled.Cols * scaled.Cols + (double)scaled.Rows * scaled.Rows));
            var templ = new Mat(canvas, canvas, scaled.Type(), Scalar.Black);
            var mask = new Mat(canvas, canvas, MatType.CV_8UC1, Scalar.Black);
            // 정수 나눗셈이라 (canvas − 변)이 홀수면 내용이 캔버스 중심에서 0.5px 치우친다.
            // 그 치우침은 회전축 밖이라 발견각과 함께 도므로, 보고 좌표는 캔버스 중심이 아니라
            // <b>내용 중심</b>(pad + (변−1)/2)으로 잡아야 한다 — 아래 CanvasPack 에 pad 를 실어 보낸다.
            var pad = new Rect((canvas - scaled.Cols) / 2, (canvas - scaled.Rows) / 2, scaled.Cols, scaled.Rows);
            using (var padRoi = templ[pad]) scaled.CopyTo(padRoi);
            using (var maskRoi = mask[pad])
            {
                if (circular) FillInscribedEllipse(maskRoi, scaled.Cols, scaled.Rows);
                else maskRoi.SetTo(Scalar.White);
            }
            return new CanvasPack(templ, mask, canvas, pad.X + (scaled.Cols - 1) / 2.0, pad.Y + (scaled.Rows - 1) / 2.0);
        }
        finally
        {
            if (!ReferenceEquals(scaled, template)) scaled.Dispose();
        }
    }

    /// <summary>w×h 사각에 내접하는 타원(외접 사각 크롭이라 사실상 원)을 채운다 —
    /// 중심은 픽셀 중심 규약 (d−1)/2 로 회전(Rotate) 중심과 동일, 각도 스윕에서 반픽셀 흔들림 없음.
    /// 학습 가드(<see cref="CvPatternTeach.HasFeature"/>)도 같은 마스크를 쓴다 — 매칭과 학습이 같은 영역을 본다.</summary>
    internal static void FillInscribedEllipse(Mat maskRoi, int w, int h)
        => Cv2.Ellipse(maskRoi,
            new RotatedRect(new Point2f((w - 1) / 2f, (h - 1) / 2f), new Size2f(w, h), 0),
            Scalar.White, -1);

    private sealed class CanvasPack(Mat templ, Mat mask, int canvas, double contentCx, double contentCy) : IDisposable
    {
        public Mat Templ { get; } = templ;
        public Mat Mask { get; } = mask;
        public int Canvas { get; } = canvas;

        /// <summary>캔버스 안에서 <b>템플릿 내용</b>의 중심(픽셀 중심 규약). 패딩이 정수 나눗셈이라
        /// 캔버스 중심과 최대 0.5px 어긋나고, 그 어긋남은 회전축 밖이라 발견각과 함께 돈다.
        /// 발견 좌표는 이 값으로 잡아야 학습 좌표(GetRectSubPix 중심)와 같은 지점을 가리킨다.</summary>
        public double ContentCx { get; } = contentCx;

        public double ContentCy { get; } = contentCy;
        public void Dispose() { Templ.Dispose(); Mask.Dispose(); }
    }

    /// <summary>창 에너지 게이트 맵 — 희소(에지 필터 출력) 이미지에선 마스크 아래 내용이 거의 없는 창의
    /// 정규화 분모가 미소해 유한하지만 허수 같은 고득점이 남는다 (진짜 매치가 노이즈로 조금만 열화돼도
    /// 역전). 창 내 픽셀 합이 템플릿 합의 25% 미만인 위치를 후보에서 제외한다 — 실제 패턴 창은 조명
    /// 열화를 감안해도 여유 있게 통과하는 문턱. 미회전 마스크의 창 합으로 전 각도 공용(회전에 따른
    /// 창 합 변화가 작고 문턱이 느슨해 판별 유지) — 각도 루프 밖 1회 계산으로 매칭 비용 절감.</summary>
    private static Mat BuildEnergyGate(Mat search, Mat templ, Mat mask)
    {
        using var winSum = new Mat();
        Cv2.MatchTemplate(search, mask, winSum, TemplateMatchModes.CCorr);   // = 255 × (창 내 픽셀 합)

        var gate = new Mat();
        var maskCount = Cv2.CountNonZero(mask);
        if (maskCount == 0)
        {
            gate.Create(winSum.Size(), MatType.CV_8UC1);
            gate.SetTo(Scalar.Black);
            return gate;
        }

        var tEnergy = Cv2.Mean(templ, mask).Val0 * maskCount;
        Cv2.Compare(winSum, 255.0 * 0.25 * tEnergy, gate, CmpTypes.GE);
        return gate;
    }

    /// <summary>
    /// 한 각도의 매칭 — 최고 스코어와 그 위치. <paramref name="centerBounds"/> 는 <b>발견 중심이
    /// 허용되는 사각</b>(search 좌표계)이다: 작업영역은 캔버스 반치수만큼 넓혀 놓았으므로
    /// 그 전체에서 최대값을 고르면 중심이 탐색 영역 밖으로 나간다. 특히 스케일 탐색을 켜면 확장량은
    /// 최대 스케일 기준인데 중심 환산은 선택 스케일 캔버스로 되돌리므로 그 차이만큼 밀린다
    /// (스케일 1.0 단독일 때 대략 맞는 것은 결과 맵 크기의 우연이지 제약이 아니다).
    /// </summary>
    private static (double Score, Point Loc) TryAngle(Mat search, Mat templ, Mat mask, Mat energyGate, double thetaDeg, Rect centerBounds)
    {
        var one = TryAngleTop(search, templ, mask, energyGate, thetaDeg, centerBounds, 1, 0);
        return one.Length > 0 ? one[0] : (double.NegativeInfinity, default);
    }

    /// <summary>
    /// 한 각도의 상위 <paramref name="want"/> 개 자리 — 최고점을 취한 뒤 그 자리를 반경
    /// <paramref name="sepPx"/> 만큼 유효 마스크에서 지우고 다시 최고점을 취하는 식으로 반복한다.
    /// matchTemplate 은 한 번만 돌고 MinMaxLoc 만 반복하므로 추가 비용이 작다.
    /// 유효 위치가 없으면 빈 배열 — 요청보다 적게 나오는 것은 정상이다.
    /// </summary>
    private static (double Score, Point Loc)[] TryAngleTop(Mat search, Mat templ, Mat mask, Mat energyGate, double thetaDeg, Rect centerBounds, int want, double sepPx)
    {
        using var rot = Rotate(templ, thetaDeg, InterpolationFlags.Linear);
        using var rotMask = Rotate(mask, thetaDeg, InterpolationFlags.Nearest);
        using var result = new Mat();
        Cv2.MatchTemplate(search, rot, result, TemplateMatchModes.CCoeffNormed, rotMask);

        // 소독 — 발산 값 무효화. 이 OpenCV 빌드는 마스크 하 에너지 0 위치에 ±∞ 를 반환한다(실측).
        // 정규화 스코어의 이론 범위는 [-1,1] 이지만 완전 일치 위치의 부동소수 오버슈트(1+ε)를
        // 제외하면 안 되므로 밴드는 ±1.5 로 여유 있게 — 보고 스코어는 아래에서 1 로 클램프.
        using var valid = new Mat();
        Cv2.InRange(result, new Scalar(-1.5), new Scalar(1.5), valid);
        Cv2.BitwiseAnd(valid, energyGate, valid);

        // 중심 허용 범위 → 결과 맵 loc 범위 (loc = 중심 − 템플릿 반치수). 회전 캔버스는 정사각이라
        // 각도와 무관하게 반치수가 같다. 경계는 바깥쪽으로 한 픽셀 여유(반올림 손실 방지).
        // 반치수는 발견 좌표와 <b>같은 자</b>여야 한다 — rot.Cols/2 로 재면 패딩차가 짝수면 0.5px,
        // 홀수면 1.0px 만큼 유효 중심 사각이 통째로 밀려 "유효 중심 = 그린 사각" 계약이 템플릿 크기
        // 패리티에 따라 흔들린다(우/하단 1px 안의 대상이 빠지고 좌/상단 바깥이 통과).
        var lx = (int)Math.Floor(centerBounds.X - (rot.Cols - 1) / 2.0);
        var ly = (int)Math.Floor(centerBounds.Y - (rot.Rows - 1) / 2.0);
        var bounds = new Rect(lx, ly, centerBounds.Width + 1, centerBounds.Height + 1)
            .Intersect(new Rect(0, 0, result.Cols, result.Rows));
        if (bounds.Width <= 0 || bounds.Height <= 0) return [];

        using (var boundMask = new Mat(result.Size(), MatType.CV_8UC1, Scalar.Black))
        {
            using (var inside = boundMask[bounds]) inside.SetTo(Scalar.White);
            Cv2.BitwiseAnd(valid, boundMask, valid);
        }

        var hits = new List<(double Score, Point Loc)>(Math.Max(1, want));
        var r = (int)Math.Max(1, Math.Round(sepPx));
        for (var k = 0; k < Math.Max(1, want); k++)
        {
            if (Cv2.CountNonZero(valid) == 0) break;
            Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out var maxLoc, valid);
            if (double.IsNegativeInfinity(maxVal)) break;
            hits.Add((Math.Min(maxVal, 1.0), maxLoc));
            // 다음 회차를 위해 이 자리 주변을 후보에서 제외 — 같은 대상이 조금 어긋난 자리로 다시 뽑히는 것 방지.
            if (k + 1 < want) Cv2.Circle(valid, maxLoc, r, Scalar.Black, -1);
        }
        return hits.ToArray();
    }

    /// <summary>+θ(atan2/이미지 규약) 회전 — warpAffine 양수각은 반대 방향이라 −θ 전달.
    /// 입력은 대각선 캔버스 전제(잘림 없음), 회전 중심은 픽셀 중심 규약의 기하 중심.
    /// 마스크는 Nearest 로 이진성 유지 — 보더 0 은 매칭 제외 영역.</summary>
    private static Mat Rotate(Mat src, double thetaDeg, InterpolationFlags interp)
    {
        var m = Cv2.GetRotationMatrix2D(new Point2f((src.Cols - 1) / 2f, (src.Rows - 1) / 2f), -thetaDeg, 1.0);
        var dst = new Mat();
        Cv2.WarpAffine(src, dst, m, src.Size(), interp, BorderTypes.Constant, Scalar.Black);
        m.Dispose();
        return dst;
    }
}
