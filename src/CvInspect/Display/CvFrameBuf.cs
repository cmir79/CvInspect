// 표시 프레임의 픽셀 보관 — 프레임 값의 형 분기, Mat 스냅샷, 픽셀 공급자 계약 검사, 채널 판정, 픽셀 값 읽기, 저장용 Mat 복원.
// UI 프레임워크 무관: UI 어댑터는 여기 담긴 픽셀을 자기 비트맵으로 옮기기만 한다(채널 수 → 픽셀 포맷 대응은 어댑터 몫).

using System.Runtime.InteropServices;
using OpenCvSharp;

namespace CvInspect.Display;

/// <summary>표시 중인 프레임의 픽셀. <see cref="Set"/> 이 프레임 값(<see cref="Mat"/>·<see cref="ICvPixelSource"/>·그 밖)을 받아 담는다.
/// 받는 포맷은 8비트 1·3·4채널뿐이고, 그 밖은 빈 프레임이다.</summary>
internal sealed class CvFrameBuf
{
    private byte[]? _snap;   // Mat 경로 전용 재사용 스냅샷 버퍼. Mat 은 호출측이 곧 dispose 할 수 있어 여기로 떠 둔다.
                             // Pixels 와 분리하는 이유: 공급자 배열을 참조 중일 때 다음 Mat 을 그 위에 덮어쓰면 안 된다.

    /// <summary>표시 프레임 픽셀 — 픽셀 값 조회·파일 저장용. Mat 경로면 자체 스냅샷, ICvPixelSource 경로면 공급자 배열 그 자체
    /// (복사 없음 — "발행 뒤 불변" 계약이 참조 보관을 허락한다). 프레임이 없으면 null.</summary>
    internal byte[]? Pixels { get; private set; }

    /// <summary><see cref="Pixels"/> 의 행 바이트 수 — Mat 경로는 폭×채널(타이트), 공급자 경로는 그쪽 Stride(행 끝 패딩 가능).</summary>
    internal int Stride { get; private set; }

    /// <summary>1=Gray8 / 3=Bgr24 / 4=Bgra32, 0=프레임 없음.</summary>
    internal int Channels { get; private set; }

    internal int Width { get; private set; }
    internal int Height { get; private set; }

    /// <summary>표시할 프레임이 있는가.</summary>
    internal bool HasFrame => Pixels is not null;

    /// <summary>표시 가능한 채널 수인가 — 8비트 1/3/4채널. BGRA(4)는 일부 GigE 컬러 카메라가 실제로 발행하는 포맷이라
    /// 빠뜨리면 컬러 검사 티칭 화면이 통째로 공백이 된다.</summary>
    internal static bool IsShowable(int channels) => channels is 1 or 3 or 4;

    /// <summary>Mat 형식 → 채널 수. 8비트 1/3/4채널이 아니면 0(16비트·float 등은 표시하지 않는다).</summary>
    internal static int ChannelsOf(MatType type)
        => type == MatType.CV_8UC1 ? 1 : type == MatType.CV_8UC3 ? 3 : type == MatType.CV_8UC4 ? 4 : 0;

    /// <summary>프레임 값을 담는다 — null 은 빈 프레임, <see cref="Mat"/> 은 스냅샷 복사, <see cref="ICvPixelSource"/> 는 배열 참조.
    /// 두 형이 아닌 값은 경고 한 줄을 남기고 빈 프레임이다. 돌려주는 값은 맞춤을 다시 할 차례인가 —
    /// 프레임이 없어졌거나 치수가 바뀌었으면 true.</summary>
    internal bool Set(object? value)
    {
        var prevW = Width;
        var prevH = Height;
        switch (value)
        {
            case null:
                SetMat(null);
                break;
            case Mat mat:
                SetMat(mat);
                break;
            case ICvPixelSource src:
                SetSource(src);
                break;
            default:
                CvLog.Publish(CvLogLevel.Warning, CvDispLog.Source,
                    $"Frame ignored: expected Mat or ICvPixelSource, got {value.GetType().FullName}.");
                SetMat(null);
                break;
        }
        return !HasFrame || Width != prevW || Height != prevH;
    }

    /// <summary>Mat 경로 — 픽셀을 자체 스냅샷 버퍼로 복사하므로 리턴 후 호출측이 mat 을 dispose 해도 안전. null/빈 Mat·표시 불가 형식은 빈 프레임.</summary>
    private void SetMat(Mat? mat)
    {
        var ch = mat is null || mat.Empty() ? 0 : ChannelsOf(mat.Type());
        if (mat is null || !IsShowable(ch))
        {
            Clear();
            return;
        }
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
        Keep(_snap, mat.Width, mat.Height, stride, ch);
    }

    /// <summary>공급자 경로 — 복사하지 않고 공급자 배열을 참조로 붙잡는다(<see cref="ICvPixelSource"/> 의 "발행 뒤 불변" 계약).
    /// 계약이 말하는 치수·길이를 만족하지 못하는 값은 빈 프레임 + 경고 로그 — 어댑터가 비트맵에 옮기다 던지기 전에 거른다.</summary>
    private void SetSource(ICvPixelSource src)
    {
        if (src.Width <= 0 || src.Height <= 0 || !IsShowable(src.Channels))
        {
            Clear();
        }
        else if (src.Pixels is null || src.Stride < src.Width * src.Channels || src.Pixels.Length < (long)src.Stride * src.Height)
        {
            CvLog.Publish(CvLogLevel.Warning, CvDispLog.Source,
                $"Frame ignored: ICvPixelSource buffer violates its contract (w={src.Width} h={src.Height} ch={src.Channels} stride={src.Stride} len={src.Pixels?.Length ?? -1}).");
            Clear();
        }
        else
        {
            Keep(src.Pixels, src.Width, src.Height, src.Stride, src.Channels);
        }
    }

    private void Keep(byte[] pixels, int w, int h, int stride, int ch)
    {
        Pixels = pixels;
        Stride = stride;
        Channels = ch;
        Width = w;
        Height = h;
    }

    /// <summary>빈 프레임 — 스냅샷 버퍼까지 놓는다.</summary>
    internal void Clear()
    {
        Pixels = null;
        _snap = null;
        Stride = 0;
        Channels = 0;
        Width = 0;
        Height = 0;
    }

    /// <summary>픽셀 (x, y) 의 값 라벨 — 1채널은 "V n", 3·4채널은 "RGB r,g,b"(저장 순서가 BGR(A)라 뒤집어 읽는다).
    /// 프레임이 없거나 이미지 밖이면 null.</summary>
    internal string? ValueAt(int x, int y)
    {
        if (Pixels is not { } px || x < 0 || y < 0 || x >= Width || y >= Height) return null;
        switch (Channels)
        {
            case 1:
                return $"V {px[y * Stride + x]}";
            case 3:
            {
                var o = y * Stride + x * 3;
                return $"RGB {px[o + 2]},{px[o + 1]},{px[o]}";
            }
            case 4:
            {
                var o = y * Stride + x * 4;
                return $"RGB {px[o + 2]},{px[o + 1]},{px[o]}";
            }
            default:
                return null;
        }
    }

    /// <summary>표시 픽셀 → 새 Mat 복원 (파일 저장용) — 프레임이 없으면 null. dispose 는 호출자 책임.
    /// 행 끝 패딩이 있어도(공급자 경로) Clone 이 연속 Mat 으로 접는다.</summary>
    internal Mat? SnapMat()
    {
        if (Pixels is null || Width == 0) return null;
        var type = Channels switch { 1 => MatType.CV_8UC1, 3 => MatType.CV_8UC3, _ => MatType.CV_8UC4 };
        using var view = Mat.FromPixelData(Height, Width, type, Pixels, Stride);   // 무복사 래핑 — dispose 는 핀 해제
        return view.Clone();
    }
}
