namespace CvInspect.Vision;

/// <summary>
/// 패턴 매칭 포즈 — 학습(티칭) 좌표를 런 좌표로 사상하는 2D 상사변환(회전+등방 스케일).
/// run = Scale·R(θ)·(p − Origin) + Found. θ 는 이미지 좌표(y 아래) atan2 규약 — +θ = atan2 각 증가 방향.
/// Scale 은 스케일 탐색 미사용 시 1 (강체변환과 동일).
/// 좌표공간 트리가 없는 환경에서 픽스처(탐색 영역 추종)의 등가물 — 티칭 기하(세그먼트/원 중심 등)에 적용한다.
/// 명시적 변환이라 <see cref="Inverse"/>(런 → 티칭)와 <see cref="Compose"/>(차례 적용)로 대수적으로 다룬다 —
/// 공간 트리처럼 암시적으로 쌓이지 않으므로 픽스처를 겹치는 것은 호출자가 합성해 넘긴다.
/// </summary>
public readonly record struct CvPose(double ThetaDeg, double OriginX, double OriginY, double FoundX, double FoundY, double Score, double Scale = 1.0)
{
    public (double X, double Y) Apply(double x, double y)
    {
        var rad = ThetaDeg * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        var dx = x - OriginX;
        var dy = y - OriginY;
        return (FoundX + Scale * (c * dx - s * dy), FoundY + Scale * (s * dx + c * dy));
    }

    /// <summary>역포즈 — 런 좌표를 티칭 좌표로. run = S·R(θ)·(p − O) + F 이므로 p = (1/S)·R(−θ)·(run − F) + O,
    /// 곧 θ' = −θ, Origin' = Found, Found' = Origin, Scale' = 1/Scale. Score 는 그대로다(같은 매칭에서 나온 값).</summary>
    public CvPose Inverse() => new(-ThetaDeg, FoundX, FoundY, OriginX, OriginY, Score, 1.0 / Scale);

    /// <summary>합성 — 이 포즈를 먼저 적용한 뒤 <paramref name="next"/> 를 적용하는 것과 같은 포즈 하나:
    /// 결과.Apply(p) == next.Apply(this.Apply(p)). 각도는 합, 스케일은 곱, 원점은 이쪽 것, 발견 위치는 이쪽 발견 위치를
    /// next 로 옮긴 자리. Score 는 둘 중 작은 값 — 합성된 픽스처는 약한 고리만큼만 믿을 수 있다.</summary>
    public CvPose Compose(CvPose next)
    {
        var found = next.Apply(FoundX, FoundY);
        return new CvPose(ThetaDeg + next.ThetaDeg, OriginX, OriginY, found.X, found.Y, Math.Min(Score, next.Score), Scale * next.Scale);
    }
}
