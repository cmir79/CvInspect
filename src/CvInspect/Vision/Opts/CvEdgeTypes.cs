using System.Text.Json.Serialization;

namespace CvInspect.Vision.Opts;

// 에지 검출 공용 enum — 캘리퍼 기반 파인더(라인/서클)의 극성·선택 규약.
// 직렬화는 식별자 문자열 — 멤버 삽입·재배열에도 저장된 레시피가 다른 설정으로 읽히지 않게 한다.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CvEdgePolarity
{
    /// <summary>양극성 — 밝기 변화 방향 무관 최강 에지.</summary>
    Either,

    /// <summary>+법선 방향으로 어두움→밝음.</summary>
    DarkToLight,

    /// <summary>+법선 방향으로 밝음→어두움.</summary>
    LightToDark,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CvEdgeSelect
{
    /// <summary>대비 최강 에지 — 탐색 범위에 비슷한 에지가 여럿이면 캘리퍼마다 다른 에지를 잡아 흔들릴 수 있음.</summary>
    Best,

    /// <summary>−법선 쪽에서 처음 만나는 에지 — 평행 에지(밴드 양변 등) 환경에서 위치 안정.</summary>
    First,

    /// <summary>+법선 쪽에서 처음 만나는(마지막) 에지.</summary>
    Last,
}
