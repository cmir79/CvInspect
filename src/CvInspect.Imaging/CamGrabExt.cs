namespace CvInspect.Imaging;

/// <summary>단발 그랩을 프레임으로 받는 확장 — 어느 <see cref="ICam"/> 에나 붙는다.</summary>
public static class CamGrabExt
{
    /// <summary>시한을 <see cref="Timeout.InfiniteTimeSpan"/> 으로 준 기본 절차에서, <see cref="ICam.GrabOne"/> 이
    /// 장 없이 돌아온 뒤 늦은 발행을 기다리는 유예(ms).
    ///
    /// 늦은 발행은 프레임이 벤더 콜백으로 들어오는 구현에서 반환 직후에 온다. 이 툴킷의 구현은 돌아오기 전에 발행하거나
    /// 이 절차를 타지 않으므로(VirtualCam·VideoCaptureCam 은 동기 발행, DeadCam·GevCam 은 자기 구현, ReconnectingCam 은
    /// 안쪽으로 넘긴다) 이 유예는 <b>밖의 구현을 위한 여유값</b>이다 —
    /// 잰 지연이 아니다(회귀가 흉내내는 지연은 120ms). 넉넉히 잡은 대가는 "장이 끝내 안 오는" 그랩에서만 이만큼 늦는 것뿐이다.
    /// 시한을 값으로 준 호출은 이 유예가 아니라 남은 시한을 기다린다.</summary>
    internal const int LatePublishGraceMs = 1000;

    /// <summary>
    /// 한 장을 찍어 돌려준다. <see cref="ICamGrabAsync"/> 를 구현한 카메라는 <b>그 구현이 불리고</b>,
    /// 아니면 아래 기본 절차가 돈다 — 구독을 걸고, <see cref="ICam.GrabOne"/> 을 부르고, 그 사이에 발행된
    /// 장을 집고, 구독을 푼다. 소비자마다 손으로 짜던 그 네 단계다.
    ///
    /// 돌려주는 값과 던지는 것의 경계는 <see cref="ICamGrabAsync.GrabFrameAsync"/> 에 적힌 그대로다 —
    /// <c>null</c> 은 "이 호출의 장이 없었고 던질 사유도 없다" 이고, 답해야 하는데 못 하는 상태는 던진다.
    /// <b>기본 절차의 시한 만료는 <c>null</c> 이지만, 표식을 단 구현은 시한 만료를 사유와 함께
    /// <see cref="TimeoutException"/> 으로 던질 수 있다</b>(<c>GevCam</c>) — 백엔드를 가리지 않는 호출자는 둘 다 받는다.
    ///
    /// <b>기본 절차가 못 하는 것</b>(그래서 콜백으로 프레임을 받는 구현은 <see cref="ICamGrabAsync"/> 를 단다):
    /// <list type="bullet">
    /// <item><b>짝을 구조적으로 보장하지 못한다.</b> 구독이 걸려 있는 동안 도착한 <b>아무 장</b>이나 집는다 —
    /// 이 호출이 부른 그랩의 장인지는 모른다. 가릴 수 있는 것은 장치 쪽 짝짓기 근거를 쥔 구현 자신뿐이다.</item>
    /// <item><b>"영영 안 온다" 를 알지 못한다.</b> 그래서 장이 없으면 시한을 끝까지 기다린다(시한이
    /// <see cref="Timeout.InfiniteTimeSpan"/> 이면 기다릴 시한이 없으므로 GrabOne 이 돌아온 뒤 1초 유예만) —
    /// 그 답을 즉시 아는 구현은 <see cref="ICamGrabAsync"/> 를 달아 스스로 답한다.</item>
    /// <item><b>시한이 끝나도 진행 중인 그랩을 끊지 못한다.</b> <see cref="ICam.GrabOne"/> 에는 취소 인자가
    /// 없다. 시한이 다하면 여기서는 <c>null</c> 로 돌아가지만 그랩은 계속 돌고, 늦게 도착한 장은
    /// <b>카메라의 다른 구독자에게</b> 나간다(우리 구독은 이미 풀렸다).</item>
    /// </list></summary>
    /// <param name="cam">찍을 카메라.</param>
    /// <param name="timeout">이 호출의 시한. 구현 설정값보다 우선한다.
    /// <see cref="Timeout.InfiniteTimeSpan"/> 이면 구현 자신의 시한에 맡긴다 — 기본 절차에서는 <see cref="ICam.GrabOne"/>
    /// 의 시한이 그랩을 묶고, 그 그랩이 장 없이 돌아오면 늦은 발행을 1초 유예만 기다린 뒤 <c>null</c> 이다.
    /// 0 이하는 1ms 로, 아주 큰 값은 int 밀리초 상한으로 다듬는다(<c>GevCam</c> 과 같은 규칙).</param>
    /// <param name="ct">취소. 시한 만료(<c>null</c>)와 달리 <see cref="OperationCanceledException"/> 으로 나간다.</param>
    public static async Task<CamFrame?> GrabFrameAsync(this ICam cam, TimeSpan timeout, CancellationToken ct = default)
    {
        if (cam is null) throw new ArgumentNullException(nameof(cam));
        if (cam is ICamGrabAsync native) return await native.GrabFrameAsync(timeout, ct).ConfigureAwait(false);

        // 발행 스레드에서 이어달리기가 돌지 않게 한다 — 그 스레드는 취득 계층의 것이고, 거기서 호출자
        // 코드를 태우면 다음 프레임이 밀린다.
        var tcs = new TaskCompletionSource<CamFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(object? _, CamFrame f) => tcs.TrySetResult(f);

        // 시한은 그랩을 띄우기 **전에** 다듬는다. 뒤에 Task.Delay 가 범위 밖 값(음수·아주 큰 값)으로 던지면 그랩만
        // 고아로 돌아, 그 장은 다른 구독자에게 가고 GrabOne 의 예외는 아무도 안 본다.
        var wait = timeout == Timeout.InfiniteTimeSpan
            ? timeout
            : TimeSpan.FromMilliseconds(Math.Min(int.MaxValue, Math.Max(1, timeout.TotalMilliseconds)));

        // 기다림은 부른 쪽 토큰에 직접 걸지 않고 연결한 취소원에 건다 — 끝날 때 끊어 등록을 돌려준다. 무한 시한의
        // Task.Delay 는 스스로 끝나지 않아, 부른 쪽 토큰에 바로 걸면 호출마다 등록이 하나씩 남는다(오래 사는 토큰이면 쌓인다).
        using var waits = CancellationTokenSource.CreateLinkedTokenSource(ct);

        cam.FrameAcquired += OnFrame;
        try
        {
            // GrabOne 은 부른 쪽을 붙잡는다(ICam 계약: 프레임이 오거나 시한이 다할 때까지 안 돌아온다).
            // 그대로 부르면 이 메서드가 비동기인 의미가 없으므로 풀 스레드로 옮긴다. 옮기는 김에
            // "UI 스레드에서 부르면 동기 마샬링이 영영 안 돌아온다" 는 그쪽 주의도 함께 비켜간다.
            var grab = Task.Run(cam.GrabOne, ct);
            // 시한 시계는 **여기서** 출발한다. 그랩이 끝난 뒤부터 다시 세면 부른 쪽이 준 상한이 실제로는
            // "그랩 시간 + 시한" 이 되어 약속을 말없이 넘는다 — 아래 둘째 대기도 이 같은 객체를 다시 쓴다.
            var deadline = Task.Delay(wait, waits.Token);

            var first = await Task.WhenAny(tcs.Task, grab, deadline).ConfigureAwait(false);
            if (ReferenceEquals(first, grab))
            {
                await grab.ConfigureAwait(false);   // 던졌으면 그 사유를 그대로, 그리고 즉시 올린다

                // ⚠ 그랩이 돌아왔다고 곧장 null 로 닫지 않는다. 대부분의 구현은 돌아오기 전에 발행을
                // 마치지만(그래서 여기서 tcs 는 대개 이미 완료다), **발행이 반환보다 살짝 늦는 구현**이
                // 있다 — 프레임이 벤더 콜백으로 들어오는 취득 계층이 그렇다. 거기서 단락하면 <b>정상
                // 그랩이 거짓 null</b> 이 된다. 남은 시한까지는 기다린다.
                // 그 대가로 "영영 안 오는" 구현은 시한을 다 태운다. 그것을 즉시 알 수 있는 구현은
                // ICamGrabAsync 를 달아 스스로 답한다(DeadCam 이 그렇게 한다) — 기본 절차가
                // 밖에서 넘겨짚는 것보다 그쪽이 옳다.
                // ⚠ 시한이 InfiniteTimeSpan("구현의 시한에 맡긴다")이면 남은 시한이 없다 — 구현의 시한은 방금 돌아온
                // GrabOne 이 이미 썼다. 그대로 deadline 을 기다리면 장 없이 돌아온 그랩(예: 읽기에 실패한
                // VideoCaptureCam)에서 영영 선다. 그래서 늦은 발행은 유예만큼만 기다리고 null 로 닫는다.
                var rest = wait == Timeout.InfiniteTimeSpan ? Task.Delay(LatePublishGraceMs, waits.Token) : deadline;
                first = await Task.WhenAny(tcs.Task, rest).ConfigureAwait(false);
            }

            if (ReferenceEquals(first, tcs.Task)) return await tcs.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();   // 취소는 시한 만료가 아니다 — 부른 쪽이 갈라 알아야 한다
            return null;                         // 시한 만료
        }
        finally
        {
            waits.Cancel();   // 남은 기다림(무한 시한·유예)의 타이머와 등록을 돌려준다
            cam.FrameAcquired -= OnFrame;
        }
    }
}
