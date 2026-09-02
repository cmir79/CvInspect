using System.Text.Json.Serialization;

namespace CvInspect.Imaging;

// 직렬화는 식별자 문자열 — 멤버 삽입·재배열에도 저장된 설정이 다른 의미로 읽히지 않게 한다 (구판 정수도 역직렬화됨).
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlipMode
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
    Both = 3,
}
