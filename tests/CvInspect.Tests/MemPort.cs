namespace CvInspect.Tests;

/// <summary>장치 없이 GenApi 노드 맵을 돌리는 메모리 포트 — 레지스터는 바이트 배열이고, 읽기 실패와 "쓰면 장치가 무엇을 바꾸는가" 를
/// 명령으로 흉내낸다. 파싱한 노드 맵(<c>GenApiNodeMap.Parse</c>)에 물려 GevCam 의 장치 경로를 그대로 부른다.</summary>
sealed class MemPort : GevSharp.IGevPort
{
    public readonly byte[] Mem = new byte[0x1000];

    /// <summary>이 주소를 읽으면 전송 시한 초과를 던진다 — 장치 상태 오류·시한 초과는 GenApiException 이 아니다.</summary>
    public ulong? TimeoutOnReadAt { get; set; }

    /// <summary>쓰기 뒤 장치가 스스로 하는 일(예: 노출을 쓰면 자동 노출을 끄는 기종).</summary>
    public Action<ulong>? AfterWrite { get; set; }

    public int Reads;
    public int Writes;

    public ValueTask ReadAsync(ulong address, Memory<byte> buffer, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Reads);
        if (TimeoutOnReadAt == address) throw new GevSharp.GevTimeoutException("fake read timeout");
        Mem.AsMemory((int)address, buffer.Length).CopyTo(buffer);
        return default;
    }

    public ValueTask WriteAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Writes);
        data.CopyTo(Mem.AsMemory((int)address));
        AfterWrite?.Invoke(address);
        return default;
    }

    /// <summary>빅엔디언 4바이트 정수 레지스터 값.</summary>
    public void SetU32(ulong address, uint value)
    {
        Mem[address] = (byte)(value >> 24);
        Mem[address + 1] = (byte)(value >> 16);
        Mem[address + 2] = (byte)(value >> 8);
        Mem[address + 3] = (byte)value;
    }

    /// <summary>빅엔디언 8바이트 실수 레지스터 값.</summary>
    public double GetF64(ulong address)
    {
        var b = Mem.AsSpan((int)address, 8).ToArray();
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        return BitConverter.ToDouble(b, 0);
    }
}
