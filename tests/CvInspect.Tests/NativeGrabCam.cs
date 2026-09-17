namespace CvInspect.Tests;

/// <summary>ICamGrabAsync 를 직접 구현한 카메라 — 확장이 기본 절차 대신 이쪽을 부르는지 가른다.
/// GrabOne 은 일부러 던지게 두었다: 기본 절차가 돌면 그것이 불려 시험이 실패한다(조용히 통과하지 않는다).</summary>
sealed class NativeGrabCam : CvInspect.Imaging.ICam, CvInspect.Imaging.ICamGrabAsync
{
    public int NativeCalls;
    public int GrabCalls;

    public string Name => "NativeGrabCam";
    public string ComType => "Fake";
    public bool IsConnected => true;
    public bool IsGrabbing => false;

    public event EventHandler<CvInspect.Imaging.CamFrame>? FrameAcquired;
    public event EventHandler<CvInspect.Imaging.ConnArgs>? ConnectionChanged;
    public event EventHandler<bool>? GrabbingChanged;

    public Task<CvInspect.Imaging.CamFrame?> GrabFrameAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Interlocked.Increment(ref NativeCalls);
        var frame = new CvInspect.Imaging.CamFrame(new byte[4], 2, 2, 2, CvInspect.Imaging.CamPixelFormat.Mono8);
        FrameAcquired?.Invoke(this, frame);   // 같은 취득이므로 구독자에게도 나간다
        return Task.FromResult<CvInspect.Imaging.CamFrame?>(frame);
    }

    public void Open() => ConnectionChanged?.Invoke(this, new CvInspect.Imaging.ConnArgs(true));
    public void Close() => ConnectionChanged?.Invoke(this, new CvInspect.Imaging.ConnArgs(false));
    public void GrabOne()
    {
        Interlocked.Increment(ref GrabCalls);
        throw new InvalidOperationException("the default path must not be used when ICamGrabAsync is implemented");
    }
    public void StartContinuous() => GrabbingChanged?.Invoke(this, true);
    public void StopContinuous() => GrabbingChanged?.Invoke(this, false);
    public void SetExposureTimeUs(double timeUs) { }
    public void Dispose() { }
}
