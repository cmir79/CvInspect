// CvDispSurface 의 그리기 — Render 파이프라인. 이미지는 여기서 직접 그리고, 결과 오버레이·편집 도형·무이미지 안내는
// UI 중립 계층(ViDrawBldr)이 만든 그리기 목록을 Avalonia 어댑터(CvDispDrawAdp)가 그린다.
//
// ⚠ Render 안에서는 다시 그리기 요청(InvalidateVisual)·렌더에 영향 주는 속성 변경·그것을 부르는 이벤트 발행을 하지 않는다 — 던진다.
// 상태 통지는 RaiseStatus 가 디스패처로 미룬다.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CvInspect.Display;

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
        if (_overlay is not null) ViDrawBldr.Overlay(_prims, _overlay, _view, tm, _overlayLabelScale, _clipOverlayToImage, _frame.Width, _frame.Height);
        if (_shapeSub.Shapes is { } shapes) ViDrawBldr.Shapes(_prims, shapes, _view, tm);
        CvDispDrawAdp.Draw(dc, _prims);
    }

    private void ApplyFit()
    {
        _fitPending = false;   // 맞추지 못해도(표면이 너무 작음) 예약은 소화한다 — 다음 크기 변경이 다시 세운다
        if (!CvViewXform.TryFit(Bounds.Width, Bounds.Height, _frame.Width, _frame.Height, out var fit)) return;
        _view = fit;
        RaiseStatus();   // 디스패처로 미룬다 — 그리기 중 통지는 던진다
    }
}
