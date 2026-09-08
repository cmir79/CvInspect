using OpenCvSharp;

namespace CvInspect.Demo;

/// <summary>
/// 합성 부품 이미지 — 어두운 배경 위의 밝은 판, 판 가운데의 어두운 구멍, 판 왼쪽 위의 어두운 "L" 표식.
/// 카메라 없이도 라인 파인더(판의 윗변)·원 파인더(구멍)·블랍(구멍)·패턴 파인더(L 표식)가 잡을 것이 있게 만든다.
/// L 은 회전 대칭을 깨는 특징이다 — 사각 판 + 가운데 구멍만으로는 180° 돌린 부품을 구분할 수 없다.
/// 판은 (dx, dy, angleDeg)로 옮기고 돌릴 수 있다: 픽스처 데모가 "움직이는 부품을 툴이 따라가는가" 를 보는 데 쓴다.
/// 기하가 상수라 검사 결과를 수치로 검증할 수 있다.
/// </summary>
public static class DemoImage
{
    public const int Width = 640;
    public const int Height = 480;

    public const int PlateX = 120;
    public const int PlateY = 110;
    public const int PlateW = 400;
    public const int PlateH = 260;

    public const int HoleCenterX = 320;
    public const int HoleCenterY = 240;
    public const int HoleRadius = 45;

    /// <summary>L 표식 — 판 왼쪽 위 안쪽. 가로 팔·세로 팔 두 막대.</summary>
    public const int KeyX = PlateX + 34;
    public const int KeyY = PlateY + 34;
    public const int KeyArm = 44;
    public const int KeyBar = 12;

    /// <summary>회전 중심 — 판의 중심. 각도는 이미지 좌표(y 아래) atan2 규약(+ = 시계 방향으로 보임).</summary>
    public static double PivotX => PlateX + PlateW / 2.0;
    public static double PivotY => PlateY + PlateH / 2.0;

    /// <summary>기준(무이동·무회전) 좌표의 점을 (dx, dy, angleDeg)만큼 옮긴 부품 위의 자리로 — 검사 결과 검증용.</summary>
    public static (double X, double Y) Map(double x, double y, double dx, double dy, double angleDeg)
    {
        var rad = angleDeg * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        var rx = x - PivotX;
        var ry = y - PivotY;
        return (PivotX + dx + rx * c - ry * s, PivotY + dy + rx * s + ry * c);
    }

    /// <summary>8비트 1채널. 살짝 흐려 에지가 서브픽셀로 잡히게 한다(계단 에지는 캘리퍼가 정수 위치에만 앉는다).</summary>
    public static Mat Create(double dx = 0, double dy = 0, double angleDeg = 0)
    {
        var img = new Mat(Height, Width, MatType.CV_8UC1, new Scalar(60));
        Cv2.FillConvexPoly(img, Quad(PlateX, PlateY, PlateW, PlateH, dx, dy, angleDeg), new Scalar(200), LineTypes.AntiAlias);
        var hole = Map(HoleCenterX, HoleCenterY, dx, dy, angleDeg);
        Cv2.Circle(img, new Point((int)Math.Round(hole.X), (int)Math.Round(hole.Y)), HoleRadius, new Scalar(40), -1, LineTypes.AntiAlias);
        Cv2.FillConvexPoly(img, Quad(KeyX, KeyY, KeyArm, KeyBar, dx, dy, angleDeg), new Scalar(40), LineTypes.AntiAlias);
        Cv2.FillConvexPoly(img, Quad(KeyX, KeyY, KeyBar, KeyArm, dx, dy, angleDeg), new Scalar(40), LineTypes.AntiAlias);
        Cv2.GaussianBlur(img, img, new Size(5, 5), 1.2);
        return img;
    }

    private static Point[] Quad(double x, double y, double w, double h, double dx, double dy, double angleDeg)
    {
        var pts = new[] { (x, y), (x + w, y), (x + w, y + h), (x, y + h) };
        var q = new Point[4];
        for (var i = 0; i < 4; i++)
        {
            var m = Map(pts[i].Item1, pts[i].Item2, dx, dy, angleDeg);
            q[i] = new Point((int)Math.Round(m.X), (int)Math.Round(m.Y));
        }
        return q;
    }
}
