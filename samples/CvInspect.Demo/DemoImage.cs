using OpenCvSharp;

namespace CvInspect.Demo;

/// <summary>
/// 합성 부품 이미지 — 어두운 배경 위의 밝은 판, 판 가운데의 어두운 구멍. 카메라 없이도 라인 파인더(판의 윗변)와
/// 원 파인더(구멍)가 잡을 것이 있게 만든다. 기하가 상수라 검사 결과를 수치로 검증할 수 있다.
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

    /// <summary>8비트 1채널. 살짝 흐려 에지가 서브픽셀로 잡히게 한다(계단 에지는 캘리퍼가 정수 위치에만 앉는다).</summary>
    public static Mat Create()
    {
        var img = new Mat(Height, Width, MatType.CV_8UC1, new Scalar(60));
        Cv2.Rectangle(img, new Rect(PlateX, PlateY, PlateW, PlateH), new Scalar(200), -1);
        Cv2.Circle(img, new Point(HoleCenterX, HoleCenterY), HoleRadius, new Scalar(40), -1);
        Cv2.GaussianBlur(img, img, new Size(5, 5), 1.2);
        return img;
    }
}
