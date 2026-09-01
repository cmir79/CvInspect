using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

/// <summary>
/// 공용 영역(<see cref="CvRegionOpt"/>) 마스크/면적 — 모양 분기를 여기 한곳에 모은다.
/// 마스크는 bbox 국소(255=영역 안), bbox 는 영역 경계를 이미지로 클립한 사각.
/// 면적은 <b>클리핑 전 기하 면적</b> — 영역이 화면 밖으로 걸치면 비율 분모로 쓸 때
/// 보이는 픽셀만 세어져 NG 쪽(안전 방향)으로 기운다.
///
/// 포즈 추종 판정용으로 변환된 기하(폴리곤/중심)를 직접 받는 오버로드도 함께 둔다 —
/// 강체 변환에서 사각은 폴리곤으로, 링은 중심만 옮기고 반경은 불변이다.
/// </summary>
public static class CvRegionMask
{
    /// <summary>티칭된 영역 그대로의 마스크 — 색 학습 등 포즈 무관 용도.</summary>
    public static (Rect Bbox, Mat? Mask) MaskOf(int imgW, int imgH, CvRegionOpt r)
        => r.Shape == CvRegionShape.Ring
            ? RingMask(imgW, imgH, r.CenterX, r.CenterY, r.RMinPx, r.RMaxPx)
            : PolyMask(imgW, imgH, RectPoly(r));

    /// <summary>티칭된 영역의 기하 면적(px²) — 비율 분모용.</summary>
    public static double GeometricArea(CvRegionOpt r)
        => r.Shape == CvRegionShape.Ring
            ? RingArea(r.RMinPx, r.RMaxPx)
            : PolyArea(RectPoly(r));

    /// <summary>사각(회전 포함) 영역 꼭짓점 — 포즈 변환의 출발점으로도 쓴다.</summary>
    public static (double X, double Y)[] RectPoly(CvRegionOpt r)
    {
        var cx = r.RectX + r.RectW / 2.0;
        var cy = r.RectY + r.RectH / 2.0;
        var rad = r.RectAngleDeg * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        Span<(double X, double Y)> local =
        [
            (-r.RectW / 2.0, -r.RectH / 2.0),
            (r.RectW / 2.0, -r.RectH / 2.0),
            (r.RectW / 2.0, r.RectH / 2.0),
            (-r.RectW / 2.0, r.RectH / 2.0),
        ];
        var poly = new (double X, double Y)[4];
        for (var i = 0; i < 4; i++)
            poly[i] = (cx + cos * local[i].X - sin * local[i].Y, cy + sin * local[i].X + cos * local[i].Y);
        return poly;
    }

    public static double RingArea(double rMinPx, double rMaxPx)
    {
        var rMin = Math.Min(rMinPx, rMaxPx);
        var rMax = Math.Max(rMinPx, rMaxPx);
        return Math.PI * (rMax * rMax - rMin * rMin);
    }

    /// <summary>폴리곤 기하 면적 — 신발끈 공식 (px²). 강체 변환에 불변.</summary>
    public static double PolyArea((double X, double Y)[] poly)
    {
        double s = 0;
        for (var i = 0; i < poly.Length; i++)
        {
            var (x1, y1) = poly[i];
            var (x2, y2) = poly[(i + 1) % poly.Length];
            s += x1 * y2 - x2 * y1;
        }
        return Math.Abs(s) / 2.0;
    }

    /// <summary>링 밴드 마스크 — 채운 바깥 원에서 안쪽 원을 파낸다. 유효 영역이 없으면 (default, null).</summary>
    public static (Rect Bbox, Mat? Mask) RingMask(int imgW, int imgH, double cx, double cy, double rMinPx, double rMaxPx)
    {
        var rMin = Math.Min(rMinPx, rMaxPx);
        var rMax = Math.Max(rMinPx, rMaxPx);
        if (rMax < 4 || rMax - rMin < 1) return (default, null);

        var bbox = new Rect(
            (int)Math.Floor(cx - rMax), (int)Math.Floor(cy - rMax),
            (int)Math.Ceiling(rMax * 2) + 1, (int)Math.Ceiling(rMax * 2) + 1);
        bbox = bbox.Intersect(new Rect(0, 0, imgW, imgH));
        if (bbox.Width < 2 || bbox.Height < 2) return (default, null);

        var mask = new Mat(bbox.Height, bbox.Width, MatType.CV_8UC1, Scalar.Black);
        var c = new Point((int)Math.Round(cx) - bbox.X, (int)Math.Round(cy) - bbox.Y);
        Cv2.Circle(mask, c, (int)Math.Round(rMax), Scalar.White, -1);
        if (rMin >= 1) Cv2.Circle(mask, c, (int)Math.Round(rMin), Scalar.Black, -1);
        return (bbox, mask);
    }

    /// <summary>폴리곤 마스크 — 무효면 (default, null).</summary>
    public static (Rect Bbox, Mat? Mask) PolyMask(int imgW, int imgH, (double X, double Y)[] poly)
    {
        var minX = (int)Math.Floor(poly.Min(c => c.X));
        var minY = (int)Math.Floor(poly.Min(c => c.Y));
        var maxX = (int)Math.Ceiling(poly.Max(c => c.X));
        var maxY = (int)Math.Ceiling(poly.Max(c => c.Y));
        var bbox = new Rect(minX, minY, maxX - minX, maxY - minY);
        bbox = bbox.Intersect(new Rect(0, 0, imgW, imgH));
        if (bbox.Width < 2 || bbox.Height < 2) return (default, null);

        var mask = new Mat(bbox.Height, bbox.Width, MatType.CV_8UC1, Scalar.Black);
        var pts = poly.Select(c => new Point((int)Math.Round(c.X) - bbox.X, (int)Math.Round(c.Y) - bbox.Y)).ToArray();
        Cv2.FillPoly(mask, [pts], Scalar.White);
        return (bbox, mask);
    }
}
