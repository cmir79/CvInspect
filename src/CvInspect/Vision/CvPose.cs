namespace CvInspect.Vision;

/// <summary>
/// 패턴 매칭 포즈 — 학습(티칭) 좌표를 런 좌표로 사상하는 2D 상사변환(회전+등방 스케일).
/// run = Scale·R(θ)·(p − Origin) + Found. θ 는 이미지 좌표(y 아래) atan2 규약 — +θ = atan2 각 증가 방향.
/// Scale 은 스케일 탐색 미사용 시 1 (강체변환과 동일).
/// 좌표공간 트리가 없는 환경에서 픽스처(탐색 영역 추종)의 등가물 — 티칭 기하(세그먼트/원 중심 등)에 적용한다.
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
}
