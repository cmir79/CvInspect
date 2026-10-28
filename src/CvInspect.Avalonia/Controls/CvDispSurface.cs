// CvDispSurface 본체 — 표시 상태(프레임·오버레이·편집 도형) 보관과 뷰 변환(줌·핏·좌표 환산).
// 그리기는 CvDispSurface.Render.cs, 포인터 조작은 CvDispSurface.Input.cs 에 있다.
// 뷰 변환·드래그·렌더 기하·픽셀 변환의 계산은 UI 중립 계층(CvInspect.Display — 코어 소스 링크)이 하고, 여기는 Avalonia 쪽 배선만 남는다.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CvInspect.Display;
using CvInspect.Vision.Edit;
using CvInspect.Vision.Overlay;

namespace CvInspect.Avalonia.Controls;

/// <summary>CvDispCtrl 의 렌더/인터랙션 표면 — 이미지·오버레이·도형을 단일 Render 로 그린다.</summary>
internal sealed partial class CvDispSurface : Control
{
    // 화면 비트맵 — BGRA 한 장을 치수가 바뀔 때까지 다시 쓴다. Gray8·Bgr24 비트맵을 쓰면 잠금 해제 때마다 프레임워크가 RGBA 로 변환하며
    // 프레임 크기×4 의 비관리 버퍼를 새로 할당하고 그 동안 비트맵 락을 쥔다(2448×2048 실측: 내장 7.4 / 8.5 ms 대 코어 CvPixConv 1.9 / 3.5 ms,
    // 바이트 일치). 그래서 채널 수와 무관하게 BGRA 로 두고 변환은 잠금 버퍼 안에서 코어가 한다.
    // 4채널 프레임은 비선곱 알파(Unpremul) — 1·3채널은 알파가 전부 255 라 어느 쪽이든 같다.
    private WriteableBitmap? _wb;
    private WriteableBitmap? _bitmap;   // 지금 보이는 비트맵(= _wb) — 프레임이 없으면 null
    // 표시 프레임 픽셀(형 분기·Mat 스냅샷·공급자 계약 검사·픽셀 값·저장용 복원) — 코어 CvFrameBuf. 여기는 그 픽셀을 비트맵으로 옮기기만 한다.
    private readonly CvFrameBuf _frame = new();
    private ViOverlay? _overlay;
    private readonly CvShapeSub _shapeSub;   // 편집 도형 목록과 그 Changed 구독 — 화면에 올라와 있는 동안만 건다(코어 CvShapeSub)
    private CvViewXform _view = CvViewXform.Identity;   // 이미지 → 화면
    private bool _fitPending = true;
    private bool _statusPosted;

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

    private IBrush? _viewBg;

    /// <summary>영상 영역 바탕(이미지 바깥 여백과 무이미지 화면). null 이면 투명하게 칠해 부모(CvDispCtrl)의 Background 가 비친다 —
    /// 호스트가 테마 키·속성을 주지 않으면 컨트롤 기본 바탕(또는 호스트가 컨트롤에 직접 준 Background) 그대로다.</summary>
    public IBrush? ViewBackground
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
        // 생성자에서 ApplyStyling 을 부르지 않는다 — 부르면 논리 트리에 붙을 때 받을 앱 스타일이 영구히 빠진다(실측).
        ClipToBounds = true;
        Focusable = false;
        // 확대 보간 — 기본값(Unspecified)은 확대한 화소를 번지게 한다(실측). None 이 배율 1.0·1.25·1.5 에서 최근접과 화소 단위로 같았다.
        // 그리기에서도 이미지 앞에 같은 값을 밀어 넣는다(Render.cs) — 이 설정은 호스트가 덮을 수 있는 속성이라서다.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);

        // 화면에서 내려가면 도형 구독을 놓고 다시 붙을 때 되건다 — 근거(누수 실측)는 코어 CvShapeSub.
        _shapeSub = new CvShapeSub(OnShapeChanged);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _shapeSub.Attach(true, true);   // 올라옴 통지 안이다 — IsLoaded 가 이 시점에 이미 섰는지에 기대지 않는다(늦으면 구독이 조용히 빠진다)
        CvLoc.Changed -= OnLocChanged;
        CvLoc.Changed += OnLocChanged;
        if (_bitmap is null) InvalidateVisual();   // 내려가 있던 사이 바뀐 언어 — 그동안은 통지를 듣지 않았다
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        CvLoc.Changed -= OnLocChanged;   // 정적 이벤트 — 내려간 컨트롤을 붙잡지 않게 놓는다
        _shapeSub.Attach(false, false);
        _editDrag.Cancel();   // 내려가며 잃은 포인터 캡처 — 다시 올라왔을 때 옛 드래그가 이어지지 않게
    }

    // === 외부 상태 주입 (CvDispCtrl 속성 콜백) ===

    /// <summary>뷰/프레임 변경 통지 — 상위 컨트롤이 상태 줄 갱신에 쓴다. <b>언제나 UI 디스패처를 거쳐(Background 우선순위) 불린다</b> —
    /// 그리기 안에서 맞춤이 뷰를 바꿀 때도 나는데, 그리기 중에는 다시 그리기 요청·렌더에 영향 주는 속성 변경이 던지기 때문이다.
    /// 한 번 미뤄진 통지가 돌기 전에 여러 번 청해도 한 번만 분다.</summary>
    internal Action? StatusChanged;

    private void RaiseStatus()
    {
        if (_statusPosted) return;
        _statusPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _statusPosted = false;
            StatusChanged?.Invoke();
        }, DispatcherPriority.Background);
    }

    internal double ViewScale => Scale;

    internal (int W, int H)? FrameSize => _frame.Width == 0 ? null : (_frame.Width, _frame.Height);

    /// <summary>표시 중인 프레임 픽셀 — 파일 저장(코어 CvImgFileIO.WriteFrame)이 읽는다.</summary>
    internal CvFrameBuf FrameBuf => _frame;

    /// <summary>화면 좌표 → 이미지 정수 픽셀 좌표 + 픽셀 값 라벨(1채널: "V n" / 컬러: "RGB r,g,b", 코어 CvFrameBuf.ValueAt) — 이미지 밖이면 null.</summary>
    internal (int X, int Y, string? Val)? ProbeImage(Point screen)
    {
        if (_bitmap is null) return null;
        var p = ToImage(screen);
        var x = (int)Math.Floor(p.X);
        var y = (int)Math.Floor(p.Y);
        if (x < 0 || y < 0 || x >= _frame.Width || y >= _frame.Height) return null;
        return (x, y, _frame.ValueAt(x, y));
    }

    /// <summary>프레임 교체 — 형 분기(Mat·ICvPixelSource·그 밖은 경고), Mat 스냅샷 복사, 공급자 계약 검사는 코어 CvFrameBuf 가 한다
    /// (Mat 은 대입 뒤 호스트가 dispose 해도 안전, 공급자 배열은 참조 — 프레임당 복사는 화면 비트맵으로 가는 변환 한 번뿐).
    /// 담을 수 없는 값은 빈 화면. 프레임이 없어지거나 치수가 바뀌면 맞춤을 예약한다. UI 스레드에서 부른다.</summary>
    public void SetFrame(object? value)
    {
        var refit = _frame.Set(value);
        if (_frame.Pixels is { } px)
        {
            try { Present(px, _frame.Width, _frame.Height, _frame.Stride, _frame.Channels); }
            catch
            {
                // 버퍼는 이미 새 프레임인데 비트맵이 옛것이면 옛 그림을 새 치수로 그리고 탐침·저장은 새 픽셀을 읽는다 — 빈 화면으로 맞춘 뒤 던진다.
                _frame.Set(null);
                _bitmap = null;
                _fitPending = true;
                InvalidateVisual();
                RaiseStatus();
                throw;
            }
        }
        else _bitmap = null;
        if (refit) _fitPending = true;
        InvalidateVisual();   // 잠금을 푼 뒤에 청한다 — 다시 그리기가 새 픽셀을 읽는다
        RaiseStatus();
    }

    /// <summary>표시 비트맵 갱신 — 재사용 BGRA 비트맵의 잠금 버퍼에 코어 변환(CvPixConv)으로 전면을 쓴다.
    /// 잠금 버퍼의 행 보폭은 원본과 다를 수 있어(정렬) 행마다 옮긴다 — 변환이 두 보폭을 따로 받는다.
    /// 쓰기는 잠금 안에서만 한다: 렌더 스레드가 같은 락으로 비트맵을 읽고, 잠금은 잡은 스레드에서 풀어야 한다(using 으로 묶는다).</summary>
    private unsafe void Present(byte[] pixels, int w, int h, int stride, int channels)
    {
        if (_wb is null || _wb.PixelSize.Width != w || _wb.PixelSize.Height != h)
        {
            // 옛 비트맵은 바로 놓는다 — 그리기 기록은 비트맵을 자기 참조로 쥐고 있어(복제 참조) 지금 놓아도 이미 기록된 그림은 안전하다.
            _wb?.Dispose();
            _wb = null;
            _bitmap = null;
            _wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Unpremul);
        }
        using (var fb = _wb.Lock())
        {
            var dst = new Span<byte>((void*)fb.Address, checked(fb.RowBytes * h));
            CvPixConv.ToBgra(channels, pixels, stride, dst, fb.RowBytes, w, h);
        }
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

    /// <summary>도형 변경 통지 — 도형을 바꾼 스레드에서 올 수 있다. 컨트롤은 UI 스레드에서만 만진다.</summary>
    private void OnShapeChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) InvalidateVisual();
        else Dispatcher.UIThread.Post(InvalidateVisual);
    }

    /// <summary>언어 전환 통지 — 무이미지 안내는 그릴 때만 번역을 읽어, 다시 그리지 않으면 다음 그리기(영상 도착·테마 전환)까지 옛 언어로
    /// 남는다. 바꾼 스레드에서 오고, UI 스레드에서 바꿨더라도 그리기 도중일 수 있으니 언제나 디스패처로 미룬다.</summary>
    private void OnLocChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_bitmap is null) InvalidateVisual();
    });

    /// <summary>맞춤을 예약만 하고 다음 그리기에서 한다 — 숨긴 채 불러도, 첫 프레임 전에 불러도 된다.</summary>
    public void FitToView()
    {
        _fitPending = true;
        InvalidateVisual();
    }

    /// <summary>화면 중앙 기준 줌 (툴바 ➕➖ 버튼).</summary>
    public void ZoomAtCenter(double factor)
    {
        if (_bitmap is null) return;
        if (!_view.TryZoomAt(factor, Bounds.Width / 2, Bounds.Height / 2, out var next)) return;
        _view = next;
        InvalidateVisual();
        RaiseStatus();
    }

    private Point ToScreen(double x, double y)
    {
        var (sx, sy) = _view.ToScreen(x, y);
        return new Point(sx, sy);
    }

    private Point ToImage(Point screen)
    {
        var (x, y) = _view.ToImage(screen.X, screen.Y);
        return new Point(x, y);
    }

    private double Scale => _view.Scale;
}
