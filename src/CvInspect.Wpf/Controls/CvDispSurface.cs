// CvDispSurface 본체 — 표시 상태(프레임·오버레이·편집 도형) 보관과 뷰 변환(줌·핏·좌표 환산).
// 그리기는 CvDispSurface.Render.cs, 마우스 조작은 CvDispSurface.Input.cs 에 있다.
// 뷰 변환·드래그·렌더 기하의 계산은 UI 중립 계층(CvInspect.Display — 코어 소스 링크)이 하고, 여기는 WPF 쪽 배선만 남는다.

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CvInspect.Display;
using CvInspect.Vision.Overlay;
using CvInspect.Vision.Edit;

namespace CvInspect.Controls;

/// <summary>CvDispCtrl 의 렌더/인터랙션 표면 — 이미지·오버레이·도형을 단일 OnRender 로 그린다.</summary>
internal sealed partial class CvDispSurface : FrameworkElement
{
    private BitmapSource? _bitmap;
    private WriteableBitmap? _wb;   // 재사용 백버퍼 — 프레임마다 새 BitmapSource 할당(대해상도=수MB LOH)·신규 GPU 텍스처를 회피, WritePixels 전면 갱신
    // 표시 프레임 픽셀(형 분기·Mat 스냅샷·공급자 계약 검사·픽셀 값·저장용 복원) — 코어 CvFrameBuf. 여기는 그 픽셀을 백버퍼로 옮기기만 한다.
    private readonly CvFrameBuf _frame = new();
    private ViOverlay? _overlay;
    private readonly CvShapeSub _shapeSub;   // 편집 도형 목록과 그 Changed 구독 — 화면에 올라와 있는 동안만 건다(코어 CvShapeSub)
    private CvViewXform _view = CvViewXform.Identity;   // 이미지 → 화면
    private bool _fitPending = true;

    /// <summary>팬 모드 — true 면 좌드래그가 도형 히트 무시하고 항상 화면 이동 (툴바 ✋ 토글).</summary>
    public bool IsPanMode { get; set; }

    private bool _clipOverlayToImage;

    /// <summary>결과 오버레이를 이미지 사각으로 자를지 — 컨트롤의 같은 이름 속성이 설명을 갖는다.</summary>
    public bool ClipOverlayToImage
    {
        get => _clipOverlayToImage;
        set
        {
            if (_clipOverlayToImage == value) return;
            _clipOverlayToImage = value;
            InvalidateVisual();
        }
    }

    private double _overlayLabelScale = 1.0;

    /// <summary>요약 HUD 가 아닌 오버레이 라벨의 글자 배율 — 컨트롤의 같은 이름 속성이 설명을 갖는다(값 검사도 그쪽).</summary>
    public double OverlayLabelScale
    {
        get => _overlayLabelScale;
        set
        {
            if (_overlayLabelScale == value) return;
            _overlayLabelScale = value;
            if (_overlay is not null) InvalidateVisual();
        }
    }

    private bool _showLoadHint = true;

    /// <summary>무이미지 안내에 "우클릭 → 불러오기" 한 줄을 붙일지. 불러오기가 없는 화면에서는
    /// 그 문구가 되지도 않는 조작을 시키는 셈이라 제목만 남긴다.</summary>
    public bool ShowLoadHint
    {
        get => _showLoadHint;
        set
        {
            if (_showLoadHint == value) return;
            _showLoadHint = value;
            if (_bitmap is null) InvalidateVisual();   // 플레이스홀더가 떠 있을 때만 다시 그리면 된다
        }
    }

    private Brush? _viewBg;

    /// <summary>영상 영역 바탕(이미지 바깥 여백과 무이미지 화면). null 이면 투명하게 칠해 부모(CvDispCtrl)의 Background 가 비친다 —
    /// 호스트가 테마 키·속성을 주지 않으면 종전 모습(#2A2A2A, 또는 호스트가 컨트롤에 직접 준 Background) 그대로다.</summary>
    public Brush? ViewBackground
    {
        get => _viewBg;
        set
        {
            if (ReferenceEquals(_viewBg, value)) return;
            _viewBg = value;
            InvalidateVisual();
        }
    }

    private ViPaint? _hintPaint;

    /// <summary>무이미지 안내 글자 색. null 이면 코어 기본(#9E9E9E).</summary>
    public ViPaint? PlaceholderPaint
    {
        get => _hintPaint;
        set
        {
            if (_hintPaint == value) return;
            _hintPaint = value;
            if (_bitmap is null) InvalidateVisual();   // 플레이스홀더가 떠 있을 때만 다시 그리면 된다
        }
    }

    public CvDispSurface()
    {
        ClipToBounds = true;
        Focusable = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

        // 화면에서 내려가면 도형 구독을 놓고 다시 붙을 때 되건다 — 근거(누수 실측)는 코어 CvShapeSub.
        _shapeSub = new CvShapeSub(OnShapeChanged);
        Loaded += (_, _) =>
        {
            _shapeSub.Attach(true, IsLoaded);
            CvLoc.Changed -= OnLocChanged;
            CvLoc.Changed += OnLocChanged;
            if (_bitmap is null) InvalidateVisual();   // 내려가 있던 사이 바뀐 언어 — 그동안은 통지를 듣지 않았다
        };
        Unloaded += (_, _) =>
        {
            CvLoc.Changed -= OnLocChanged;   // 정적 이벤트 — 내려간 컨트롤을 붙잡지 않게 놓는다
            _shapeSub.Attach(false, IsLoaded);
        };
    }

    /// <summary>언어 전환 통지 — 무이미지 안내는 그릴 때만 번역을 읽어, 다시 그리지 않으면 다음 그리기(영상 도착·테마 전환)까지 옛 언어로
    /// 남는다. 바꾼 스레드에서 오니 디스패처로 넘긴다.</summary>
    private void OnLocChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (_bitmap is null) InvalidateVisual();
    });

    // === 외부 상태 주입 (CvDispCtrl DP 콜백) ===

    /// <summary>뷰/프레임 변경 통지 — 상위 컨트롤이 상태바 갱신에 사용 (디스패처 경유 호출됨).</summary>
    internal Action? StatusChanged;

    internal double ViewScale => Scale;

    internal (int W, int H)? FrameSize => _frame.Width == 0 ? null : (_frame.Width, _frame.Height);

    /// <summary>표시 중인 프레임 픽셀 — 파일 저장(코어 CvImgFileIO.WriteFrame)이 읽는다.</summary>
    internal CvFrameBuf FrameBuf => _frame;

    /// <summary>화면 좌표 → 이미지 정수 픽셀 좌표 + 픽셀 값 라벨(1채널: "V n" / 컬러: "RGB r,g,b", 코어 CvFrameBuf.ValueAt) — 이미지 밖이면 null.</summary>
    internal (int X, int Y, string? Val)? ProbeImage(System.Windows.Point screen)
    {
        if (_bitmap is null) return null;
        var p = ToImage(screen);
        var x = (int)Math.Floor(p.X);
        var y = (int)Math.Floor(p.Y);
        if (x < 0 || y < 0 || x >= _frame.Width || y >= _frame.Height) return null;
        return (x, y, _frame.ValueAt(x, y));
    }

    /// <summary>프레임 교체 — 형 분기(Mat·ICvPixelSource·그 밖은 경고), Mat 스냅샷 복사, 공급자 계약 검사는 코어 CvFrameBuf 가 한다
    /// (Mat 은 대입 뒤 호스트가 dispose 해도 안전, 공급자 배열은 참조 — 프레임당 복사는 WPF 백버퍼로 가는 WritePixels 한 번뿐).
    /// 담을 수 없는 값은 빈 화면. 프레임이 없어지거나 치수가 바뀌면 맞춤을 예약한다.</summary>
    public void SetFrame(object? value)
    {
        var refit = _frame.Set(value);
        if (_frame.Pixels is { } px)
        {
            try { Present(px, _frame.Width, _frame.Height, _frame.Stride, FormatOf(_frame.Channels)); }
            catch
            {
                // 버퍼는 이미 새 프레임인데 비트맵이 옛것이면 옛 그림을 새 치수로 그리고 탐침·저장은 새 픽셀을 읽는다 — 빈 화면으로 맞춘 뒤 던진다.
                _frame.Set(null);
                _bitmap = null;
                _fitPending = true;
                InvalidateVisual();
                StatusChanged?.Invoke();
                throw;
            }
        }
        else _bitmap = null;
        if (refit) _fitPending = true;
        InvalidateVisual();
        StatusChanged?.Invoke();
    }

    /// <summary>채널 수 → WPF 픽셀 포맷. 받는 채널 수(1·3·4)는 코어 CvFrameBuf.IsShowable 이 정한다.</summary>
    private static PixelFormat FormatOf(int channels) => channels switch
    {
        1 => PixelFormats.Gray8,
        3 => PixelFormats.Bgr24,
        _ => PixelFormats.Bgra32,
    };

    /// <summary>표시 비트맵 갱신 — 재사용 WriteableBitmap 에 전면 WritePixels. 매 프레임 새 BitmapSource 할당(대해상도=수MB LOH →
    /// GC 일시정지)과 신규 GPU 텍스처를 피한다. stride 는 그대로 넘긴다 — 행 끝 패딩은 WritePixels 가 건너뛴다.</summary>
    private void Present(byte[] pixels, int w, int h, int stride, PixelFormat fmt)
    {
        if (_wb is null || _wb.PixelWidth != w || _wb.PixelHeight != h || _wb.Format != fmt)
            _wb = new WriteableBitmap(w, h, 96, 96, fmt, null);
        _wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
        _bitmap = _wb;
    }

    public void SetOverlay(ViOverlay? overlay)
    {
        _overlay = overlay;
        InvalidateVisual();
    }

    /// <summary>도형 목록 교체 — 옛 목록 구독을 놓고, 화면에 올라와 있을 때만 새 목록에 건다(코어 CvShapeSub.Swap).</summary>
    public void SetShapes(IReadOnlyList<CvEditShape>? shapes)
    {
        _shapeSub.Swap(shapes, IsLoaded);
        _editDrag.Cancel();
        InvalidateVisual();
    }

    private void OnShapeChanged(object? sender, EventArgs e) => InvalidateVisual();

    public void FitToView()
    {
        _fitPending = true;
        InvalidateVisual();
    }

    /// <summary>화면 중앙 기준 줌 (툴바 ➕➖ 버튼).</summary>
    public void ZoomAtCenter(double factor)
    {
        if (_bitmap is null) return;
        if (!_view.TryZoomAt(factor, ActualWidth / 2, ActualHeight / 2, out var next)) return;
        _view = next;
        InvalidateVisual();
        StatusChanged?.Invoke();
    }

    private System.Windows.Point ToScreen(double x, double y)
    {
        var (sx, sy) = _view.ToScreen(x, y);
        return new System.Windows.Point(sx, sy);
    }

    private System.Windows.Point ToImage(System.Windows.Point screen)
    {
        var (x, y) = _view.ToImage(screen.X, screen.Y);
        return new System.Windows.Point(x, y);
    }

    private double Scale => _view.Scale;
}
