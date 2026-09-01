using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

/// <summary>
/// 패턴 학습의 공통 조각 — 학습영역 모양(<see cref="CvTrainShape"/>)에 매인 두 가지만 모은다.
/// 검사마다 학습 루틴이 따로 있어(툴 구성·입력 슬롯이 검사별로 다르다) 루틴 자체는 검사가 갖고,
/// 모양이 정하는 크롭과 무특징 판정만 여기서 한 벌로 관리한다 — 검사기마다 복제하면 원형 학습의
/// 규약(외접 사각·내접원 가드·학습각 0)이 조용히 갈린다.
/// </summary>
public static class CvPatternTeach
{
    /// <summary>학습 성립 최소 변 길이(px) — 이보다 작으면 표본이 못 된다.</summary>
    public const int MinSpanPx = 8;

    /// <summary>
    /// 원형 학습영역 크롭 — 외접 사각을 서브픽셀 중심으로 뽑는다. 모서리에 남는 배경은
    /// 매칭 마스크(내접원)가 제외하므로 무해하다. 원은 각도 개념이 없어 학습각은 0 으로 저장한다.
    /// 지름이 <see cref="MinSpanPx"/> 미만이거나 외접 사각이 이미지 밖으로 걸치면 null —
    /// GetRectSubPix 는 화면 밖을 가장자리 픽셀 복제로 채우므로, 막지 않으면 실제로는 없는
    /// 줄무늬 내용으로 '학습 성공'이 되어 버린다.
    /// </summary>
    public static Mat? CropCircle(Mat img, CvPatternOpt pat, out double originX, out double originY)
    {
        originX = pat.TrainCircleX;
        originY = pat.TrainCircleY;

        var d = (int)Math.Round(pat.TrainCircleR * 2);
        if (d < MinSpanPx) return null;

        var half = d / 2.0;
        if (originX - half < 0 || originY - half < 0 || originX + half > img.Cols || originY + half > img.Rows)
            return null;

        var template = new Mat();
        Cv2.GetRectSubPix(img, new Size(d, d), new Point2f((float)originX, (float)originY), template);
        return template;
    }

    /// <summary>축 정렬 학습영역 크롭 — 잘라낸 조각과 그 <b>원점</b>(픽셀 중심 규약)을 함께 돌려준다.
    /// 실패(영역이 이미지 밖 / 변이 <see cref="MinSpanPx"/> 미만)면 null.
    ///
    /// <b>원점이 (변−1)/2 인 것이 이 함수의 요점이다.</b> 잘라낸 조각의 픽셀 i 는 이미지 roi.X+i 이므로
    /// 픽셀 중심 규약에서 그 조각의 중심은 roi.X+(W−1)/2 다. W/2 로 적으면 매칭이 돌려주는 자리
    /// (loc+(W−1)/2)와 정확히 반 픽셀 어긋나, 부품이 안 움직였는데 픽스처가 밀린다. 원형(<see cref="CropCircle"/>)과
    /// 회전 사각(호스트가 GetRectSubPix)이 이미 같은 규약이라, 축 정렬만 각 호스트에 복제돼 있으면
    /// 그 자리에서 갈린다 — 실제로 네 저장소 전부 그랬다.</summary>
    public static Mat? CropRect(Mat img, double x, double y, double w, double h, out double originX, out double originY)
    {
        originX = originY = 0;
        var roi = CvImageOps.ClipRect(x, y, w, h, img.Cols, img.Rows);
        if (roi is null || roi.Value.Width < MinSpanPx || roi.Value.Height < MinSpanPx) return null;

        originX = roi.Value.X + (roi.Value.Width - 1) / 2.0;
        originY = roi.Value.Y + (roi.Value.Height - 1) / 2.0;
        return img[roi.Value].Clone();
    }

    /// <summary>
    /// 무특징(빈 영역) 학습 거부 판정 — 실매칭 영역(사각=전면 / 원=내접원)의 밝기 표준편차 ≥ 1.
    /// CCoeffNormed 는 상수 템플릿에서 전면 1.0 허위 매치가 되므로 티칭 시점에 차단한다.
    /// 원형을 사각 전면으로 재면 원 밖 모서리의 에지가 가드를 통과시켜, 정작 매칭되는 원 안이
    /// 균일한 무특징 학습이 성립해 버린다. 마스크는 매칭이 쓰는 것과 같은 내접원
    /// (<see cref="CvPatternFinder"/> 의 것) — 학습 가드와 매칭 가드가 같은 영역을 본다.
    /// </summary>
    public static bool HasFeature(Mat template, CvTrainShape shape)
    {
        Scalar std;
        if (shape == CvTrainShape.Circle)
        {
            using var mask = new Mat(template.Rows, template.Cols, MatType.CV_8UC1, Scalar.Black);
            CvPatternFinder.FillInscribedEllipse(mask, template.Cols, template.Rows);
            Cv2.MeanStdDev(template, out _, out std, mask);
        }
        else
        {
            Cv2.MeanStdDev(template, out _, out std);
        }
        return std.Val0 >= 1.0;
    }
}
