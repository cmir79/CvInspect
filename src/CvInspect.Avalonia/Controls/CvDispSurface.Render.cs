// CvDispSurface 의 그리기 — Render 파이프라인. 이미지는 여기서 직접 그리고, 결과 오버레이·편집 도형·무이미지 안내는
// UI 중립 계층(ViDrawBldr)이 만든 그리기 목록을 Avalonia 어댑터(CvDispDrawAdp)가 그린다.
//
// ⚠ Render 안에서는 다시 그리기 요청(InvalidateVisual)·렌더에 영향 주는 속성 변경·그것을 부르는 이벤트 발행을 하지 않는다 — 던진다.
// 상태 통지는 RaiseStatus 가 디스패처로 미룬다.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CvInspect.Display;
using ColorConversionCodes = OpenCvSharp.ColorConversionCodes;
using Cv2 = OpenCvSharp.Cv2;
using Mat = OpenCvSharp.Mat;
using MatType = OpenCvSharp.MatType;

namespace CvInspect.Avalonia.Controls;

internal sealed partial class CvDispSurface
{
    // 그리기 목록 — 그릴 때마다 비우고 다시 채운다. 목록 객체와 폴리라인 점 배열(풀)은 다시 쓰지만 프리미티브는 프레임마다 새로 만든다.
    // 비우는 것은 다음 그리기 때라 그 사이 지난 프레임의 프리미티브·글자 객체가 살아 있다(목록 하나 분량).
    private readonly List<ViPrim> _prims = new();

    private static readonly RenderOptions NearestImage = new() { BitmapInterpolationMode = BitmapInterpolationMode.None };

    // === 렌더 ===

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // 맞춤 예약만 한다. Avalonia 는 그 프레임의 배치(이 통지 포함)가 끝난 뒤에만 Render 를 부르고, 크기(Bounds)가 바뀐 요소는
        // 스스로 다시 그려진다 — 그래서 그 그리기가 예약을 소화한다(실측 헤드리스: 300→500·500→400 폭 모두 새 폭으로 맞춤,
        // 부모 SizeChanged 에서 무효화해도 같았다). 다시 그리기를 따로 청할 이유가 없다.
        _fitPending = true;
    }

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        // 히트 영역 확보 겸 영상 영역 바탕 — 테마 바탕이 없으면 투명(부모 Background 가 비친다). 투명 채움도 히트로 잡힌다(실측).
        dc.DrawRectangle(_viewBg ?? Brushes.Transparent, null, new Rect(0, 0, w, h));
        var tm = CvDispTextPort.Instance;
        ViDrawBldr.Clear(_prims);

        if (_bitmap is null || _frame.Width == 0)
        {
            // 무이미지 플레이스홀더 — 중앙 안내 문구만. 불러오기가 없는 화면에서는 힌트 문구가 되지도 않는 조작을 시키는 셈이라 제목만 남긴다.
            ViDrawBldr.Placeholder(_prims, w, h, CvLoc.T("cv:NoImage"),
                _showLoadHint ? CvLoc.T("cv:NoImageHint") : null, tm, _hintPaint);
            CvDispDrawAdp.Draw(dc, _prims);
            return;
        }

        if (_fitPending) ApplyFit();

        using (dc.PushTransform(new Matrix(_view.Scale, 0, 0, _view.Scale, _view.OffsetX, _view.OffsetY)))
        using (dc.PushRenderOptions(NearestImage))
            dc.DrawImage(_bitmap, new Rect(0, 0, _frame.Width, _frame.Height));

        // 클립(ClipOverlayToImage)의 근거는 CvDispCtrl.ClipOverlayToImage 주석. 편집 도형은 자르지 않는다 — 가장자리 핸들을 잡을 수 있어야 한다.
        // 숨김(👁 토글)은 결과 오버레이 전부(HUD 포함)만 뺀다 — 티칭 도형은 숨김과 무관하게 그린다(편집 중인 도형이 사라지면 잡을 수 없다).
        if (_overlay is not null && !_isOverlayHidden) ViDrawBldr.Overlay(_prims, _overlay, _view, tm, _overlayLabelScale, _clipOverlayToImage, _frame.Width, _frame.Height);
        if (_shapeSub.Shapes is { } shapes) ViDrawBldr.Shapes(_prims, shapes, _view, tm);
        CvDispDrawAdp.Draw(dc, _prims);
    }

    // === 그래픽 포함 내보내기 ===

    /// <summary>지금 표시 중인 프레임과 결과 오버레이를 원본 해상도로 다시 그린 BGR 3채널 Mat — 프레임이 없으면 null. 반환 Mat 은 호출자 소유다.
    /// 기하(출력 W×H, DPI 96·k, 뷰 Scale 1/k)는 코어 CvExportGeom 이 정한다 — 이미지는 원본 픽셀 1:1, 글자·선은 기준 화면 비율의 k 배 픽셀.
    /// 화면과 달리 넣지 않는 것: 편집 도형, 영상 영역 바탕·안내 문구(이미지가 전면을 덮는다), 이미지 사각 자르기(출력이 곧 이미지 사각이다).
    /// 숨김(👁)과 무관하게 오버레이를 넣는다 — 이름 그대로 "그래픽 포함" 이다. 화면 비트맵과 같은 그리기 어댑터를 지나므로 글자·선 모양이 화면과 같다.
    /// 렌더러를 쓰므로 UI 스레드에서 부른다(공개 메서드가 검사한다).</summary>
    internal Mat? RenderWithOverlay()
    {
        if (_bitmap is null || _frame.Width == 0) return null;
        int w = _frame.Width, h = _frame.Height;
        var geo = CvExportGeom.Of(w, h);
        var prims = new List<ViPrim>();
        try
        {
            using var opaque = OpaqueCopyOf4Ch();   // 4채널 프레임만 — 근거는 그 메서드
            using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(geo.Dpi, geo.Dpi));
            using (var dc = rtb.CreateDrawingContext())
            {
                // 이미지 — 뷰 Scale 1/k 로 DIP (W/k × H/k) 에 그리면 DPI 96·k 비트맵에서 원본 픽셀 1:1 이 된다. 보간은 화면과 같이 최근접으로 민다:
                // 배율이 부동소수 끝자리만큼 1 에서 어긋나도 화소 중심 표본이 같은 화소를 집어 원본 값이 그대로 남는다(회귀가 바이트로 잰다).
                using (dc.PushTransform(new Matrix(geo.View.Scale, 0, 0, geo.View.Scale, 0, 0)))
                using (dc.PushRenderOptions(NearestImage))
                    dc.DrawImage(opaque ?? _bitmap, new Rect(0, 0, w, h));
                if (_overlay is not null)
                    ViDrawBldr.Overlay(prims, _overlay, geo.View, CvDispTextPort.Instance, _overlayLabelScale, false, w, h);
                CvDispDrawAdp.Draw(dc, prims);
            }
            // 읽어 낼 형식을 못 박는다 — 렌더 대상의 기본 형식은 플랫폼마다 다를 수 있다(헤드리스 프레임 버퍼는 Rgba8888). 복사가 형식을 옮겨 준다.
            using var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
            using var fb = wb.Lock();
            rtb.CopyPixels(fb);
            using var bgra = Mat.FromPixelData(h, w, MatType.CV_8UC4, fb.Address, fb.RowBytes);
            var bgr = new Mat();
            try
            {
                Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);   // 알파 버림 — 불투명 이미지(4채널은 OpaqueCopyOf4Ch)가 전면을 덮어 결과는 불투명이다
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

    /// <summary>내보내기에 그릴 4채널 프레임의 불투명 사본 — 알파를 255 로 채운다(코어 CvPixConv.ToOpaqueBgra). 1·3채널이면 null(화면 비트맵이 원래 불투명이다).
    /// 사본을 두는 이유: 렌더 대상은 미리 곱한 알파라 알파가 255 보다 작은 화소를 그대로 그리면 색이 알파만큼 어두워진다(알파 0 → 검정) —
    /// 이미지 저장(알파 포함 원본)과 달리 B·G·R 이 원본에서 어긋난다. 내보내기는 알파를 버리는 3채널이므로 원본 B·G·R 을 남기려면 불투명으로 그려야 한다.
    /// 화면은 그대로 둔다 — 화면에서는 투명 화소가 영상 영역 바탕을 비추는 것이 계약이다(ViewBackground). 부른 쪽이 Dispose 한다.</summary>
    private unsafe WriteableBitmap? OpaqueCopyOf4Ch()
    {
        if (_frame.Channels != 4 || _frame.Pixels is not { } px) return null;
        int w = _frame.Width, h = _frame.Height;
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Unpremul);
        try
        {
            using var fb = wb.Lock();
            var dst = new Span<byte>((void*)fb.Address, checked(fb.RowBytes * h));
            CvPixConv.ToOpaqueBgra(4, px, _frame.Stride, dst, fb.RowBytes, w, h);
        }
        catch
        {
            wb.Dispose();
            throw;
        }
        return wb;
    }

    private void ApplyFit()
    {
        _fitPending = false;   // 맞추지 못해도(표면이 너무 작음) 예약은 소화한다 — 다음 크기 변경이 다시 세운다
        if (!CvViewXform.TryFit(Bounds.Width, Bounds.Height, _frame.Width, _frame.Height, out var fit)) return;
        _view = fit;
        RaiseStatus();   // 디스패처로 미룬다 — 그리기 중 통지는 던진다
    }
}
