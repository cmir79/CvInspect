// CvDispSurface 본체 — 표시 상태(프레임·오버레이·편집 도형) 보관과 뷰 변환(줌·핏·좌표 환산).
// 그리기는 CvDispSurface.Render.cs, 마우스 조작은 CvDispSurface.Input.cs 에 있다.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CvInspect.Vision.Overlay;
using OpenCvSharp;

namespace CvInspect.Controls;

/// <summary>CvDispCtrl 의 렌더/인터랙션 표면 — 이미지·오버레이·도형을 단일 OnRender 로 그린다.</summary>
internal sealed partial class CvDispSurface : FrameworkElement
{
    private const double MinScale = 0.02;
    private const double MaxScale = 64;

    private BitmapSource? _bitmap;
    private WriteableBitmap? _wb;   // 재사용 백버퍼 — 프레임마다 새 BitmapSource 할당(대해상도=수MB LOH)·신규 GPU 텍스처를 회피, WritePixels 전면 갱신
    private byte[]? _pixels;        // 표시 프레임 픽셀 — 상태바 픽셀 값 조회·파일 저장용. Mat 경로면 _snap(자체 스냅샷),
                                    // ICvPixelSource 경로면 공급자 배열 그 자체(복사 없음 — "발행 뒤 불변" 계약이 참조 보관을 허락한다).
    private byte[]? _snap;          // Mat 경로 전용 재사용 스냅샷 버퍼. Mat 은 호출측이 곧 dispose 할 수 있어 여기로 떠 둔다.
                                    // _pixels 와 분리하는 이유: 공급자 배열을 참조 중일 때 다음 Mat 을 그 위에 덮어쓰면 안 된다.
    private int _stride;            // _pixels 의 행 바이트 수 — Mat 경로는 폭×채널(타이트), 공급자 경로는 그쪽 Stride(행 끝 패딩 가능)
    private int _channels;          // 1=Gray8 / 3=Bgr24 / 4=Bgra32, 0=표시 불가 포맷
    private int _imgW;
    private int _imgH;
    private ViOverlay? _overlay;
    private IReadOnlyList<CvEditShape>? _shapes;
    private Matrix _view = Matrix.Identity;   // 이미지 → 화면
    private bool _fitPending = true;

    /// <summary>팬 모드 — true 면 좌드래그가 도형 히트 무시하고 항상 화면 이동 (툴바 ✋ 토글).</summary>
    public bool IsPanMode { get; set; }

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

    public CvDispSurface()
    {
        ClipToBounds = true;
        Focusable = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    // === 외부 상태 주입 (CvDispCtrl DP 콜백) ===

    /// <summary>뷰/프레임 변경 통지 — 상위 컨트롤이 상태바 갱신에 사용 (디스패처 경유 호출됨).</summary>
    internal Action? StatusChanged;

    internal double ViewScale => Scale;

    internal (int W, int H)? FrameSize => _imgW == 0 ? null : (_imgW, _imgH);

    /// <summary>화면 좌표 → 이미지 정수 픽셀 좌표 + 픽셀 값 라벨(1채널: "V n" / 컬러: "RGB r,g,b") — 이미지 밖이면 null.</summary>
    internal (int X, int Y, string? Val)? ProbeImage(System.Windows.Point screen)
    {
        if (_bitmap is null) return null;
        var p = ToImage(screen);
        var x = (int)Math.Floor(p.X);
        var y = (int)Math.Floor(p.Y);
        if (x < 0 || y < 0 || x >= _imgW || y >= _imgH) return null;

        string? val = null;
        if (_pixels is { } px)
        {
            var stride = _stride;
            switch (_channels)
            {
                case 1:
                    val = $"V {px[y * stride + x]}";
                    break;
                case 3:
                {
                    var o = y * stride + x * 3;
                    val = $"RGB {px[o + 2]},{px[o + 1]},{px[o]}";
                    break;
                }
                case 4:
                {
                    var o = y * stride + x * 4;
                    val = $"RGB {px[o + 2]},{px[o + 1]},{px[o]}";
                    break;
                }
            }
        }
        return (x, y, val);
    }

    /// <summary>프레임 교체(Mat) — 픽셀을 자체 스냅샷 버퍼로 복사하므로 리턴 후 호출측이 mat 을 dispose 해도 안전.
    /// null/빈 Mat 은 빈 화면. 치수 변경 시 자동 Fit.</summary>
    public void SetFrame(Mat? mat)
    {
        var prevW = _imgW;
        var prevH = _imgH;
        if (mat is null || mat.Empty() || !TryMapChannels(mat.Type(), out var ch, out var fmt))
        {
            ClearFrame();
        }
        else
        {
            var stride = mat.Width * ch;
            var len = stride * mat.Height;
            if (_snap is null || _snap.Length != len) _snap = new byte[len];
            if (mat.IsContinuous())
            {
                Marshal.Copy(mat.Data, _snap, 0, len);
            }
            else
            {
                using var cont = mat.Clone();   // 서브뷰(ROI)라 행 사이 패딩이 있는 경우 — Clone 은 항상 연속
                Marshal.Copy(cont.Data, _snap, 0, len);
            }
            Present(_snap, mat.Width, mat.Height, stride, ch, fmt);
        }
        AfterFrame(prevW, prevH);
    }

    /// <summary>프레임 교체(픽셀 공급자) — 복사하지 않고 공급자 배열을 참조로 붙잡는다. 프레임당 복사는 WPF 백버퍼로 가는
    /// WritePixels 한 번뿐이다. 이것이 안전한 근거는 <see cref="ICvPixelSource"/> 의 "발행 뒤 불변" 계약이다.
    /// 계약이 말하는 치수·길이를 만족하지 못하는 값은 빈 화면 + 경고 로그(WritePixels 가 던지기 전에 거른다).</summary>
    public void SetFrame(ICvPixelSource? src)
    {
        var prevW = _imgW;
        var prevH = _imgH;
        if (src is null || src.Width <= 0 || src.Height <= 0 || !TryMapChannels(src.Channels, out var fmt))
        {
            ClearFrame();
        }
        else if (src.Pixels is null || src.Stride < src.Width * src.Channels || src.Pixels.Length < (long)src.Stride * src.Height)
        {
            CvLog.Publish(CvLogLevel.Warning, nameof(CvDispCtrl),
                $"Frame ignored: ICvPixelSource buffer violates its contract (w={src.Width} h={src.Height} ch={src.Channels} stride={src.Stride} len={src.Pixels?.Length ?? -1}).");
            ClearFrame();
        }
        else
        {
            Present(src.Pixels, src.Width, src.Height, src.Stride, src.Channels, fmt);
        }
        AfterFrame(prevW, prevH);
    }

    private void ClearFrame()
    {
        _bitmap = null;
        _pixels = null;
        _snap = null;
        _stride = 0;
        _channels = 0;
        _imgW = 0;
        _imgH = 0;
    }

    private void AfterFrame(int prevW, int prevH)
    {
        if (_bitmap is null || _imgW != prevW || _imgH != prevH) _fitPending = true;
        InvalidateVisual();
        StatusChanged?.Invoke();
    }

    /// <summary>표시 가능한 8비트 채널 수 → WPF 픽셀 포맷. 그 외(16비트/float 등)는 false — 비트맵 없음(플레이스홀더).</summary>
    private static bool TryMapChannels(int channels, out PixelFormat fmt)
    {
        switch (channels)
        {
            case 1: fmt = PixelFormats.Gray8; return true;
            case 3: fmt = PixelFormats.Bgr24; return true;
            // BGRA — 일부 GigE 컬러 카메라가 실제로 발행하는 포맷. 빠뜨리면 컬러 검사 티칭 화면이 통째로 공백이 된다.
            case 4: fmt = PixelFormats.Bgra32; return true;
            default: fmt = default; return false;
        }
    }

    private static bool TryMapChannels(MatType type, out int channels, out PixelFormat fmt)
    {
        channels = type == MatType.CV_8UC1 ? 1 : type == MatType.CV_8UC3 ? 3 : type == MatType.CV_8UC4 ? 4 : 0;
        return TryMapChannels(channels, out fmt);
    }

    /// <summary>표시 비트맵 갱신 — 재사용 WriteableBitmap 에 전면 WritePixels. 매 프레임 새 BitmapSource 할당(대해상도=수MB LOH →
    /// GC 일시정지)과 신규 GPU 텍스처를 피한다. stride 는 그대로 넘긴다 — 행 끝 패딩은 WritePixels 가 건너뛴다.</summary>
    private void Present(byte[] pixels, int w, int h, int stride, int ch, PixelFormat fmt)
    {
        if (_wb is null || _wb.PixelWidth != w || _wb.PixelHeight != h || _wb.Format != fmt)
            _wb = new WriteableBitmap(w, h, 96, 96, fmt, null);
        _wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
        _bitmap = _wb;
        _pixels = pixels;
        _stride = stride;
        _channels = ch;
        _imgW = w;
        _imgH = h;
    }

    public void SetOverlay(ViOverlay? overlay)
    {
        _overlay = overlay;
        InvalidateVisual();
    }

    public void SetShapes(IReadOnlyList<CvEditShape>? shapes)
    {
        if (_shapes is not null)
            foreach (var s in _shapes) s.Changed -= OnShapeChanged;
        _shapes = shapes;
        if (_shapes is not null)
            foreach (var s in _shapes) s.Changed += OnShapeChanged;
        _drag = DragMode.None;
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
        var next = Math.Clamp(Scale * factor, MinScale, MaxScale);
        factor = next / Scale;
        if (Math.Abs(factor - 1) < 1e-9) return;
        var m = _view;
        m.ScaleAt(factor, factor, ActualWidth / 2, ActualHeight / 2);
        _view = m;
        InvalidateVisual();
        StatusChanged?.Invoke();
    }

    private System.Windows.Point ToScreen(double x, double y) => _view.Transform(new System.Windows.Point(x, y));

    private System.Windows.Point ToImage(System.Windows.Point screen)
    {
        var inv = _view;
        inv.Invert();
        return inv.Transform(screen);
    }

    private double Scale => _view.M11;   // 등방 스케일 전제 (Scale/ScaleAt 만 사용)

    /// <summary>표시 픽셀 → 새 Mat 복원 (파일 저장용) — 표시 가능한 프레임이 없으면 null. dispose 는 호출자 책임.
    /// 행 끝 패딩이 있어도(공급자 경로) Clone 이 연속 Mat 으로 접는다.</summary>
    internal Mat? SnapMat()
    {
        if (_pixels is null || _imgW == 0) return null;
        var type = _channels switch { 1 => MatType.CV_8UC1, 3 => MatType.CV_8UC3, _ => MatType.CV_8UC4 };
        using var view = Mat.FromPixelData(_imgH, _imgW, type, _pixels, _stride);   // 무복사 래핑 — dispose 는 핀 해제
        return view.Clone();
    }
}
