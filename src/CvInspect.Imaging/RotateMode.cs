using System.Text.Json.Serialization;

namespace CvInspect.Imaging;

// 값 = 각도(90/180/270) — 정수로 저장된 구판 설정도 자명하게 읽힌다. 신판 직렬화는 식별자 문자열.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RotateMode
{
    None = 0,
    Rotate90 = 90,
    Rotate180 = 180,
    Rotate270 = 270,
}
