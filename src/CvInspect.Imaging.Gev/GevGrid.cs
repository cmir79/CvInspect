namespace CvInspect.Imaging.Gev;

/// <summary>
/// 카메라가 받아 주는 값은 연속이 아니라 <b>일정 간격의 격자</b>다. 격자에서 벗어난 값은 거절되는데,
/// 사람이 넣는 값(설정 파일의 노출 시간 등)이 그 간격에 맞을 이유가 없다.
///
/// 거절만 하고 끝내면 카메라는 <b>이전 값 그대로 돌아간다</b> — 밝기만 틀린 채 예외도 없이 검사가 돈다.
/// 그래서 가장 가까운 격자 값으로 맞춰 다시 쓰되, <b>조용히 바꾸지 않고 무엇을 대신 썼는지 알린다.</b>
///
/// ⚠ <b>격자를 아는 것은 정수 노드뿐이고, 그 단위가 우리 단위(us)라는 보장은 없다.</b> 시간 노드는 대개
/// "정수 배수 × 시간 기준" 구조라, 기준이 1us 인 장치에서는 두 단위가 같지만 기준이 다르면 어긋난다
/// (실측한 장치는 기준이 1us 라 그대로 맞았다 — 그래서 이 일치를 규칙으로 믿으면 안 된다).
/// 그러니 이 계산은 <b>단정이 아니라 재시도용</b>이고, 실제로 적용된 값은 쓰고 나서 되읽어 확인한다.
/// </summary>
public static class GevGrid
{
    /// <summary>
    /// <paramref name="value"/> 에 가장 가까운 격자 값. 격자는 <paramref name="anchor"/> 에서 시작해
    /// <paramref name="increment"/> 간격이다. 간격이 없으면(1 이하) 맞출 것이 없으므로 null.
    /// </summary>
    public static double? Snap(double value, long anchor, long increment)
    {
        if (increment <= 1) return null;
        if (double.IsNaN(value) || double.IsInfinity(value)) return null;
        if (value <= anchor) return anchor;

        var steps = Math.Round((value - anchor) / increment, MidpointRounding.AwayFromZero);

        // 격자 밖으로 넘어갈 만큼 큰 값이면 맞출 수 없다 — 상한은 여기서 알 수 없으므로 장치가 판정하게 둔다.
        var snapped = anchor + steps * increment;
        return double.IsFinite(snapped) ? snapped : null;
    }
}
