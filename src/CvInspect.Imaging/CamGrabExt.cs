namespace CvInspect.Imaging;

/// <summary>단발 그랩을 프레임으로 받는 확장 — 어느 <see cref="ICam"/> 에나 붙는다.</summary>
public static class CamGrabExt
{
    /// <summary>시한을 <see cref="Timeout.InfiniteTimeSpan"/> 으로 준 기본 절차에서 기다릴 시한 대신 쓰는 유예(ms). 두 자리에 쓴다 —
    /// <see cref="ICam.GrabOne"/> 이 장 없이 돌아온 뒤 <b>늦은 발행</b>을 기다릴 때, 그리고 장을 받은 뒤 아직 안 돌아온
    /// <b>GrabOne 의 꼬리</b>(우리 뒤의 구독자·그랩의 뒷정리)를 기다릴 때.
    ///
    /// 늦은 발행은 프레임이 벤더 콜백으로 들어오는 구현에서 반환 직후에 온다. 이 툴킷의 구현은 돌아오기 전에 발행하거나
    /// 이 절차를 타지 않으므로(VirtualCam·VideoCaptureCam 은 동기 발행, DeadCam·GevCam 은 자기 구현, ReconnectingCam 은
    /// 안쪽으로 넘긴다) 이 유예는 <b>밖의 구현을 위한 여유값</b>이다 —
    /// 잰 지연이 아니다(회귀가 흉내내는 지연은 120ms). 넉넉히 잡은 대가는 "장이 끝내 안 오는" 그랩에서만 이만큼 늦는 것뿐이다.
    /// 꼬리 쪽은 보통 마이크로초로 끝나고(동기 발행 구현의 뒷정리), 이 값까지 가는 것은 우리 뒤의 구독자가 장을 붙잡을 때뿐이다 — 그때도
    /// 받은 장은 그대로 돌려주고, 카메라는 아직 그 그랩을 쥐고 있을 수 있다.
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
    /// <b>장을 들고 돌아올 때는 기본 절차의 <see cref="ICam.GrabOne"/> 도 돌아와 있다</b> — 그래서 곧바로 건 다음 그랩·연속 시작이
    /// "이미 기다리는 그랩이 있다" 로 거절되지 않는다. 장은 GrabOne 안에서 발행되므로 받은 순간에는 GrabOne 이 아직 남은 구독자에게
    /// 넘기거나 뒷정리하는 중이고, 그것을 기다린 뒤에 돌아온다. 단 같은 시한까지만 기다린다(무한 시한이면 1초 유예) — 우리 뒤의 구독자가
    /// 장을 그보다 오래 붙잡으면 장을 시한에 돌려주고, 그때는 카메라가 아직 그 그랩을 쥐고 있을 수 있다. 그 대가로 우리 뒤의 느린 구독자는
    /// 이 호출을 그만큼 늦춘다 — UI 스레드에서 이 작업을 동기로 기다리는데(<c>.Result</c>) 그 구독자가 UI 스레드로 동기 마샬링하면 시한
    /// 내내 선다(그 뒤 장은 돌아온다).
    ///
    /// <b><see cref="ICam.GrabOne"/> 이 던졌으면 그 사이 장이 와 있어도 던진다.</b> 로컬 카메라는 발행 중 구독자 예외를 삼키므로 이 갈래는
    /// 진입 거절(연속 취득 중·다른 그랩 대기 중)이다 — 그때 구독에 든 장은 라이브나 남의 그랩의 장이고, 그것을 답으로 내면 거절이
    /// 정상 답으로 덮인다. 발행한 뒤에 던지는 밖의 구현도 같다 — 던진 GrabOne 은 실패다.
    ///
    /// <b>기본 절차가 못 하는 것</b>(그래서 콜백으로 프레임을 받는 구현은 <see cref="ICamGrabAsync"/> 를 단다):
    /// <list type="bullet">
    /// <item><b>짝을 구조적으로 보장하지 못한다.</b> 이 호출의 GrabOne 이 시작된 뒤 도착한 <b>아무 장</b>이나 집는다 —
    /// 이 호출이 부른 그랩의 장인지는 모른다(시작 전에 온 장은 버린다 — 이 호출 뒤에 온 장이 아니다). 가릴 수 있는 것은 장치 쪽 짝짓기
    /// 근거를 쥔 구현 자신뿐이다. 남는 자리 셋: 시한 안에 안 끝난 그랩의 장(시한에 그대로 돌려준다), GrabOne 이 시작된 뒤 진입 검사 전에
    /// 남의 그랩이 장을 내고 표식까지 내린 경우(이 그랩은 받아들여지고 남의 장을 돌려준다 — 짧은 창이다), GrabOne 이 장 없이 돌아온 뒤
    /// 늦은 발행을 기다리는 동안 다른 스레드의 그랩이나 연속 시작이 받아들여진 경우(그 장을 집는다 — 읽기에 실패한 VideoCaptureCam 등).</item>
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
        // 이 호출의 GrabOne 이 시작되기 전에 온 장은 집지 않는다 — GrabOne 은 "이 호출 뒤에 오는 프레임" 을 내므로 그 장은 답일 수 없다.
        // 구독과 그랩 시작 사이(풀 스레드가 본문을 집기까지)에 라이브 틱이나 앞 그랩의 장이 들어와 답으로 나갔다(검토 실측: 시작 전에
        // 취소된 호출이 GrabOne 을 한 번도 안 부르고 그 장을 돌려줬다 20/20).
        var started = 0;
        void OnFrame(object? _, CamFrame f)
        {
            if (Volatile.Read(ref started) != 0) tcs.TrySetResult(f);
        }

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
            var grab = Task.Run(() =>
            {
                Volatile.Write(ref started, 1);
                cam.GrabOne();
            }, ct);
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

            if (ReferenceEquals(first, tcs.Task))
            {
                // ⚠ 장을 받았다고 곧장 돌아가지 않는다 — GrabOne 이 아직 안 돌아왔을 수 있다. 발행은 GrabOne 안에서 일어나서, 우리 구독자가
                // 장을 받은 뒤에도 그 뒤의 구독자와 그랩의 뒷정리(로컬 카메라의 "단발 그랩 진행 중" 표식 내리기)가 남아 있다. 그때 돌아가면
                // 부른 쪽이 곧바로 건 그랩·연속 시작이 "이미 기다리는 그랩이 있다" 로 거절된다(가상 카메라 실측: 반환 0 ms 뒤 19/20, 1 ms 뒤 0/20).
                // 그래서 그랩이 돌아오기를 기다린다 — 단 같은 시한까지만(무한 시한이면 유예까지만): 우리 뒤의 구독자가 장을 오래 붙잡으면
                // 시한 약속이 깨진다. 시한이 다하거나 취소되면 받은 장을 돌려준다(종전과 같다).
                if (!grab.IsCompleted)
                {
                    var tail = wait == Timeout.InfiniteTimeSpan ? Task.Delay(LatePublishGraceMs, waits.Token) : deadline;
                    await Task.WhenAny(grab, tail).ConfigureAwait(false);
                }
                // ⚠ 그랩이 던졌으면 장이 와 있어도 그 사유를 올린다. 로컬 카메라는 발행 중 구독자 예외를 삼키므로 여기 오는 것은 진입 거절이고
                // (연속 취득 중·다른 그랩 대기 중), 그때 우리 구독이 집은 장은 라이브의 틱이나 남의 그랩의 장이다. 종전에는 그 장을 답으로 내
                // 거절이 정상 답으로 덮였다(재현: 라이브 중 부른 그랩이 라이브 장을 받아 왔다 — 장치처럼 읽기에 락을 오래 쥐는 소스에서는 대부분).
                // 시한 안에 안 끝난 그랩은 종전처럼 고아로 돈다 — 그 끝(나중의 거절 포함)은 아무도 보지 않는다. 그 밖에 남는 틈은 summary 첫 항목.
                if (grab.IsFaulted || grab.IsCanceled) await grab.ConfigureAwait(false);
                return await tcs.Task.ConfigureAwait(false);
            }
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
