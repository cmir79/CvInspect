using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Vision.Opts;

/// <summary>
/// 극좌표 언랩 — 반경 밴드 [RMin, RMax] 를 360° + 오버랩으로 가로 전개 (각도=X, 반경=Y).
/// 밴드는 디스플레이 동심원 쌍(min/max) 그립으로 시각 편집. 중심은 편집 대상이 아니라
/// 선행 원 검출 툴에서 파생 — 표시용 중심은 CenterHook 으로 공급받는다 (런타임은 검출 중심 사용).
/// 오버랩: 이음새(0°)에 걸친 타겟 잘림 방지 — 앞쪽 OverlapDeg 만큼을 뒤에 이어붙임.
/// </summary>
public sealed class CvUnwrapOpt : INotifyPropertyChanged, ICvShapeSource
{
    /// <summary>그립이 쓰는 반경 밴드(<see cref="RMin"/>·<see cref="RMax"/>)만 통지한다 — 이 값을 보여 주는 편집기가
    /// 드래그를 따라오게 하려는 것이다. 나머지는 편집기에서 고치는 값이라 편집기가 이미 안다.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private double _rMin = 40;
    private double _rMax = 120;

    // 반경 밴드 — 디스플레이 도형(동심원 그립) 드래그로 편집 (PG 미노출).
    [Browsable(false)] public double RMin { get => _rMin; set => SetField(ref _rMin, value); }
    [Browsable(false)] public double RMax { get => _rMax; set => SetField(ref _rMax, value); }

    /// <summary>편집 도형 중심 공급 훅 — 선행 원 툴의 기대 중심 (검사기가 배선). 없으면 (0,0).</summary>
    [Browsable(false)]
    [JsonIgnore]
    public Func<(double X, double Y)>? CenterHook { get; set; }

    [CvCategory("cv:CatUnwrap", 1)]
    [CvName("cv:OverlapDeg")]
    [CvDesc("cv:OverlapDegDesc")]
    public double OverlapDeg { get; set; } = 30;

    /// <summary>편집 도형 — 반경 밴드(min/max)만 그립 편집. 중심은 선행 원 툴 파생(이동 불가)이라 <see cref="CenterHook"/> 이 준다.</summary>
    public IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited)
    {
        var (cx, cy) = CenterHook?.Invoke() ?? (0, 0);
        var ring = new CvEditRing { Color = ViOverlayColor.Teal, Label = "Band" };
        ring.Set(cx, cy, RMin, RMax);
        ring.Changed += (_, _) =>
        {
            RMin = ring.RMin;
            RMax = ring.RMax;
            onEdited();
        };
        return [ring];
    }

    private void SetField(ref double field, double value, [CallerMemberName] string? name = null)
    {
        if (field.Equals(value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
