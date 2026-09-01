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
    private byte[]? _pixels;        // 표시 프레임 픽셀 스냅샷(타이트 패킹, stride = 폭×채널) — 상태바 픽셀 값 조회·파일 저장용.
                                    // SetFrame 이 Mat 픽셀을 여기로 복사하므로 이후 원본 Mat 수명과 무관.
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
            var stride = _imgW * _channels;
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

    /// <summary>프레임 교체 — mat 픽셀을 자체 스냅샷 버퍼로 복사하므로 리턴 후 호출측이 mat 을 dispose 해도 안전.
    /// null/빈 Mat 은 빈 화면. 치수 변경 시 자동 Fit.</summary>
    public void SetFrame(Mat? mat)
    {
        var prevW = _imgW;
        var prevH = _imgH;
        if (mat is null || mat.Empty())
        {
            _bitmap = null;
            _pixels = null;
            _channels = 0;
            _imgW = 0;
            _imgH = 0;
        }
        else
        {
            _bitmap = UpdateBitmap(mat);
            _imgW = mat.Width;
            _imgH = mat.Height;
        }
        if (_bitmap is null || _imgW != prevW || _imgH != prevH) _fitPending = true;
        InvalidateVisual();
        StatusChanged?.Invoke();
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

    /// <summary>프레임 → 표시 비트맵. Mat 픽셀을 스냅샷 버퍼로 복사한 뒤 재사용 WriteableBitmap 에 전면 WritePixels —
    /// 매 프레임 새 BitmapSource 할당(대해상도=수MB LOH → GC 일시정지)과 신규 GPU 텍스처 업로드를 회피(더티 리전만 갱신).
    /// 전면 덮어쓰기라 잔상 없음. 해상도/포맷 변경 시에만 백버퍼 재생성.
    /// UI 스레드 호출 전제(FrameworkElement 소유 스레드 = Frame DP 갱신 경로).</summary>
    private WriteableBitmap? UpdateBitmap(Mat mat)
    {
        PixelFormat fmt;
        var type = mat.Type();
        if (type == MatType.CV_8UC1) { fmt = PixelFormats.Gray8; _channels = 1; }
        else if (type == MatType.CV_8UC3) { fmt = PixelFormats.Bgr24; _channels = 3; }
        // BGRA — 일부 GigE 컬러 카메라가 실제로 발행하는 포맷. 빠뜨리면 컬러 검사 티칭 화면이 통째로 공백이 된다.
        else if (type == MatType.CV_8UC4) { fmt = PixelFormats.Bgra32; _channels = 4; }
        else
        {
            // 표시 불가 포맷(16비트/float 등) — 비트맵 없음(플레이스홀더). 스냅샷도 남기지 않는다.
            _pixels = null;
            _channels = 0;
            return null;
        }

        var stride = mat.Width * _channels;
        var len = stride * mat.Height;
        if (_pixels is null || _pixels.Length != len) _pixels = new byte[len];
        if (mat.IsContinuous())
        {
            Marshal.Copy(mat.Data, _pixels, 0, len);
        }
        else
        {
            using var cont = mat.Clone();   // 서브뷰(ROI)라 행 사이 패딩이 있는 경우 — Clone 은 항상 연속
            Marshal.Copy(cont.Data, _pixels, 0, len);
        }

        if (_wb is null || _wb.PixelWidth != mat.Width || _wb.PixelHeight != mat.Height || _wb.Format != fmt)
            _wb = new WriteableBitmap(mat.Width, mat.Height, 96, 96, fmt, null);

        _wb.WritePixels(new Int32Rect(0, 0, mat.Width, mat.Height), _pixels, stride, 0);
        return _wb;
    }

    /// <summary>표시 스냅샷 → 새 Mat 복원 (파일 저장용) — 표시 가능한 프레임이 없으면 null. dispose 는 호출자 책임.</summary>
    internal Mat? SnapMat()
    {
        if (_pixels is null || _imgW == 0) return null;
        var type = _channels switch { 1 => MatType.CV_8UC1, 3 => MatType.CV_8UC3, _ => MatType.CV_8UC4 };
        var mat = new Mat(_imgH, _imgW, type);
        Marshal.Copy(_pixels, 0, mat.Data, _pixels.Length);
        return mat;
    }
}
