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

    /// <summary>행 바이트 수 — 장치에 따라 폭×바이트/픽셀보다 클 수 있다(행 끝 패딩).
    /// 소비 측은 항상 이 값으로 행을 걸어야 한다 (<see cref="CamFrameMatExt.AsMat"/> 은 자동 반영).
    /// <b>항상 양수다</b> — 취득 계층에는 압축 포맷 때문에 "줄 간격이 없다"는 상태가 있을 수 있지만,
    /// CamFrame 은 이미 풀린 8비트 프레임이라 행이 언제나 정수 바이트로 떨어진다. 어댑터는 그 상태를
    /// 여기까지 흘려보내지 말고 빈틈없는 값으로 접어 넣는다.</summary>
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
