using System.Text.Json.Serialization;

namespace CvInspect.Vision.Opts;

/// <summary>검사 영역의 모양.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CvRegionShape
{
    /// <summary>회전 사각 — 일반 면적 판정.</summary>
    Rect,

    /// <summary>원환(링 밴드) — 고리형 대상(와셔 등). 가운데(볼트 머리 등 닮은 이물)를 안쪽 반경으로 배제.</summary>
    Ring,
}

/// <summary>
/// 공용 검사 영역 — 모양(<see cref="Shape"/>)과 그 기하를 한 덩어리로 갖는다.
/// 툴 파라미터가 이걸 품으면 영역 모양이 툴 설정이 된다 — 모양별로 검사를 따로 만들지 않는다.
/// 마스크/면적 산출은 <see cref="CvRegionMask"/>, 편집 도형은 표시 계층의 도형 바인더가 담당.
///
/// 좌표 공간은 소유 툴의 규약을 따른다 (예: 컬러 세그먼트 = 원본 공간). 기존 툴(블랍 등)의
/// 사각 탐색영역을 이걸로 바꾸려면 티칭 JSON 마이그레이션과 파인더의 마스크 인지가 필요하므로
/// 신설 툴부터 채택한다.
/// </summary>
public sealed class CvRegionOpt
{
    public CvRegionShape Shape { get; set; } = CvRegionShape.Rect;

    // 사각 (Shape=Rect) — 회전 그립 포함 도형 드래그로 편집.
    public double RectX { get; set; } = 100;
    public double RectY { get; set; } = 100;
    public double RectW { get; set; } = 160;
    public double RectH { get; set; } = 120;
    public double RectAngleDeg { get; set; }

    // 링 밴드 (Shape=Ring) — 링 도형 드래그로 편집(중심 이동 포함).
    public double CenterX { get; set; } = 200;
    public double CenterY { get; set; } = 200;
    public double RMinPx { get; set; } = 80;
    public double RMaxPx { get; set; } = 120;
}
