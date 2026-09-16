using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

/// <summary>
/// 검사 공통 순수 기하 — 각도 정규화(<see cref="Mod360"/>·<see cref="Wrap180"/>) · 발견 포즈로 티칭 기하를
/// 옮기기(<see cref="XformByPose"/>·<see cref="FixturePose"/>) · 정규화 창(<c>NormalizedWindow</c>) ·
/// 템플릿 매칭(<see cref="MatchPattern"/>·<see cref="MatchPatternAll"/>).
///
/// <b>여기 있는 것은 어느 응용에서나 같은 뜻인 계산뿐이다.</b> 무엇을 무엇으로 검증할지, 어느 점수부터
/// 합격으로 볼지, 후보 각도를 몇 개 돌지는 호스트가 정한다 — 이 파일은 그 판단에 쓸 값을 줄 뿐이다.
/// (0.25.0 에서 <c>VerifyLandmark</c> 를 걷어내며 그 선을 분명히 했다. 그 메서드는 "랜드마크가 맞는가" 라는
/// 응용 판정을 들고 있었고, 그러느라 티칭 관계까지 전제하고 있었다. 지금은 그 밑에 깔려 있던 기하만 남았다.)
/// 극좌표 언랩은 <see cref="CvUnwrapGeom"/> 에 있다.
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

    /// <summary>
    /// 픽스처 포즈 — 파인더가 낸 포즈에서 <b>학습각을 걷어</b>, <see cref="CvPose.Apply"/>·
    /// <see cref="CvPose.Inverse"/>·<see cref="CvPose.Compose"/> 에 그대로 태울 수 있는 포즈로 만든다.
    ///
    /// 이 보정이 따로 필요한 이유: 파인더는 <see cref="CvPose.ThetaDeg"/> 에 <b>절대 발견각</b>을 담는다.
    /// 티칭 기하를 옮기려면 학습각을 뺀 차를 써야 하는데(<see cref="XformByPose"/> 가 그 차를 쓴다),
    /// 그 사실을 모르고 발견 포즈에 바로 <c>Apply</c> 를 태우면 학습각만큼 회전이 더 먹는다 —
    /// 부품이 <b>움직이지 않았는데도</b> 25° 로 티칭한 패턴에서 43px 밀리는 식이다. 학습각이 0 이면 둘이
    /// 같아서 안 드러나므로, 그 값이 이름을 갖고 한 곳에 있어야 같은 실수가 안 난다.
    ///
    /// <c>FixturePose(pat, p).Apply(x, y)</c> 는 <c>XformByPose(pat, p, x, y)</c> 와 같다 — 회귀가 그것을 못 박는다.
    /// <paramref name="extraDeg"/> 는 후보 각도 가산분이다(대칭 접힘 스윕처럼 <b>호출자가 도는</b> 것 —
    /// 툴킷은 대상의 대칭 차수를 모른다).
    /// </summary>
    public static CvPose FixturePose(CvPatternOpt pat, CvPose found, double extraDeg = 0)
        => new(found.ThetaDeg + extraDeg - pat.TrainedAngleDeg,
            pat.TrainedOriginX, pat.TrainedOriginY, found.FoundX, found.FoundY, found.Score, found.Scale);

    /// <summary>
    /// 정규화 창 — 발견 포즈를 걷어 <b>부품이 티칭 자세·티칭 크기로 선</b> 창 하나를 잘라 온다.
    /// 그 창 안에서는 티칭 좌표가 그대로 통하므로, 티칭 때 잡아 둔 상대 위치(랜드마크·보조 특징·ROI)를
    /// 그 자리에서 바로 찾을 수 있다.
    ///
    /// <b>전체 이미지를 돌리지 않는다</b> — 목적지 크기가 곧 계산 범위라 창 넓이만큼만 든다. 후보 각도를
    /// 여러 개 도는 호출자에게는 그 차이가 접힘 수만큼 곱해진다.
    ///
    /// <b>돌아오는 길을 포즈로 돌려준다</b>(<c>WindowToImage</c>) — 창에서 찾은 좌표를 <c>Apply</c> 한 번으로
    /// 입력 이미지 좌표로 되돌린다. 역변환 체인을 호출자가 손으로 짤 일이 없고, <b>반환 좌표의 공간이
    /// 경로에 따라 갈리지 않는다</b>(창 좌표 아니면 이미지 좌표, 그 둘뿐이다).
    ///
    /// <b>돌려받은 <c>Window</c> 는 호출자 것이다</b> — 다 쓰면 <c>Dispose</c> 한다(후보 각도를 도는 루프라면
    /// 루프 안에서 잡고 놓는다).
    ///
    /// <b>회전은 <paramref name="extraDeg"/> 하나로 다 준다.</b> 창은 <b>원본에서 한 번에</b> 최종 자세로
    /// 떠 오므로, 잘라 놓고 다시 돌릴 때 필요하던 모서리 여유(회전 패딩)가 아예 필요 없다.
    ///
    /// ⚠ <b>대상 템플릿이 기울여 티칭됐다면 그 학습각도 여기에 실어야 한다</b> — 티칭 공간에서는 대상이
    /// 자기 <see cref="CvPatternOpt.TrainedAngleDeg"/> 만큼 돌아 있으므로, <c>extraDeg</c> 에 그 값을
    /// <b>더해야</b> 창 안에서 템플릿 자세로 선다(부호는 회귀가 실측으로 못 박는다).
    /// 안 더하면 기울여 티칭한 대상에서만 점수가 깎이는데, 증상이 "좀 낮다" 라서 <b>진짜 미검출과 구분되지
    /// 않는다.</b> 그 덧셈을 손으로 하지 않으려면 <b>대상 옵트를 받는 오버로드</b>를 쓴다 — 거기서는 이 함정이
    /// 구조로 사라진다.
    ///
    /// 창 크기는 <b>티칭 공간 픽셀</b>이다 — 부품이 크게 보여도 창에 담기는 티칭 범위는 그대로다.
    /// 이것이 의도다: 정규화가 발견 스케일을 나눠 없애므로 <b>대상도 티칭 크기로 돌아온다</b>. 그래서
    /// 티칭 크기로 뜬 템플릿이 창에서 같은 화소 수를 차지하고, 크기를 맞출 일 없이 바로 비교된다.
    /// 창을 발견 스케일만큼 키우면 오히려 티칭 때보다 넓은 범위가 들어와 엉뚱한 것을 물 여지가 는다.
    /// 크기는 "템플릿 + 여유" 로 잡는 것이 보통이고, 템플릿 크기는 <see cref="CvPatternOpt.TemplateSize"/> 가
    /// 디코드 없이 알려 준다.
    /// 경계 밖은 <paramref name="border"/> 로 채워지므로 창이 이미지를 벗어나도 크기가 줄지 않는다
    /// (잘린 창 때문에 좌표계가 바뀌는 일이 없다). 채운 자리와 진짜 어두운 자리를 가려야 하면
    /// <paramref name="borderValue"/> 를 쓰거나 <see cref="BorderTypes.Replicate"/> 를 고른다.
    ///
    /// 못 쓰는 포즈(<see cref="CvPose.IsValid"/> 가 false)면 던진다. 이미지가 비었거나 크기가 0 이하면 null.
    /// </summary>
    public static (Mat Window, CvPose WindowToImage)? NormalizedWindow(
        Mat img, CvPatternOpt pat, CvPose found,
        double taughtX, double taughtY, int width, int height, double extraDeg = 0,
        InterpolationFlags interp = InterpolationFlags.Linear,
        BorderTypes border = BorderTypes.Constant, Scalar? borderValue = null)
    {
        if (img is null || img.Empty() || pat is null || width <= 0 || height <= 0) return null;

        var fx = FixturePose(pat, found, extraDeg);
        // 창 왼쪽 위 모서리가 놓일 티칭 좌표 — 창 중앙이 요청한 티칭 점에 오도록.
        var (px, py) = fx.Apply(taughtX - width / 2.0, taughtY - height / 2.0);

        // 창 좌표 → 이미지 좌표. 원점 0 이므로 Apply 는 "그 모서리에서 회전·스케일만큼 민다" 가 된다.
        var windowToImage = new CvPose(fx.ThetaDeg, 0, 0, px, py, fx.Score, fx.Scale);

        var window = new Mat();
        using (var m = AffineOf(windowToImage.Inverse()))
            Cv2.WarpAffine(img, window, m, new Size(width, height), interp, border, borderValue ?? Scalar.All(0));
        return (window, windowToImage);
    }

    /// <summary>
    /// 정규화 창 — <b>대상 패턴이 티칭된 자리를, 그 패턴의 템플릿 자세로</b> 잘라 온다.
    /// 앵커(<paramref name="pat"/>)의 발견 포즈로 공간을 세우고, 창의 자리·크기·회전을
    /// <paramref name="target"/> 이 스스로 말하게 하는 형태다.
    ///
    /// 자리는 <see cref="CvPatternOpt.TrainedOriginX"/>/<c>Y</c>, 크기는 학습 템플릿 크기
    /// (<see cref="CvPatternOpt.TemplateSize"/>, 디코드 없음) + <paramref name="marginPx"/> 여유,
    /// 회전은 <paramref name="extraDeg"/> 에 <b><paramref name="target"/> 의 학습각을 더한 값</b>이다.
    ///
    /// <b>그 덧셈이 이 오버로드의 존재 이유다.</b> 낮은 쪽 오버로드는 각도를 호출자가 합쳐 넘겨야 하는데,
    /// 대상 학습각은 <b>조작자가 학습 사각을 기울이는 순간에만</b> 0 이 아니게 된다 — 원형으로 잡아도,
    /// 사각을 반듯하게 잡아도 0 이라 테스트와 평소 티칭에서는 드러나지 않는다. 빠뜨리면 기울여 잡은
    /// 첫 티칭에서만 점수가 깎이고, 그 증상이 진짜 미검출과 같은 모양이다. 손으로 더하게 두지 않는다.
    ///
    /// <paramref name="extraDeg"/> 는 후보 각도 가산분으로 남는다(대칭 접힘 스윕은 호출자가 돈다).
    /// 대상이 아직 학습되지 않았으면(템플릿 없음) null.
    ///
    /// <b>창과 함께 그 창에서 쓸 옵트(<c>MatchOpt</c>)를 돌려준다</b> — 정규화가 무엇을 걷어냈는지 아는 곳은
    /// 창을 만든 여기뿐이라서다. 원본 옵트를 그대로 <see cref="MatchPattern"/> 에 넣으면 <b>조용히 못 찾는다</b>:
    /// 파인더는 각도 존의 중심을 <see cref="CvPatternOpt.TrainedAngleDeg"/> 로 잡는데(장면에서 대상이 그 자세로
    /// 누워 있다는 전제), 이 창은 이미 그 각을 걷어 대상을 0° 로 세워 놓았다. 각도 탐색이 꺼져 있으면 존이 0 이라
    /// <b>창에 없는 그 한 자세만</b> 평가한다(실측: 준비된 옵트는 <c>ThetaDeg 0</c>, 그대로 넣으면 <c>20</c> —
    /// 학습각 그대로다). 점수가 얼마나 떨어지는지는 대상 나름이고, 자세가 뚜렷한 대상일수록 미검출로 간다.
    /// 각도 탐색이 켜져 있으면 존이 그 어긋남을 덮어 가려지기도 한다 — <b>그래서 더 나쁘다.</b>
    /// 편집기에서 스위치 하나를 끄는 순간 드러나는 결함이 된다.
    /// 그래서 <c>MatchOpt</c> 는 사본에 두 가지를 맞춰 준다:
    /// <list type="bullet">
    /// <item><c>TrainedAngleDeg = 0</c> — 창이 이미 세워 두었다.</item>
    /// <item><c>UseSearchRegion = true</c> + 창 중앙 ±<paramref name="marginPx"/> 사각 — 호출자가 말한 그 허용
    /// 범위를 그대로 지킨다. 안 걸면 파인더는 <b>창 전체</b>를 중심 허용 범위로 잡아, 실효 반경이
    /// <c>marginPx + 템플릿 절반</c> 쪽으로 넓어진다(옆 후보가 미끄러져 들어온다).</item>
    /// </list>
    /// 원본은 건드리지 않는다(<see cref="CvPatternOpt.Clone"/>).
    ///
    /// ⚠ <b><see cref="CvPatternOpt.UseAngleSearch"/>·<see cref="CvPatternOpt.UseScaleSearch"/> 는 그대로 둔다 —
    /// 그것은 호출자의 판단이다.</b> 다만 이 창에서 뜻이 달라진다: 각도 탐색을 켜면 이제 <b>0° 중심</b>으로 돌므로
    /// 자세가 흔들려도 잡히는데, 그 말은 <b>자세로 후보를 가르던 판별이 무력해진다</b>는 뜻이다(어느 후보각에서든
    /// 돌려 맞출 수 있다). 스케일 탐색은 창이 이미 티칭 크기라 1.0 중심이 되어 대개 불필요하다.
    /// 그래서 이 둘을 호스트가 <b>되돌려 끄기로 했다면, 조용히 끄지 않는다</b> — 편집기에서 켠 사람에게는
    /// 스위치가 먹지 않는 것으로 보이고, 왜 안 먹는지는 현장에서 풀 수 없다. 껐다는 사실을 로그로 남긴다.
    /// </summary>
    public static (Mat Window, CvPose WindowToImage, CvPatternOpt MatchOpt)? NormalizedWindow(
        Mat img, CvPatternOpt pat, CvPose found, CvPatternOpt target, int marginPx, double extraDeg = 0,
        InterpolationFlags interp = InterpolationFlags.Linear,
        BorderTypes border = BorderTypes.Constant, Scalar? borderValue = null)
    {
        if (target?.TemplateSize() is not { } t) return null;

        var w = t.W + 2 * marginPx;
        var h = t.H + 2 * marginPx;
        var got = NormalizedWindow(img, pat, found,
            target.TrainedOriginX, target.TrainedOriginY, w, h,
            extraDeg + target.TrainedAngleDeg, interp, border, borderValue);
        if (got is not { } g) return null;

        var run = target.Clone();
        run.TrainedAngleDeg = 0;
        run.UseSearchRegion = true;
        run.SearchX = w / 2.0 - marginPx;
        run.SearchY = h / 2.0 - marginPx;
        run.SearchW = 2 * marginPx;
        run.SearchH = 2 * marginPx;
        return (g.Window, g.WindowToImage, run);
    }

    /// <summary>포즈를 warpAffine 용 2×3 행렬로. 포즈 대수와 한 곳에서 맞물리게 두어 각도 부호를 다시 정하지 않는다 —
    /// warpAffine 은 양수각이 점을 반대로 옮기는 자리라 손으로 쓸 때 가장 잘 뒤집힌다.</summary>
    private static Mat AffineOf(CvPose p)
    {
        var rad = p.ThetaDeg * Math.PI / 180.0;
        var c = p.Scale * Math.Cos(rad);
        var s = p.Scale * Math.Sin(rad);
        var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, c);
        m.Set(0, 1, -s);
        m.Set(0, 2, p.FoundX - (c * p.OriginX - s * p.OriginY));
        m.Set(1, 0, s);
        m.Set(1, 1, c);
        m.Set(1, 2, p.FoundY - (s * p.OriginX + c * p.OriginY));
        return m;
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

}
