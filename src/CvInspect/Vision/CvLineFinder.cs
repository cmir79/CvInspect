using OpenCvSharp;
using CvInspect.Vision.Opts;

namespace CvInspect.Vision;

/// <summary>라인 피팅 결과 — 각도(atan2, deg)와 오버레이용 피팅 세그먼트(입력 이미지 공간).
/// <paramref name="RmsPx"/> 는 아웃라이어 제외 후 최종 피팅의 잔차 RMS(수직거리, 입력 이미지 픽셀) —
/// 피팅 품질 지표다. 검출점이 한 직선에 잘 놓였으면 작고, 캘리퍼가 서로 다른 구조(꼭짓점 등)를
/// 물면 커진다. 후보 라인이 여럿일 때 어느 쪽 데이터를 쓸지 고르는 근거로 쓴다.</summary>
public readonly record struct CvLineFit(double AngleDeg, double StartX, double StartY, double EndX, double EndY, int PointCount, double RmsPx, double AngleDevDeg);

/// <summary>
/// 라인 파인더 — 기대 세그먼트를 따라 캘리퍼(CvCaliper.DetectEdge) 배열을 세우고 검출점에 PCA 라인 피팅.
/// +법선 = 세그먼트 방향(Start→End)을 atan2 +90° 회전한 방향 — 극성 기준.
/// </summary>
public static class CvLineFinder
{
    /// <summary>
    /// 위치 편차 대응 검출 2패스 — 티칭 세그먼트로 1차 피팅 후, 피팅 세그먼트에 캘리퍼를 재배치해 한 번 더.
    /// 실제 라인이 티칭 세그먼트에서 평행이동/회전해 있으면 1차 캘리퍼가 에지를 비스듬히 만나
    /// 서브픽셀이 왜곡되는데, 2차에서 법선이 에지와 직교 정렬되며 수렴. 재피팅 실패 시 1차 결과 유지.
    /// (CvCircleFinder.FindWithRefit 와 동일 규약. 탐색 길이 밖 대편차는 선행 패턴 포즈 추종으로.)
    /// </summary>
    public static CvLineFit? FindWithRefit(Mat img, double sx, double sy, double ex, double ey, CvFindLineOpt opt)
    {
        var fit = FindOnce(img, sx, sy, ex, ey, opt);
        if (fit is null) return null;

        var f = fit.Value;
        return Gate(WithDev(FindOnce(img, f.StartX, f.StartY, f.EndX, f.EndY, opt) ?? fit, sx, sy, ex, ey), opt);
    }

    /// <summary>
    /// 품질 게이트 — 잔차가 임계를 넘으면 미검출로 돌린다. <b>최종 결과에만 한 번</b> 적용한다:
    /// 재피팅은 "1차가 비스듬해 왜곡될 수 있다"는 전제로 있는 기능이라, 1차 잔차로 미리 자르면
    /// 스스로 고칠 기회를 뺏는다. 2차까지 가고도 잔차가 크면 그때는 값을 믿을 수 없다.
    /// </summary>
    private static CvLineFit? Gate(CvLineFit? fit, CvFindLineOpt opt)
    {
        if (fit is not { } f) return null;
        if (opt.UseRmsGate && f.RmsPx > Math.Abs(opt.MaxRmsPx)) return null;
        // 기대각 게이트 — 검출 라인이 입력 세그먼트 방향에서 크게 벗어나면 다른 구조를 잡은 것이다.
        // (세그먼트가 이미 포즈 변환된 자리라면 그 방향이 곧 "제품이 돌아간 만큼" 기대되는 라인 방향이다.)
        if (opt.UseAngleGate && f.AngleDevDeg > Math.Abs(opt.MaxAngleDevDeg)) return null;
        return f;
    }

    /// <summary>
    /// 캘리퍼 라인 검출 — 세그먼트를 따라 NumCalipers 개 법선 프로파일에서 에지점을 찾고 라인 피팅.
    /// 에지점 부족(&lt;3) 또는 NumToIgnore 를 채울 수 없으면 null (조용한 미적용 금지).
    /// </summary>
    public static CvLineFit? Find(Mat img, double sx, double sy, double ex, double ey, CvFindLineOpt opt)
    {
        var fit = FindOnce(img, sx, sy, ex, ey, opt);
        if (fit is null) return null;

        // 옵션 재피팅 — FindWithRefit 과 같은 2패스를 티칭 설정으로 켜는 경로.
        // 2패스가 검출에 실패하면 1패스 결과를 유지한다(기존 규약).
        if (opt.UseRefit)
        {
            var f = fit.Value;
            fit = FindOnce(img, f.StartX, f.StartY, f.EndX, f.EndY, opt) ?? fit;
        }
        return Gate(WithDev(fit, sx, sy, ex, ey), opt);
    }

    /// <summary>
    /// 기대각 편차 확정 — 기준은 <b>최초 입력 세그먼트</b>의 방향이다. 재피팅 2패스는 1차 결과를
    /// 입력으로 받으므로 그때 다시 재면 편차가 0 에 수렴해 게이트가 무의미해진다.
    /// 무방향 라인 규약으로 접어 (0, 90] 범위 — 180° 뒤집힌 검출은 편차 0 으로 본다.
    ///
    /// 각도는 <b>입력 이미지 공간</b>에서 잰다 — 세그먼트·캘리퍼·검출선이 모두 그 공간에 있으므로
    /// "놓은 방향에서 벗어났나"는 여기서 정확히 판정된다(축소 배율과 무관).
    /// 다만 이것은 <b>제품이 기준각에서 얼마나 돌았나</b>(물리 각도)와는 다른 값이다: 비등방 축소에서는
    /// 원본의 강체 회전이 전단을 동반해 이 공간의 각과 원본 공간의 각이 갈린다. 물리 각도가 필요한
    /// 호출측(포즈 추종 검사 등)은 SpaceMap 으로 환산해 따로 재야 하고, 이 값은 그 대체물이 아니다.
    /// </summary>
    private static CvLineFit? WithDev(CvLineFit? fit, double sx, double sy, double ex, double ey)
    {
        if (fit is not { } f) return null;

        var expect = Math.Atan2(ey - sy, ex - sx) * 180.0 / Math.PI;
        var d = (f.AngleDeg - expect) % 180.0;
        if (d > 90.0) d -= 180.0;
        else if (d <= -90.0) d += 180.0;
        return f with { AngleDevDeg = Math.Abs(d) };
    }

    /// <summary>단일 패스 검출 — 재피팅 없이 주어진 세그먼트에서 한 번만 캘리퍼를 세운다.</summary>
    private static CvLineFit? FindOnce(Mat img, double sx, double sy, double ex, double ey, CvFindLineOpt opt)
    {
        var dxv = ex - sx;
        var dyv = ey - sy;
        var len = Math.Sqrt(dxv * dxv + dyv * dyv);
        if (len < 2) return null;

        var dirX = dxv / len;
        var dirY = dyv / len;
        var nX = -dirY;   // +법선 — 방향을 atan2 +90° 회전
        var nY = dirX;

        var count = Math.Max(3, opt.NumCalipers);

        var pts = new List<(double X, double Y)>();
        for (var i = 0; i < count; i++)
        {
            var t = (i + 0.5) / count;
            var cx = sx + dxv * t;
            var cy = sy + dyv * t;

            var hit = CvCaliper.DetectEdge(img, cx, cy, nX, nY, dirX, dirY,
                opt.SearchLength, opt.ProjectionLength, opt.Polarity, opt.ContrastThreshold, opt.EdgeSelect);
            if (hit is null) continue;

            pts.Add((cx + nX * hit.Value.S, cy + nY * hit.Value.S));
        }

        if (pts.Count < 3) return null;
        // 아웃라이어 제거 요청을 채울 수 없으면 검출 실패 — 조용한 미적용(노이즈 포함 피팅)이
        // 잘못된 각도를 OK 로 내보내는 것 방지.
        if (opt.NumToIgnore > 0 && pts.Count - opt.NumToIgnore < 3) return null;

        var (mx, my, ux, uy) = CvFit.Line(pts);

        // 아웃라이어 제외 후 재피팅 — 잔차(수직 거리) 큰 점부터 NumToIgnore 개
        if (opt.NumToIgnore > 0)
        {
            pts = pts
                .OrderBy(p => Math.Abs(-uy * (p.X - mx) + ux * (p.Y - my)))
                .Take(pts.Count - opt.NumToIgnore)
                .ToList();
            (mx, my, ux, uy) = CvFit.Line(pts);
        }

        // 피팅 방향을 세그먼트 방향(Start→End)으로 정렬 — 각도 부호 기준 유지
        if (ux * dirX + uy * dirY < 0) { ux = -ux; uy = -uy; }
        var angle = Math.Atan2(uy, ux) * 180.0 / Math.PI;

        // 피팅 품질 — 최종 점집합의 수직거리 RMS (방향 부호는 제곱이라 무관).
        // 아웃라이어 제외 후 값이라 "남긴 점들이 얼마나 한 직선에 놓였나"를 뜻한다.
        double sqSum = 0;
        foreach (var (px, py) in pts)
        {
            var d = -uy * (px - mx) + ux * (py - my);
            sqSum += d * d;
        }
        var rms = Math.Sqrt(sqSum / pts.Count);

        // 오버레이 세그먼트 — 세그먼트 양 끝을 피팅 라인에 투영
        var (p1X, p1Y) = CvFit.ProjectOnLine(sx, sy, mx, my, ux, uy);
        var (p2X, p2Y) = CvFit.ProjectOnLine(ex, ey, mx, my, ux, uy);
        // AngleDevDeg 는 최초 세그먼트를 아는 호출측(Find/FindWithRefit)이 WithDev 로 확정한다.
        return new CvLineFit(angle, p1X, p1Y, p2X, p2Y, pts.Count, rms, 0);
    }
}
