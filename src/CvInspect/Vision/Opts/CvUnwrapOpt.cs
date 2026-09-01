using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CvInspect.Vision.Opts;

/// <summary>
/// 극좌표 언랩 — 반경 밴드 [RMin, RMax] 를 360° + 오버랩으로 가로 전개 (각도=X, 반경=Y).
/// 밴드는 디스플레이 동심원 쌍(min/max) 그립으로 시각 편집. 중심은 편집 대상이 아니라
/// 선행 원 검출 툴에서 파생 — 표시용 중심은 CenterHook 으로 공급받는다 (런타임은 검출 중심 사용).
/// 오버랩: 이음새(0°)에 걸친 타겟 잘림 방지 — 앞쪽 OverlapDeg 만큼을 뒤에 이어붙임.
/// </summary>
public sealed class CvUnwrapOpt
{
    // 반경 밴드 — 디스플레이 도형(동심원 그립) 드래그로 편집 (PG 미노출 — 정적 스냅샷이라 실시간 미반영).
    [Browsable(false)] public double RMin { get; set; } = 40;
    [Browsable(false)] public double RMax { get; set; } = 120;

    /// <summary>편집 도형 중심 공급 훅 — 선행 원 툴의 기대 중심 (검사기가 배선). 없으면 (0,0).</summary>
    [Browsable(false)]
    [JsonIgnore]
    public Func<(double X, double Y)>? CenterHook { get; set; }

    [CvCategory("cv:CatUnwrap", 1)]
    [CvName("cv:OverlapDeg")]
    [CvDesc("cv:OverlapDegDesc")]
    public double OverlapDeg { get; set; } = 30;
}
