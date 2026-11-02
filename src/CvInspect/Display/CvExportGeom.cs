// 그래픽 포함 내보내기(CvDispCtrl.RenderWithOverlay)의 기하 — 출력은 프레임 원본 해상도, 글자·선 굵기는 "기준 화면에 맞춰 봤을 때의 비율".
// UI 프레임워크 무관: 두 UI 패키지가 이 값 하나로 출력 비트맵의 DPI 와 그리기 목록의 뷰 변환을 정해, 같은 프레임이면 같은 그림이 나온다.
//
// 식: k = 1 / fitScale. fitScale 은 이미지 W×H 를 기준 화면 1280×960 DIP 에 맞췄을 때의 배율이다(화면의 ⛶ 맞춤과 같은 여백 0.97 —
// CvViewXform.RawFitScale. 줌 범위로는 자르지 않는다: 자르면 아주 긴 프레임에서 k 가 1/0.02 = 50 에 묶인다). 단 k ≥ 1 — 작은 이미지에서
// 글자를 원본 픽셀보다 작게 줄이지 않는다.
// 그리는 쪽은 뷰를 Scale = 1/k, Offset = 0 으로 두고 그리기 목록(DIP)을 만든 뒤 출력 비트맵을 DPI 96·k 로 만들어 그 위에 그린다.
// 그러면 DIP 공간이 (W/k × H/k) 라 이미지는 원본 픽셀 1:1 로, 글자·선은 화면에서 보던 DIP 크기의 k 배 픽셀로 나온다.
// 실측(이 식 그대로): 2448×2048 → k ≈ 2.199(글자 14 DIP 가 약 31 px), 1280×960 → k ≈ 1.031, 640×480 → k = 1(하한).

namespace CvInspect.Display;

/// <summary>내보내기 한 장의 기하 — 글자·선 배수 <see cref="K"/> 와 그리기 목록을 만들 뷰 변환 <see cref="View"/>.</summary>
internal readonly record struct CvExportGeom(double K, CvViewXform View)
{
    /// <summary>기준 화면 폭(DIP) — 글자·선 비율을 정하는 가상의 표시 칸.</summary>
    internal const double RefViewW = 1280;

    /// <summary>기준 화면 높이(DIP).</summary>
    internal const double RefViewH = 960;

    /// <summary>출력 비트맵 DPI — 96·k. 한 DIP 가 k 픽셀이 된다.</summary>
    internal double Dpi => 96 * K;

    /// <summary>프레임 W×H 의 내보내기 기하. 치수가 0 이하면 던진다 — 부르는 쪽은 프레임이 있을 때만 부른다.</summary>
    /// <exception cref="ArgumentOutOfRangeException">폭·높이가 0 이하.</exception>
    internal static CvExportGeom Of(int imgW, int imgH)
    {
        if (imgW <= 0) throw new ArgumentOutOfRangeException(nameof(imgW), imgW, "Image width must be positive");
        if (imgH <= 0) throw new ArgumentOutOfRangeException(nameof(imgH), imgH, "Image height must be positive");
        // 맞춤 배율의 날값(여백 0.97, 줌 범위로 자르지 않음) — 화면 맞춤(TryFit)은 줌 하한 0.02 로 잘라 아주 긴 프레임(폭 ÷ 1280 > 약 48)에서
        // k 가 50 에 묶인다. 여기서 k 는 줌 조작이 아니라 글자 비율이라 자르지 않는다. 치수는 위에서 양수로 걸렀다.
        var k = Math.Max(1.0, 1.0 / CvViewXform.RawFitScale(RefViewW, RefViewH, imgW, imgH));
        return new CvExportGeom(k, new CvViewXform(1.0 / k, 0, 0));
    }
}
