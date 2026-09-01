namespace CvInspect.Vision;

/// <summary>
/// 축소(툴) 공간 → 원본(오버레이) 공간 환산 — 환산은 반드시 이 타입 경유 (누락 방지 단일 지점).
///
/// 전처리가 잘라 내기까지 하면 배율만으로는 못 맞춘다. 잘라 낸 만큼 원점이 옮겨가 있어
/// 배율만 곱하면 그 오프셋만큼 어긋난 자리에 그려진다 — 값은 맞는데 그림만 틀려 알아채기 어렵다.
/// 그래서 <see cref="Ox"/>·<see cref="Oy"/> 를 함께 갖는다. 크롭이 없으면 0 이라 종전과 같다.
///
/// 직접 만들지 말고 <c>CvImageOps.MapOf</c> 를 쓴다 — 잘라 낸 자리를 아는 것은 전처리 쪽이다.
/// </summary>
public readonly record struct CvSpaceMap(double Sx, double Sy, double Ox = 0, double Oy = 0)
{
    /// <summary>환산 없음 — 툴이 원본 해상도에서 직접 도는 검사용.</summary>
    public static CvSpaceMap Identity => new(1.0, 1.0);

    public (double X, double Y) Apply(double x, double y) => (x * Sx + Ox, y * Sy + Oy);
}
