// CvDispSurface 의 그리기 — OnRender 파이프라인. 이미지는 여기서 직접 그리고, 결과 오버레이·편집 도형·무이미지 안내는
// UI 중립 계층(ViDrawBldr)이 만든 그리기 목록을 WPF 어댑터(CvDispDrawAdp)가 그린다.

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CvInspect.Display;
using ColorConversionCodes = OpenCvSharp.ColorConversionCodes;
using Cv2 = OpenCvSharp.Cv2;
using Mat = OpenCvSharp.Mat;
using MatType = OpenCvSharp.MatType;

namespace CvInspect.Controls;

internal sealed partial class CvDispSurface
{
    // 그리기 목록 — 그릴 때마다 비우고 다시 채운다. 목록 객체와 폴리라인 점 배열(풀)은 다시 쓰지만 프리미티브는 프레임마다 새로 만든다.
    // 비우는 것은 다음 그리기 때라 그 사이 지난 프레임의 프리미티브·글자 객체가 살아 있다(목록 하나 분량).
    private readonly List<ViPrim> _prims = new();

    // === 렌더 ===

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // 맞춤을 예약만 하지 않고 다시 그리기를 함께 청한다. 크기가 바뀐 배치에서 WPF 는 Arrange 안에서 OnRender 를
        // 먼저 부르고(그때는 예약이 없어 옛 배율로 그린다) 이 통지는 배치가 끝난 뒤에 보낸다(PresentationCore 8.0.31
        // UIElement.Arrange → ContextLayoutManager.UpdateLayout 의 fireSizeChangedEvents, 원문 확인). 예약만 세우고
        // 나가면 두 가지가 남는다 — 실측(1000×500 영상, 표면 800→1400 폭, 새 프레임 없음):
        //  ① 다음 프레임이 올 때까지 옛 배율: 776px 그대로(맞춤은 1126px). 프레임이 드문 화면(관제·라인 정지 중)에서
        //     창이 커지면 여백, 작아지면 잘림이 그대로 남았다.
        //  ② 다음 그리기가 무엇이든 예약을 소화한다: 휠 한 칸 축소가 621px 이 아니라 1126px 로 커졌다(줌이 덮인다).
        // 부모 쪽 SizeChanged 에서 무효화해서는 안 된다 — 이 통지는 바깥 요소부터 나가고, 핸들러가 다시 그리기를
        // 청하면 WPF 가 통지를 멈추고 배치부터 다시 해서 이 표면은 예약이 서기 전에 다시 그려진다(실측 776px 그대로).
        // 여기서는 예약이 먼저 서 있으니 이 요청이 부르는 그리기가 맞춘다. 대가는 크기가 바뀔 때 OnRender 한 번 더.
        _fitPending = true;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // 히트 영역 확보 겸 영상 영역 바탕 — 테마 바탕이 없으면 투명(부모 Background 가 비친다).
        dc.DrawRectangle(_viewBg ?? Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var tm = new CvDispTextPort(ppd);
        ViDrawBldr.Clear(_prims);

        if (_bitmap is null || _frame.Width == 0)
        {
            // 무이미지 플레이스홀더 — 중앙 안내 문구만 (히트 없음 — 우클릭 메뉴 등 상위 인터랙션 유지).
            // 불러오기가 없는 화면에서는 힌트 문구가 되지도 않는 조작을 시키는 셈이라 제목만 남긴다.
            ViDrawBldr.Placeholder(_prims, ActualWidth, ActualHeight, CvLoc.T("cv:NoImage"),
                _showLoadHint ? CvLoc.T("cv:NoImageHint") : null, tm, _hintPaint);
            CvDispDrawAdp.Draw(dc, _prims, ppd);
            return;
        }

        if (_fitPending) ApplyFit();

        dc.PushTransform(new MatrixTransform(new Matrix(_view.Scale, 0, 0, _view.Scale, _view.OffsetX, _view.OffsetY)));
        dc.DrawImage(_bitmap, new Rect(0, 0, _frame.Width, _frame.Height));
        dc.Pop();

        // 클립(ClipOverlayToImage)의 근거·실측은 CvDispCtrl.ClipOverlayToImage 주석. 편집 도형은 자르지 않는다 — 가장자리 핸들을 잡을 수 있어야 한다.
        // 숨김(👁 토글)은 결과 오버레이 전부(HUD 포함)만 뺀다 — 티칭 도형은 숨김과 무관하게 그린다(편집 중인 도형이 사라지면 잡을 수 없다).
        if (_overlay is not null && !_isOverlayHidden) ViDrawBldr.Overlay(_prims, _overlay, _view, tm, _overlayLabelScale, _clipOverlayToImage, _frame.Width, _frame.Height);
        if (_shapeSub.Shapes is { } shapes) ViDrawBldr.Shapes(_prims, shapes, _view, tm);
        CvDispDrawAdp.Draw(dc, _prims, ppd);
    }

    // === 그래픽 포함 내보내기 ===

    /// <summary>지금 표시 중인 프레임과 결과 오버레이를 원본 해상도로 다시 그린 BGR 3채널 Mat — 프레임이 없으면 null. 반환 Mat 은 호출자 소유다.
    /// 기하(출력 W×H, DPI 96·k, 뷰 Scale 1/k)는 코어 CvExportGeom 이 정한다 — 이미지는 원본 픽셀 1:1, 글자·선은 기준 화면 비율의 k 배 픽셀.
    /// 화면과 달리 넣지 않는 것: 편집 도형, 영상 영역 바탕·안내 문구(이미지가 전면을 덮는다), 이미지 사각 자르기(출력이 곧 이미지 사각이다).
    /// 숨김(👁)과 무관하게 오버레이를 넣는다 — 이름 그대로 "그래픽 포함" 이다. 화면과 같은 그리기 어댑터·백버퍼를 지나므로 글자·선 모양이 화면과 같다.
    /// 글자 측정의 화소/DIP 배율은 k 다 — 출력 비트맵에서 한 DIP 가 k 화소라 글리프 맞춤도 그 배율로 한다.
    /// 창 없이 그린다(DrawingVisual → RenderTargetBitmap). UI 스레드에서 부른다(공개 메서드가 검사한다).</summary>
    internal Mat? RenderWithOverlay()
    {
        if (_bitmap is null || _frame.Width == 0) return null;
        int w = _frame.Width, h = _frame.Height;
        var geo = CvExportGeom.Of(w, h);
        var prims = new List<ViPrim>();
        try
        {
            var visual = new DrawingVisual();
            // 최근접 — 배율이 부동소수 끝자리만큼 1 에서 어긋나도 화소 중심 표본이 같은 화소를 집어 원본 값이 그대로 남는다(회귀가 바이트로 잰다).
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            using (var dc = visual.RenderOpen())
            {
                // 이미지 — 뷰 Scale 1/k 로 DIP (W/k × H/k) 에 그리면 DPI 96·k 비트맵에서 원본 픽셀 1:1 이 된다.
                dc.PushTransform(new MatrixTransform(new Matrix(geo.View.Scale, 0, 0, geo.View.Scale, 0, 0)));
                dc.DrawImage(ExportSource(), new Rect(0, 0, w, h));
                dc.Pop();
                if (_overlay is not null)
                    ViDrawBldr.Overlay(prims, _overlay, geo.View, new CvDispTextPort(geo.K), _overlayLabelScale, false, w, h);
                CvDispDrawAdp.Draw(dc, prims, geo.K);
            }
            var rtb = new RenderTargetBitmap(w, h, geo.Dpi, geo.Dpi, PixelFormats.Pbgra32);
            rtb.Render(visual);
            using var bgra = new Mat(h, w, MatType.CV_8UC4);   // 새로 만든 Mat 은 연속 — 행 보폭 = 폭×4
            rtb.CopyPixels(Int32Rect.Empty, bgra.Data, checked(w * h * 4), w * 4);
            var bgr = new Mat();
            try
            {
                Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);   // 알파 버림 — 불투명 이미지(ExportSource)가 전면을 덮어 결과는 불투명이다
            }
            catch
            {
                bgr.Dispose();
                throw;
            }
            return bgr;
        }
        finally
        {
            ViDrawBldr.Clear(prims);
        }
    }

    /// <summary>내보내기에 그릴 이미지 — 1·3채널은 화면 백버퍼 그대로(원래 불투명), 4채널은 알파를 255 로 채운 사본이다(코어 CvPixConv.ToOpaqueBgra).
    /// 사본을 두는 이유: 렌더 대상은 미리 곱한 알파(Pbgra32)라 알파가 255 보다 작은 화소를 그대로 그리면 색이 알파만큼 어두워진다(알파 0 → 검정) —
    /// 이미지 저장(알파 포함 원본)과 달리 B·G·R 이 원본에서 어긋난다. 내보내기는 알파를 버리는 3채널이므로 원본 B·G·R 을 남기려면 불투명으로 그려야 한다.
    /// 화면은 그대로 둔다 — 화면에서는 투명 화소가 영상 영역 바탕을 비추는 것이 계약이다(ViewBackground).</summary>
    private BitmapSource ExportSource()
    {
        if (_frame.Channels != 4) return _bitmap!;
        int w = _frame.Width, h = _frame.Height;
        var opaque = new byte[checked(w * h * 4)];
        CvPixConv.ToOpaqueBgra(4, _frame.Pixels!, _frame.Stride, opaque, w * 4, w, h);
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, opaque, w * 4);
        src.Freeze();
        return src;
    }

    private void ApplyFit()
    {
        _fitPending = false;   // 맞추지 못해도(표면이 너무 작음) 예약은 소화한다 — 다음 크기 변경이 다시 세운다
        if (!CvViewXform.TryFit(ActualWidth, ActualHeight, _frame.Width, _frame.Height, out var fit)) return;
        _view = fit;
        StatusChanged?.Invoke();   // 컨트롤이 디스패처 경유로 갱신 — OnRender 중 호출 안전
    }
}
