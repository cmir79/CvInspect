namespace CvInspect.Imaging;

/// <summary>단발 그랩을 프레임으로 받는 확장 — 어느 <see cref="ICam"/> 에나 붙는다.</summary>
public static class CamGrabExt
{
    /// <summary>
    /// 한 장을 찍어 돌려준다. <see cref="ICamGrabAsync"/> 를 구현한 카메라는 <b>그 구현이 불리고</b>,
    /// 아니면 아래 기본 절차가 돈다 — 구독을 걸고, <see cref="ICam.GrabOne"/> 을 부르고, 그 사이에 발행된
    /// 장을 집고, 구독을 푼다. 소비자마다 손으로 짜던 그 네 단계다.
    ///
    /// 돌려주는 값과 던지는 것의 경계는 <see cref="ICamGrabAsync.GrabFrameAsync"/> 에 적힌 그대로다 —
    /// <c>null</c> 은 시한 만료나 "애초에 답 못 하는 구현" 이고, 답해야 하는데 못 하는 상태는 던진다.
    ///
    /// <b>기본 절차가 못 하는 것</b>(그래서 콜백으로 프레임을 받는 구현은 <see cref="ICamGrabAsync"/> 를 단다):
    /// <list type="bullet">
    /// <item><b>짝을 구조적으로 보장하지 못한다.</b> 구독이 걸려 있는 동안 도착한 <b>아무 장</b>이나 집는다 —
    /// 이 호출이 부른 그랩의 장인지는 모른다. 가릴 수 있는 것은 장치 쪽 짝짓기 근거를 쥔 구현 자신뿐이다.</item>
    /// <item><b>시한이 끝나도 진행 중인 그랩을 끊지 못한다.</b> <see cref="ICam.GrabOne"/> 에는 취소 인자가
    /// 없다. 시한이 다하면 여기서는 <c>null</c> 로 돌아가지만 그랩은 계속 돌고, 늦게 도착한 장은
    /// <b>카메라의 다른 구독자에게</b> 나간다(우리 구독은 이미 풀렸다).</item>
    /// </list></summary>
    /// <param name="timeout">이 호출의 시한. 구현 설정값보다 우선한다.
    /// <see cref="Timeout.InfiniteTimeSpan"/> 이면 구현 자신의 시한에 맡긴다.</param>
    public static async Task<CamFrame?> GrabFrameAsync(this ICam cam, TimeSpan timeout, CancellationToken ct = default)
    {
        if (cam is null) throw new ArgumentNullException(nameof(cam));
        if (cam is ICamGrabAsync native) return await native.GrabFrameAsync(timeout, ct).ConfigureAwait(false);

        // 발행 스레드에서 이어달리기가 돌지 않게 한다 — 그 스레드는 취득 계층의 것이고, 거기서 호출자
        // 코드를 태우면 다음 프레임이 밀린다.
        var tcs = new TaskCompletionSource<CamFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(object? _, CamFrame f) => tcs.TrySetResult(f);

        cam.FrameAcquired += OnFrame;
        try
        {
            // GrabOne 은 부른 쪽을 붙잡는다(ICam 계약: 프레임이 오거나 시한이 다할 때까지 안 돌아온다).
            // 그대로 부르면 이 메서드가 비동기인 의미가 없으므로 풀 스레드로 옮긴다. 옮기는 김에
            // "UI 스레드에서 부르면 동기 마샬링이 영영 안 돌아온다" 는 그쪽 주의도 함께 비켜간다.
            var grab = Task.Run(cam.GrabOne, ct);
            var finished = await Task.WhenAny(grab, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, grab))
            {
                ct.ThrowIfCancellationRequested();   // 취소는 시한 만료가 아니다 — 부른 쪽이 갈라 알아야 한다
                return null;                         // 시한 만료
            }

            await grab.ConfigureAwait(false);        // 그랩이 던졌으면 그 사유를 그대로 올린다
            return tcs.Task.IsCompleted ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            cam.FrameAcquired -= OnFrame;
        }
    }
}
