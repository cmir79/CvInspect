using OpenCvSharp;

namespace CvInspect.Imaging;

/// <summary>
/// 카메라가 발행하는 프레임 한 장.
/// <see cref="Frame"/> 은 <b>이벤트 호출 동안만 유효</b>하다 — 고속 취득에서 프레임마다 새 버퍼를
/// 만들지 않도록 발행자가 호출 후 버퍼를 재사용/해제할 수 있다. 프레임을 보관하거나 다른 스레드
/// (UI 디스패처 등)로 넘기려면 반드시 <c>Frame.Clone()</c> 을 넘겨라.
/// </summary>
public sealed class CamFrameEvt : EventArgs
{
    public CamFrameEvt(Mat frame, DateTime timestampUtc)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        TimestampUtc = timestampUtc;
    }

    /// <summary>프레임 이미지 — 이벤트 호출 동안만 유효. 보관하려면 Clone().</summary>
    public Mat Frame { get; }

    /// <summary>취득 시각 (UTC).</summary>
    public DateTime TimestampUtc { get; }
}
