using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

/// <summary>
/// 이미지 전처리/에지 연산 — Mat 입출력만 다루는 순수·결정적 연산 (검증 하네스가 동일 입력으로 재현 가능).
/// </summary>
public static class CvImageOps
{
    /// <summary>전처리 — 잘라내기 → SampleX/Y 축소(면적 평균) → 미디언. 출력이 이후 툴들의 좌표 공간.</summary>
    public static Mat Preprocess(Mat gray, CvImageProcessOpt opt)
    {
        var crop = CropRectOf(gray, opt);
        using var src = new Mat(gray, crop);   // 잘라내지 않으면 전체 뷰라 복사 비용이 없다

        var sx = Math.Max(1, opt.SampleX);
        var sy = Math.Max(1, opt.SampleY);
        var w = Math.Max(1, src.Cols / sx);
        var h = Math.Max(1, src.Rows / sy);

        var pre = new Mat();
        if (w == src.Cols && h == src.Rows) src.CopyTo(pre);
        else Cv2.Resize(src, pre, new Size(w, h), 0, 0, InterpolationFlags.Area);

        if (opt.MedianKernel >= 3)
            Cv2.MedianBlur(pre, pre, opt.MedianKernel | 1);
        return pre;
    }

    /// <summary>
    /// 실제로 잘라 쓸 자리. 끄면 전체다. 켰는데 자리가 이미지 밖이거나 너무 작으면 전체로 돌린다 —
    /// 잘못된 자리로 검사를 이어 가는 것보다 낫고, 그냥 넘기면 크기 0 이미지로 터진다.
    /// </summary>
    public static Rect CropRectOf(Mat gray, CvImageProcessOpt opt)
    {
        var full = new Rect(0, 0, gray.Cols, gray.Rows);
        if (!opt.UseCrop) return full;

        var clipped = ClipRect(opt.CropX, opt.CropY, opt.CropW, opt.CropH, gray.Cols, gray.Rows);
        if (clipped is not { Width: >= 8, Height: >= 8 } r)
        {
            CvLog.Warn(nameof(CvImageOps),
                $"Crop region is out of bounds or too small ({opt.CropX:F0},{opt.CropY:F0} {opt.CropW:F0}x{opt.CropH:F0}) — using the full image.");
            return full;
        }
        return r;
    }

    /// <summary>
    /// 전처리 출력 공간 → 원본 공간 환산. 잘라내면 배율뿐 아니라 원점 이동까지 들어가므로
    /// <see cref="CvSpaceMap"/> 을 직접 만들지 말고 이걸 쓴다 — 오프셋을 빠뜨리면 그림만 어긋난다.
    /// </summary>
    public static CvSpaceMap MapOf(Mat gray, Mat pre, CvImageProcessOpt opt)
    {
        var crop = CropRectOf(gray, opt);
        return new CvSpaceMap(
            crop.Width / (double)pre.Cols,
            crop.Height / (double)pre.Rows,
            crop.X,
            crop.Y);
    }

    /// <summary>Sobel 에지 크기 이미지 — magnitude × scale 을 0~255 포화한 8bit.</summary>
    public static Mat SobelMagnitude(Mat pre, CvEdgeExtractOpt opt)
        => SobelMagnitude(pre, opt.MagnitudeScale);

    /// <summary>Sobel 에지 크기 이미지 (배율 직접 지정) — 에지(형상) 공간 매칭 등 옵션 객체 없는 경로용.
    /// 기본 0.25 = 강한 스텝 에지(≈1440)가 8bit 포화 근처에 오는 배율 — NCC 는 선형 배율 불변이라
    /// 배율은 포화 클리핑 억제 용도만.</summary>
    public static Mat SobelMagnitude(Mat pre, double magnitudeScale = 0.25)
    {
        using var gx = new Mat();
        using var gy = new Mat();
        Cv2.Sobel(pre, gx, MatType.CV_32F, 1, 0, 3);
        Cv2.Sobel(pre, gy, MatType.CV_32F, 0, 1, 3);
        using var mag = new Mat();
        Cv2.Magnitude(gx, gy, mag);

        var edge = new Mat();
        mag.ConvertTo(edge, MatType.CV_8U, magnitudeScale);
        return edge;
    }

    /// <summary>
    /// 에지 필터 — 이진화 + 연결요소로 유효 블랍만 남긴 이미지(에지 크기 값 유지).
    /// 유효 블랍이 하나도 없으면 null (검사 불능).
    ///
    /// <b>boundaries 는 채택 블랍 외곽선(입력 공간)인데, 속을 메우면 그 결과의 외곽선이다.</b>
    /// 메우기로 서로 이어진 블랍은 <b>한 덩어리로 합쳐져</b> 나오므로 개수가 채택 블랍 수와 다를 수 있다 —
    /// 화면 도형이나 개수 판정이 이 목록에 기대고 있으면 채움 토글에 따라 값이 달라진다.
    ///
    /// FillHoles 는 채택 블랍의 막힌 속을 255 로 채워 도넛형 대상을 면으로 남기고(원 에지 크기가 0 인
    /// 자리라 칠하지 않으면 검게 남는다), FillHolesAtBorder 는 이미지 가장자리를 벽으로 보아
    /// 가장자리에 잘려 트인 속(아치형)까지 같은 규칙에 넣는다. 켜고 끄면 평면이 달라지므로
    /// 이 평면을 쓰는 패턴은 재학습, 라인은 재티칭 대상이다.
    /// </summary>
    public static Mat? FilterEdgeBlobs(Mat edge, CvEdgeFilterOpt opt, out List<(double X, double Y)[]> boundaries)
    {
        boundaries = [];

        using var bin = new Mat();
        // −0.5: THRESH_BINARY 는 초과(>) 채택이라, 설명대로 "Threshold 이상(≥)" 픽셀이 잡히도록 반 LSB 내림.
        Cv2.Threshold(edge, bin, opt.Threshold - 0.5, 255, ThresholdTypes.Binary);

        // 영역 사용 시, 가장자리 걸침 메우기의 "벽"은 이미지가 아니라 영역 경계다 — 영역이 대상을
        // 잘라내는 통상 수단이라, 잘린 자리를 가장자리로 안 치면 옵션이 그 자리에서 무력해진다.
        Mat? fillWall = null;        // 255 = 영역 밖 (메우기에서 전경처럼 막는다)
        Point[]? fillSeeds = null;   // 바깥 판정 시드 = 영역 모서리(안쪽으로 살짝 들임)
        if (opt.UseRegion)
        {
            // 회전 사각 영역 — 폴리곤 마스크 (각도 0 이면 축 정렬과 동일). 영역이 이미지 밖이면
            // 마스크가 비어 블랍 0개 → null 로 자연 수렴.
            using var regionMask = new Mat(bin.Size(), MatType.CV_8UC1, Scalar.Black);
            var cx = opt.RegionX + opt.RegionW / 2.0;
            var cy = opt.RegionY + opt.RegionH / 2.0;
            var rad = opt.RegionAngleDeg * Math.PI / 180.0;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);
            var hw = opt.RegionW / 2.0;
            var hh = opt.RegionH / 2.0;
            var local = new[] { (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh) };
            var poly = new Point[4];
            for (var i = 0; i < 4; i++)
            {
                var (lx, ly) = local[i];
                poly[i] = new Point(
                    (int)Math.Round(cx + cos * lx - sin * ly),
                    (int)Math.Round(cy + sin * lx + cos * ly));
            }
            Cv2.FillConvexPoly(regionMask, poly, Scalar.White);
            Cv2.BitwiseAnd(bin, regionMask, bin);

            if (opt.FillHoles && opt.FillHolesAtBorder)
            {
                fillWall = new Mat();
                Cv2.BitwiseNot(regionMask, fillWall);
                fillSeeds = new Point[4];
                for (var i = 0; i < 4; i++)
                {
                    // 모서리를 중심 쪽으로 3px 들여 시드가 폴리곤 경계 반올림에 걸리지 않게 한다.
                    var dx = poly[i].X - cx;
                    var dy = poly[i].Y - cy;
                    var len = Math.Max(3.0, Math.Sqrt(dx * dx + dy * dy));
                    var t = 1.0 - 3.0 / len;
                    fillSeeds[i] = new Point(
                        Math.Clamp((int)Math.Round(cx + dx * t), 0, bin.Cols - 1),
                        Math.Clamp((int)Math.Round(cy + dy * t), 0, bin.Rows - 1));
                }
            }
        }

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids, PixelConnectivity.Connectivity8);

        var keep = new bool[n];
        var any = false;
        for (var i = 1; i < n; i++)
        {
            if (stats.At<int>(i, (int)ConnectedComponentsTypes.Area) < Math.Max(1, opt.MinPixels)) continue;
            keep[i] = true;
            any = true;
        }
        if (!any) { fillWall?.Dispose(); return null; }

        // 채택 라벨 → 255 마스크 — 라벨 이미지(CV_32S) 1 패스 (블랍 수 비례 전면 패스 회피).
        using var mask = new Mat(bin.Size(), MatType.CV_8UC1, Scalar.Black);
        {
            var cols = labels.Cols;
            var rows = labels.Rows;   // Mat 치수 프로퍼티는 P/Invoke — 루프 밖 캐시 (OCVS002)
            var rowLabels = new int[cols];
            var rowMask = new byte[cols];
            var labelStep = (nint)labels.Step();
            var maskStep = (nint)mask.Step();
            for (var y = 0; y < rows; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(labels.Data + y * labelStep, rowLabels, 0, cols);
                for (var x = 0; x < cols; x++)
                    rowMask[x] = keep[rowLabels[x]] ? (byte)255 : (byte)0;
                System.Runtime.InteropServices.Marshal.Copy(rowMask, 0, mask.Data + y * maskStep, cols);
            }
        }

        // 속 메우기 — 바깥에서 닿을 수 없는 배경을 메울 자리로 계산해 마스크에 합친다.
        // 외곽선(boundaries)·단계 표시도 메운 마스크 기준이 되어 도넛이 면으로 그려진다.
        Mat? holes = null;
        Mat? filtered = null;
        try
        {
            if (opt.FillHoles)
            {
                holes = ComputeMaskHoles(mask, opt.FillHolesAtBorder, fillWall, fillSeeds);
                Cv2.BitwiseOr(mask, holes, mask);
            }

            filtered = new Mat();
            Cv2.BitwiseAnd(edge, edge, filtered, mask);
            if (holes is not null)
            {
            // 메운 자리는 문턱 미달이라(그래서 배경으로 남은 것) 마스크만으로는 어둡게 남는다 —
            // 255 로 칠해 평면에서 형상이 면으로 서게 한다. 문턱 아래 잔결은 버려지고, 테두리 픽셀보다
            // 밝을 수 있지만 학습·검사가 같은 평면을 보므로 일관.
                filtered.SetTo(Scalar.All(255), holes);
            }

            Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            foreach (var c in contours)
                boundaries.Add(Array.ConvertAll(c, p => ((double)p.X, (double)p.Y)));

            var result = filtered;
            filtered = null;   // 성공 — 소유권을 호출자에게 넘긴다(아래 finally 가 해제하지 않게)
            return result;
        }
        finally
        {
            // 네이티브 Mat 은 GC 가 늦게 거둔다 — 중간에 던지면 프레임마다 샌다.
            fillWall?.Dispose();
            holes?.Dispose();
            filtered?.Dispose();
        }
    }

    /// <summary>
    /// 마스크에서 "메울 배경" 계산 — 바깥이 닿을 수 없는 배경이 메울 자리다. 반환 255 = 메울 자리.
    /// 기본은 바깥 컨투어 채움이라 사방이 막힌 속만 잡히고, 한 곳이라도 트인 자리는 그 통로째 배경으로
    /// 남는다(블랍 파인더의 갇힌 구멍 규칙과 동일). closeAtBorder 는 가장자리를 벽으로 본다 —
    /// 모서리 시드에서 4-연결로 닿을 수 있는 배경만 바깥으로 치므로, 가장자리에 다리를 붙이고 잘린
    /// 형상(아치)의 속은 바깥이 들어올 길이 없어 메울 자리가 된다. 4-연결인 것은 8-연결 전경의
    /// 대각 틈으로 바깥이 새어들지 않게 하기 위함. wall/seeds 를 주면 벽과 모서리가 그것(영역 경계·
    /// 영역 모서리)으로 바뀐다 — 영역으로 잘린 자리가 가장자리 역할을 한다.
    /// 안전장치 둘: 시드가 하나도 배경이 아니면(모서리 전부 전경) 바깥 판정 불능, 메울 자리가
    /// 배경의 9/10 을 넘으면(모서리가 고립 주머니에 갇혀 화면 전체가 뒤집히는 병리) 바깥 대표성
    /// 상실 — 둘 다 막힌 속 규칙으로 되돌아간다. 문턱이 9/10 인 것은 대상이 화면 대부분을 차지해
    /// 속이 배경 과반인 정상 장면(실기 확인 — 약 50%)을 병리로 오판하지 않기 위함.
    /// </summary>
    private static Mat ComputeMaskHoles(Mat mask, bool closeAtBorder, Mat? wall = null, Point[]? seeds = null)
    {
        var holes = new Mat();

        if (closeAtBorder)
        {
            using var work = mask.Clone();
            if (wall is not null) Cv2.BitwiseOr(work, wall, work);
            var w = work.Cols;
            var h = work.Rows;
            seeds ??= [new Point(0, 0), new Point(w - 1, 0), new Point(0, h - 1), new Point(w - 1, h - 1)];

            var bgTotal = (int)work.Total() - Cv2.CountNonZero(work);   // 벽 제외 배경 수
            var seeded = false;
            foreach (var s in seeds)
            {
                if (work.At<byte>(s.Y, s.X) != 0) continue;
                Cv2.FloodFill(work, s, Scalar.All(255));
                seeded = true;
            }
            if (seeded)
            {
                Cv2.Compare(work, 0, holes, CmpTypes.EQ);   // 바깥이 못 닿은 배경 = 메울 자리
                if ((long)Cv2.CountNonZero(holes) * 10 <= (long)bgTotal * 9)
                {
                    WarnBorderFallback(null);   // 정상 — 걸려 있던 경고 상태를 푼다
                    return holes;
                }
                // 배경 9/10 초과가 "메울 자리" — 모서리가 바깥을 대표하지 못한 것(고립 주머니 등).
                // <b>이 밸브는 성긴 판정이다</b>: 주머니가 배경의 10~50% 면 통과해 버린다. 면적만으로는
                // "큰 정상 속"과 "모서리가 갇힌 병리"를 가를 수 없어서인데, 가르는 지표를 실측 없이
                // 고르면 정상 장면을 막는 쪽으로 틀리기 쉽다. 지금은 사각지대를 알고 두는 상태다.
                WarnBorderFallback("corner seeds do not represent the outside (holes would cover most background)");
            }
            else
            {
                // 시드가 전부 전경 — 바깥을 짚을 자리가 없다. 종전에는 <b>아무 로그 없이</b> 규칙이
                // 바뀌어, 결과만 보고는 어느 규칙으로 채워졌는지 알 수 없었다(9/10 쪽만 로그가 있었다).
                WarnBorderFallback("all corner seeds are foreground — no outside to flood from");
            }
            // 바깥 판정 불능 — 아래 막힌 속 규칙으로.
        }

        // 바깥 컨투어 채움 — FindContours 는 입력을 보존하므로(위 boundaries 추출과 같은 전제) 사본 불필요.
        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        using (var filled = mask.Clone())
        {
            if (contours.Length > 0)
                Cv2.DrawContours(filled, contours, -1, Scalar.All(255), thickness: -1);
            Cv2.BitwiseXor(filled, mask, holes);   // 채움으로 새로 생긴 픽셀만
        }
        return holes;
    }

    /// <summary>가장자리 걸침 메우기가 <b>막힌 속 규칙으로 폴백</b>했음을 알린다 — 사유별로 <b>시간 억제</b>한다.
    /// 이 판정은 프레임마다 도는 검사 경로에 있어, 조건이 지속되면 같은 줄로 로그가 잠긴다.
    ///
    /// 억제를 <b>상태 전이</b>(폴백↔정상)로 하면 안 된다 — 이 클래스는 static 이라 상태가 검사 인스턴스
    /// 전체에 공유되고, 카메라가 여럿이면 한쪽이 정상일 때마다 다른 쪽의 억제가 풀려 <b>매 프레임 찍힌다</b>
    /// (억제가 통째로 무효가 되는 조합이다). 사유별 마지막 시각만 두면 인스턴스 수와 무관하게
    /// "같은 사유는 한동안 한 줄" 이 되고, 조건이 계속되면 주기적으로 다시 알려 살아 있음을 보인다.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _borderFallbackSeen = new();

    private const int BorderFallbackQuietSec = 60;

    private static void WarnBorderFallback(string? reason)
    {
        if (reason is null) return;   // 정상 경로 — 남의 억제를 풀지 않는다
        var now = DateTime.UtcNow;
        var last = _borderFallbackSeen.GetOrAdd(reason, DateTime.MinValue);
        if ((now - last).TotalSeconds < BorderFallbackQuietSec) return;
        _borderFallbackSeen[reason] = now;
        CvLog.Warn(nameof(CvImageOps),
            $"FillHolesAtBorder: {reason} — falling back to enclosed-hole rule.");
    }

    /// <summary>실수 rect 를 이미지 경계로 클립한 정수 Rect — 유효 영역이 없으면 null.</summary>
    public static Rect? ClipRect(double x, double y, double w, double h, int imgW, int imgH)
    {
        var x0 = Math.Max(0, (int)Math.Floor(x));
        var y0 = Math.Max(0, (int)Math.Floor(y));
        var x1 = Math.Min(imgW, (int)Math.Ceiling(x + w));
        var y1 = Math.Min(imgH, (int)Math.Ceiling(y + h));
        if (x1 - x0 < 1 || y1 - y0 < 1) return null;
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }
}
