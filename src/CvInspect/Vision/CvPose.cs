namespace CvInspect.Vision;

/// <summary>
/// 패턴 매칭 포즈 — 학습(티칭) 좌표를 런 좌표로 사상하는 2D 상사변환(회전+등방 스케일).
/// run = Scale·R(θ)·(p − Origin) + Found. θ 는 이미지 좌표(y 아래) atan2 규약 — +θ = atan2 각 증가 방향.
/// Scale 은 스케일 탐색 미사용 시 1 (강체변환과 동일).
/// 좌표공간 트리가 없는 환경에서 픽스처(탐색 영역 추종)의 등가물이다.
///
/// ⚠ <b>파인더가 낸 포즈를 티칭 기하에 바로 <see cref="Apply"/> 하지 않는다 — 학습각이 남아 있다.</b>
/// 파인더는 <see cref="ThetaDeg"/> 에 <b>절대 발견각</b>을 담고(학습각을 빼지 않는다), 티칭 기하를 옮기는 쪽은
/// 그 차(<c>ThetaDeg − TrainedAngleDeg</c>)를 써야 한다. 그 보정을 해 주는 것이
/// <see cref="CvInspGeom.XformByPose"/> 이므로 <b>티칭 기하를 런 좌표로 옮길 때는 그것을 쓴다.</b>
/// <see cref="Apply"/> 를 바로 태우면 학습각만큼 회전이 더 먹는데, 값은 그럴싸하게 나오고 어긋난 양만큼만
/// 틀린다 — 25° 로 티칭한 패턴에서 부품이 움직이지 않았는데도 43px 밀리는 식이다(학습각이 0 이면 둘이 같다).
/// 여기 <see cref="Apply"/>·<see cref="Inverse"/>·<see cref="Compose"/> 는 <b>순수 대수</b>라, 학습각 보정이
/// 끝난 포즈에 쓰는 것이 맞다.
/// 명시적 변환이라 <see cref="Inverse"/>(런 → 티칭)와 <see cref="Compose"/>(차례 적용)로 대수적으로 다룬다 —
/// 공간 트리처럼 암시적으로 쌓이지 않으므로 픽스처를 겹치는 것은 호출자가 합성해 넘긴다.
///
/// ⚠ <b>이것은 struct 라 <c>null</c> 이 될 수 없고, <c>default</c> 의 <see cref="Scale"/> 은 1 이 아니라 0 이다</b> —
/// 위 선언의 <c>Scale = 1.0</c> 은 생성자 기본 인자일 뿐 struct 의 기본값이 아니다. 그래서 빈 결과 목록에
/// <c>FirstOrDefault()</c> 를 쓰면 null 대신 <b>멀쩡해 보이는 0짜리 포즈</b>가 손에 들어오고, 그것으로 티칭 기하를
/// 옮기면 모든 좌표가 <c>(0,0)</c> 으로 접힌다 — 화면에는 도형이 그려지고 검사도 돌아서, 틀렸다는 신호가 없다.
/// <see cref="Inverse"/> 를 태우면 <c>Scale</c> 이 무한대가 되는데, 유한하지 않은 포즈로 탐색 좌표를 만들면
/// 경계 검사가 통과시켜 네이티브 접근 위반으로 프로세스가 죽는다.
/// 그래서 <see cref="Apply"/> 와 <see cref="Inverse"/> 는 그런 포즈를 받으면 <b>조용히 계산하지 않고 던진다</b>.
/// 목록에서 하나를 꺼내는 자리에서는 <c>FirstOrDefault()</c> 대신 <c>Count &gt; 0</c> 을 먼저 보거나
/// <see cref="IsValid"/> 로 확인한다.
/// </summary>
public readonly record struct CvPose(double ThetaDeg, double OriginX, double OriginY, double FoundX, double FoundY, double Score, double Scale = 1.0)
{
    /// <summary>변환에 쓸 수 있는 포즈인가 — 스케일이 유한한 양수이고 각도·좌표가 전부 유한한가.
    /// <c>default(CvPose)</c> 는 Scale 이 0 이라 false 다.</summary>
    public bool IsValid => double.IsFinite(Scale) && Scale > 0
        && double.IsFinite(ThetaDeg) && double.IsFinite(OriginX) && double.IsFinite(OriginY)
        && double.IsFinite(FoundX) && double.IsFinite(FoundY);

    public (double X, double Y) Apply(double x, double y)
    {
        // 접힌 좌표를 조용히 돌려주지 않는다 — default(CvPose) 로 옮긴 기하는 전부 (0,0) 이 되는데,
        // 그 자리에서 검사가 그대로 돌아 "찾았다" 를 세운다. 틀린 답보다 멈추는 편이 싸다.
        if (!IsValid) throw new InvalidOperationException(
            $"CvPose is not usable for a transform (Scale={Scale}). A default(CvPose) has Scale 0 — " +
            "check the result list is non-empty instead of using FirstOrDefault(), or test IsValid.");

        var rad = ThetaDeg * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        var dx = x - OriginX;
        var dy = y - OriginY;
        return (FoundX + Scale * (c * dx - s * dy), FoundY + Scale * (s * dx + c * dy));
    }

    /// <summary>역포즈 — 런 좌표를 티칭 좌표로. run = S·R(θ)·(p − O) + F 이므로 p = (1/S)·R(−θ)·(run − F) + O,
    /// 곧 θ' = −θ, Origin' = Found, Found' = Origin, Scale' = 1/Scale. Score 는 그대로다(같은 매칭에서 나온 값).
    /// 쓸 수 없는 포즈(<see cref="IsValid"/> 가 false)면 던진다 — 1/0 은 무한대 스케일을 만들고, 그 포즈로
    /// 탐색 좌표를 만들면 네이티브에서 프로세스가 죽는다.</summary>
    public CvPose Inverse()
    {
        if (!IsValid) throw new InvalidOperationException(
            $"CvPose cannot be inverted (Scale={Scale}). Inverting it would give an infinite scale, and a " +
            "non-finite pose used to build search coordinates takes the process down in native code.");

        return new(-ThetaDeg, FoundX, FoundY, OriginX, OriginY, Score, 1.0 / Scale);
    }

    /// <summary>합성 — 이 포즈를 먼저 적용한 뒤 <paramref name="next"/> 를 적용하는 것과 같은 포즈 하나:
    /// 결과.Apply(p) == next.Apply(this.Apply(p)). 각도는 합, 스케일은 곱, 원점은 이쪽 것, 발견 위치는 이쪽 발견 위치를
    /// next 로 옮긴 자리. Score 는 둘 중 작은 값 — 합성된 픽스처는 약한 고리만큼만 믿을 수 있다.</summary>
    public CvPose Compose(CvPose next)
    {
        var found = next.Apply(FoundX, FoundY);
        return new CvPose(ThetaDeg + next.ThetaDeg, OriginX, OriginY, found.X, found.Y, Math.Min(Score, next.Score), Scale * next.Scale);
    }
}
