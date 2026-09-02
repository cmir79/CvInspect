using OpenCvSharp;

namespace CvInspect.Imaging;

/// <summary>CamFrame → Mat 연결 — 취득 프레임을 검사·표시(Mat 세계)로 넘길 때 쓴다.</summary>
public static class CamFrameMatExt
{
    /// <summary>
    /// 무복사 래핑 — 반환 Mat 은 <see cref="CamFrame.Pixels"/> 버퍼를 그대로 본다.
    /// dispose 는 핀 해제일 뿐이며 픽셀은 CamFrame 이 계속 소유한다(프레임이 살아 있는 한 언제든 유효).
    /// 픽셀을 고치는 연산에 넘길 거면 원본 오염을 피하도록 Clone() 후 작업하라.
    /// </summary>
    public static Mat AsMat(this CamFrame frame)
    {
        if (frame is null) throw new ArgumentNullException(nameof(frame));
        var type = frame.Format switch
        {
            CamPixelFormat.Mono8 => MatType.CV_8UC1,
            CamPixelFormat.Bgr24 => MatType.CV_8UC3,
            _ => MatType.CV_8UC4,
        };
        return Mat.FromPixelData(frame.Height, frame.Width, type, frame.Pixels, frame.Stride);
    }
}
