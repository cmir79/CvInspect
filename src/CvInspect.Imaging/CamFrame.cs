using System.Runtime.InteropServices;
using OpenCvSharp;

namespace CvInspect.Imaging;

/// <summary>
/// 카메라가 발행하는 프레임 한 장 — GC 소유 byte[] 경계 타입.
/// 수명 계약이 없다: 이벤트 밖으로 들고 나가든 다른 스레드(UI 디스패처 등)로 넘기든 안전하다.
/// 검사(Mat 세계)로는 <see cref="CamFrameMatExt.AsMat"/> 로 무복사 래핑해 넘기고, 표시로는 <see cref="ICvPixelSource"/> 로
/// 그대로 건넨다 — 소비자가 배열을 참조로 붙잡으므로 <b>발행한 배열은 다시 쓰지 않는다</b>(어댑터 책임).
/// 벤더 어댑터는 SDK 버퍼를 byte[] 로 실체화해 이 타입으로 발행한다 — Mat 경유가 편하면
/// <see cref="FromMat"/> 을 쓴다.
/// </summary>
public sealed class CamFrame : ICvPixelSource
{
    public CamFrame(byte[] pixels, int width, int height, int stride, CamPixelFormat format,
                    TimeSpan? deviceTimestamp = null)
    {
        DeviceTimestamp = deviceTimestamp;
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

    /// <summary>1=Mono8, 3=Bgr24, 4=Bgra32 — <see cref="ICvPixelSource"/> 계약. 열거형에 새 포맷이 들어오면 여기서 던진다:
    /// 조용히 4 로 접으면 표시가 엉뚱한 채널 수로 픽셀을 읽는다.</summary>
    public int Channels => Format switch
    {
        CamPixelFormat.Mono8 => 1,
        CamPixelFormat.Bgr24 => 3,
        CamPixelFormat.Bgra32 => 4,
        _ => throw new NotSupportedException($"CamPixelFormat.{Format} has no channel mapping."),
    };

    /// <summary>이 객체가 만들어진 시각(UTC) — 즉 <b>호스트에 도착한 시각</b>이지 촬영 시각이 아니다.
    /// 전송과 대기열에 걸린 시간이 이미 지난 뒤의 값이다. 촬영 시각은 <see cref="DeviceTimestamp"/> 를 본다.</summary>
    public DateTime TimestampUtc { get; }

    /// <summary>
    /// 장치가 찍은 촬영 시각. 장치가 제공하지 않으면 null.
    ///
    /// <b>절대 시각이 아니다</b> — 기준점은 장치가 정하며(대개 전원 인가 시점) 호스트 시계와 무관하다.
    /// 그래서 두 가지로만 쓴다: 프레임 사이의 <b>간격</b>, 그리고 <see cref="TimestampUtc"/> 와의 <b>차이</b>.
    ///
    /// 차이는 그 자체로는 의미가 없고(시계 기준점이 다르다) <b>차이의 변화</b>가 대기 시간이다 —
    /// 한 구간에서 관측한 최소 차이를 0 으로 놓으면, 각 프레임이 그보다 초과한 만큼이
    /// 전송 뒤 어딘가에서 앉아 있던 시간이다. 화면이 밀리는데 처리량은 정상일 때 이 값이 원인을 가른다.
    /// </summary>
    public TimeSpan? DeviceTimestamp { get; }

    /// <summary>Mat → CamFrame 실체화(픽셀 복사) — 소스 구현이 발행 직전에 쓴다.
    /// 8UC1/8UC3/8UC4 만 지원(그 외는 예외). 타이트 패킹(stride = 폭×채널)으로 담는다.</summary>
    public static CamFrame FromMat(Mat mat, TimeSpan? deviceTimestamp = null)
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
        return new CamFrame(buf, mat.Width, mat.Height, stride, fmt, deviceTimestamp);
    }
}
