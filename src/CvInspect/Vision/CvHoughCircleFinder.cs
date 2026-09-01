using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

/// <summary>허프서클 검출 결과 — 중심/반경(입력 이미지 공간).</summary>
public readonly record struct CvHoughCircleHit(double X, double Y, double Radius);

/// <summary>
/// 허프서클 파인더 — 에지 그라디언트 투표 원 검출 (내부 Sobel+Canny). 밝기 문턱 없이 동작해
/// 제품-배경 밝기가 모호해도 원 에지만 있으면 잡는다. 탐색 반경 범위 = 기대 원(자체 티칭 도형)
/// 반경 ±허용% — 크기가 다른 배경 원은 후보에서 배제. 탐색 영역 제한 시 영역 내에서만.
/// </summary>
public static class CvHoughCircleFinder
{
    public static CvHoughCircleHit? Find(Mat img, CvHoughCircleOpt opt)
    {
        var expectedRadius = opt.Radius;
        if (expectedRadius < 3) return null;

        var roi = new Rect(0, 0, img.Cols, img.Rows);
        if (opt.UseSearchRegion)
        {
            var clip = CvImageOps.ClipRect(opt.SearchX, opt.SearchY, opt.SearchW, opt.SearchH, img.Cols, img.Rows);
            if (clip is null || clip.Value.Width < 4 || clip.Value.Height < 4) return null;
            roi = clip.Value;
        }

        using var view = img[roi];

        var tol = Math.Clamp(opt.RadiusTolPct, 1, 90) / 100.0;
        var minR = Math.Max(3, (int)Math.Floor(expectedRadius * (1 - tol)));
        var maxR = (int)Math.Ceiling(expectedRadius * (1 + tol));

        // minDist = 기대 반경 — 대상 원 1개 전제라 근접 후보는 병합, 최고 득표(첫 후보) 채택.
        var circles = Cv2.HoughCircles(view, HoughModes.Gradient, 1, Math.Max(10, expectedRadius),
            Math.Max(1, opt.CannyThreshold), Math.Max(1, opt.AccumThreshold), minR, maxR);
        if (circles.Length == 0) return null;

        var c = circles[0];
        return new CvHoughCircleHit(roi.X + c.Center.X, roi.Y + c.Center.Y, c.Radius);
    }
}
