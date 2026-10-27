// CvDispSurface 의 그리기 — OnRender 파이프라인. 이미지는 여기서 직접 그리고, 결과 오버레이·편집 도형·무이미지 안내는
// UI 중립 계층(ViDrawBldr)이 만든 그리기 목록을 WPF 어댑터(CvDispDrawAdp)가 그린다.

using System.Windows;
using System.Windows.Media;
using CvInspect.Display;

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

        if (_bitmap is null || _imgW == 0)
        {
            // 무이미지 플레이스홀더 — 중앙 안내 문구만 (히트 없음 — 우클릭 메뉴 등 상위 인터랙션 유지).
            // 불러오기가 없는 화면에서는 힌트 문구가 되지도 않는 조작을 시키는 셈이라 제목만 남긴다.
            ViDrawBldr.Placeholder(_prims, ActualWidth, ActualHeight, CvLoc.T("cv:NoImage"),
                _showLoadHint ? CvLoc.T("cv:NoImageHint") : null, tm);
            CvDispDrawAdp.Draw(dc, _prims, ppd);
            return;
        }

        if (_fitPending) ApplyFit();

        dc.PushTransform(new MatrixTransform(new Matrix(_view.Scale, 0, 0, _view.Scale, _view.OffsetX, _view.OffsetY)));
        dc.DrawImage(_bitmap, new Rect(0, 0, _imgW, _imgH));
        dc.Pop();

        // 클립(ClipOverlayToImage)의 근거·실측은 CvDispCtrl.ClipOverlayToImage 주석. 편집 도형은 자르지 않는다 — 가장자리 핸들을 잡을 수 있어야 한다.
        if (_overlay is not null) ViDrawBldr.Overlay(_prims, _overlay, _view, tm, _overlayLabelScale, _clipOverlayToImage, _imgW, _imgH);
        if (_shapes is not null) ViDrawBldr.Shapes(_prims, _shapes, _view, tm);
        CvDispDrawAdp.Draw(dc, _prims, ppd);
    }

    private void ApplyFit()
    {
        _fitPending = false;   // 맞추지 못해도(표면이 너무 작음) 예약은 소화한다 — 다음 크기 변경이 다시 세운다
        if (!CvViewXform.TryFit(ActualWidth, ActualHeight, _imgW, _imgH, out var fit)) return;
        _view = fit;
        StatusChanged?.Invoke();   // 컨트롤이 디스패처 경유로 갱신 — OnRender 중 호출 안전
    }
}
