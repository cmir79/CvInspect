using System.Runtime.InteropServices;
using OpenCvSharp;

namespace CvInspect.Imaging;

/// <summary>
/// 카메라가 발행하는 프레임 한 장 — GC 소유 byte[] 경계 타입.
/// 수명 계약이 없다: 이벤트 밖으로 들고 나가든 다른 스레드(UI 디스패처 등)로 넘기든 안전하다.
/// 검사·표시(Mat 세계)로는 <see cref="CamFrameMatExt.AsMat"/> 로 무복사 래핑해 넘긴다.
/// 벤더 어댑터는 SDK 버퍼를 byte[] 로 실체화해 이 타입으로 발행한다 — Mat 경유가 편하면
/// <see cref="FromMat"/> 을 쓴다.
/// </summary>
public sealed class CamFrame
{
    public CamFrame(byte[] pixels, int width, int height, int stride, CamPixelFormat format)
    {
        Pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        TimestampUtc = DateTime.UtcNow;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public CamPixelFormat Format { get; }

    /// <summary>취득 시각 (UTC) — 생성 시점에 찍힌다.</summary>
    public DateTime TimestampUtc { get; }

    /// <summary>Mat → CamFrame 실체화(픽셀 복사) — 소스 구현이 발행 직전에 쓴다.
    /// 8UC1/8UC3/8UC4 만 지원(그 외는 예외). 타이트 패킹(stride = 폭×채널)으로 담는다.</summary>
    public static CamFrame FromMat(Mat mat)
    {
        if (mat is null || mat.Empty())
            throw new ArgumentException("mat is null or empty.", nameof(mat));

        var type = mat.Type();
        CamPixelFormat fmt;
        if (type == MatType.CV_8UC1) fmt = CamPixelFormat.Mono8;
        else if (type == MatType.CV_8UC3) fmt = CamPixelFormat.Bgr24;
        else if (type == MatType.CV_8UC4) fmt = CamPixelFormat.Bgra32;
        else throw new NotSupportedException($"Unsupported Mat type for CamFrame: {type}");

        var stride = mat.Width * mat.Channels();
        var buf = new byte[stride * mat.Height];
        if (mat.IsContinuous())
        {
            Marshal.Copy(mat.Data, buf, 0, buf.Length);
        }
        else
        {
            using var cont = mat.Clone();   // 서브뷰(ROI)라 행 사이 패딩이 있는 경우 — Clone 은 항상 연속
            Marshal.Copy(cont.Data, buf, 0, buf.Length);
        }
        return new CamFrame(buf, mat.Width, mat.Height, stride, fmt);
    }
}
