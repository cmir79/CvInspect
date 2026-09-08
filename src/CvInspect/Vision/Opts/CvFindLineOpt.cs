using System.ComponentModel;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Vision.Opts;

/// <summary>
/// 라인 검출 — 탐색 세그먼트를 따라 캘리퍼(법선 프로파일 에지점) 배열 + 라인 피팅.
/// 세그먼트 방향(Start→End)이 라인 각도의 부호 기준 (피팅 방향을 세그먼트 방향으로 정렬).
/// </summary>
public sealed class CvFindLineOpt : ICvShapeSource
{
    // 탐색 세그먼트 — 디스플레이 도형(끝점 핸들) 드래그로 편집 (PG 미노출 — 정적 스냅샷이라 실시간 미반영).
    [Browsable(false)] public double StartX { get; set; } = 100;
    [Browsable(false)] public double StartY { get; set; } = 100;
    [Browsable(false)] public double EndX { get; set; } = 300;
    [Browsable(false)] public double EndY { get; set; } = 100;

    [CvCategory("cv:CatCaliper", 1)]
    [CvName("cv:NumCalipers")]
    [CvDesc("cv:NumCalipersDesc")]
    public int NumCalipers { get; set; } = 10;

    [CvCategory("cv:CatCaliper", 1)]
    [CvName("cv:SearchLength")]
    [CvDesc("cv:SearchLengthDesc")]
    public int SearchLength { get; set; } = 40;

    [CvCategory("cv:CatCaliper", 1)]
    [CvName("cv:ProjectionLength")]
    [CvDesc("cv:ProjectionLengthDesc")]
    public int ProjectionLength { get; set; } = 5;

    [CvCategory("cv:CatEdge", 2)]
    [CvName("cv:Polarity")]
    [CvDesc("cv:PolarityDesc")]
    public CvEdgePolarity Polarity { get; set; } = CvEdgePolarity.Either;

    [CvCategory("cv:CatEdge", 2)]
    [CvName("cv:ContrastThreshold")]
    [CvDesc("cv:ContrastThresholdDesc")]
    public double ContrastThreshold { get; set; } = 10;

    [CvCategory("cv:CatEdge", 2)]
    [CvName("cv:EdgeSelect")]
    [CvDesc("cv:EdgeSelectDesc")]
    public CvEdgeSelect EdgeSelect { get; set; } = CvEdgeSelect.Best;

    [CvCategory("cv:CatFit", 3)]
    [CvName("cv:UseRefit")]
    [CvDesc("cv:UseRefitDesc")]
    public bool UseRefit { get; set; }

    [CvCategory("cv:CatQuality", 4)]
    [CvName("cv:UseAngleGate")]
    [CvDesc("cv:UseAngleGateDesc")]
    public bool UseAngleGate { get; set; }

    [CvCategory("cv:CatQuality", 4)]
    [CvName("cv:MaxAngleDevDeg")]
    [CvDesc("cv:MaxAngleDevDegDesc")]
    public double MaxAngleDevDeg { get; set; } = 10.0;

    [CvCategory("cv:CatQuality", 4)]
    [CvName("cv:UseRmsGate")]
    [CvDesc("cv:UseRmsGateDesc")]
    public bool UseRmsGate { get; set; }

    [CvCategory("cv:CatQuality", 4)]
    [CvName("cv:MaxRmsPx")]
    [CvDesc("cv:MaxRmsPxDesc")]
    public double MaxRmsPx { get; set; } = 2.0;

    [CvCategory("cv:CatFit", 3)]
    [CvName("cv:NumToIgnore")]
    [CvDesc("cv:NumToIgnoreDesc")]
    public int NumToIgnore { get; set; }

    /// <summary>편집 도형 — 탐색 선분(이동 + 양 끝점).</summary>
    public IReadOnlyList<CvEditShape>? CreateShapes(Action onEdited)
    {
        var seg = new CvEditSeg { Color = ViOverlayColor.Yellow, Label = "Line" };
        seg.Set(StartX, StartY, EndX, EndY);
        seg.Changed += (_, _) =>
        {
            StartX = seg.X1;
            StartY = seg.Y1;
            EndX = seg.X2;
            EndY = seg.Y2;
            onEdited();
        };
        return [seg];
    }
}
