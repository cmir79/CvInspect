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

    /// <summary>피팅에 남아야 하는 최소 검출점 수. 0 = 끔(종전 동작 — 하한은 3점).
    ///
    /// <b>잔차 게이트만으로는 증거 부족을 걸러내지 못한다 — 오히려 반대로 움직인다.</b> 점이 줄수록 남은 점들은
    /// 거의 공선이 되어 잔차가 <b>좋아지기</b> 때문이다. 실측(캘리퍼 12개, 티칭 세그먼트 360px): 에지가 전 구간에
    /// 있으면 12점·rms 0.000, 100px 에만 있으면 <b>3점·rms 0.000 인데 각도가 9.46° 틀어진 채</b> 잔차·각도 게이트를
    /// 둘 다 통과한다. 곧 "대상의 일부만 보고 잰 값" 과 "제대로 본 값" 이 품질 지표로 구별되지 않는다.
    /// 그것을 가르는 값은 잔차가 아니라 <b>몇 개가 살아남았는가</b>(<c>CvLineFit.PointCount</c>)뿐이다.
    ///
    /// 0 이 기본인 이유는 가동 중인 설비의 판정을 말없이 바꾸지 않기 위해서다 — 켜는 순간 종전에 통과하던
    /// 부분 검출이 미검출이 된다. 캘리퍼 수의 절반 남짓부터 시작해 현장 값으로 조인다.</summary>
    [CvCategory("cv:CatQuality", 4)]
    [CvName("cv:MinPoints")]
    [CvDesc("cv:MinPointsDesc")]
    public int MinPoints { get; set; }

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
