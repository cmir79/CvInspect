// 표시 프레임 픽셀 → BGRA 32비트 행 변환 — 화면 비트맵이 BGRA 한 형식만 받는 UI 어댑터가 Gray8·Bgr24 프레임을 옮길 때 쓴다.
// UI 프레임워크 무관: 원본·대상은 바이트 구간과 행 보폭(stride)으로만 받는다. 대상은 대개 비트맵 잠금 버퍼라 행 보폭이
// 원본과 다르다(폭 1001 의 BGRA 잠금 버퍼가 4004 가 아니라 정렬된 값을 줄 수 있다) — 그래서 행마다 따로 옮긴다.
//
// 결과 값: Gray8 은 B=G=R=v, A=255. Bgr24 는 B·G·R 그대로, A=255. 4채널(BGRA)은 그대로 복사한다(알파 포함).
// 근거(2448×2048 실측, 같은 기계): 프레임워크 내장 변환(Gray8·Bgr24 비트맵을 잠금 해제할 때 변환)은 7.4 / 8.5 ms 에 해제마다
// 프레임 크기×4 의 비관리 버퍼를 새로 할당하고 그 동안 비트맵 락을 쥐었다. 여기 벡터 경로는 1.9 / 3.5 ms 이고 바이트가 같았다.

#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif
using System.Runtime.InteropServices;

namespace CvInspect.Display;

/// <summary>8비트 1·3·4채널 픽셀 행 → BGRA(8비트 4채널) 행.</summary>
internal static class CvPixConv
{
    /// <summary>채널 수(1·3·4)에 맞는 변환으로 <paramref name="height"/> 행을 옮긴다. 원본 행은 <paramref name="width"/>×채널 바이트,
    /// 대상 행은 <paramref name="width"/>×4 바이트를 쓴다(행 끝 패딩은 건드리지 않는다).</summary>
    /// <exception cref="ArgumentOutOfRangeException">채널 수가 1·3·4 가 아니거나 치수·보폭이 음수·부족하다.</exception>
    /// <exception cref="ArgumentException">원본·대상 구간이 그 치수·보폭을 담기에 짧다.</exception>
    internal static void ToBgra(int channels, ReadOnlySpan<byte> src, int srcStride, Span<byte> dst, int dstStride, int width, int height)
    {
        switch (channels)
        {
            case 1:
                GrayToBgra(src, srcStride, dst, dstStride, width, height);
                break;
            case 3:
                BgrToBgra(src, srcStride, dst, dstStride, width, height);
                break;
            case 4:
                CopyBgra(src, srcStride, dst, dstStride, width, height);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be 1, 3 or 4.");
        }
    }

    /// <summary>Gray8 → BGRA: B=G=R=v, A=255.</summary>
    internal static void GrayToBgra(ReadOnlySpan<byte> src, int srcStride, Span<byte> dst, int dstStride, int width, int height)
    {
        Check(src.Length, srcStride, 1, dst.Length, dstStride, width, height);
        for (var y = 0; y < height; y++)
            GrayRow(src.Slice(y * srcStride, width), dst.Slice(y * dstStride, width * 4));
    }

    /// <summary>Bgr24 → BGRA: B·G·R 그대로, A=255.</summary>
    internal static void BgrToBgra(ReadOnlySpan<byte> src, int srcStride, Span<byte> dst, int dstStride, int width, int height)
    {
        Check(src.Length, srcStride, 3, dst.Length, dstStride, width, height);
        for (var y = 0; y < height; y++)
            BgrRow(src.Slice(y * srcStride, width * 3), dst.Slice(y * dstStride, width * 4));
    }

    /// <summary>BGRA → BGRA: 행 보폭만 맞춰 그대로 복사한다(알파 포함).</summary>
    internal static void CopyBgra(ReadOnlySpan<byte> src, int srcStride, Span<byte> dst, int dstStride, int width, int height)
    {
        Check(src.Length, srcStride, 4, dst.Length, dstStride, width, height);
        var row = width * 4;
        for (var y = 0; y < height; y++)
            src.Slice(y * srcStride, row).CopyTo(dst.Slice(y * dstStride, row));
    }

    /// <summary>치수·보폭·구간 길이 검사 — 마지막 행은 패딩 없이 끝나도 된다(행 바이트만 있으면 된다).</summary>
    private static void Check(int srcLen, int srcStride, int bpp, int dstLen, int dstStride, int width, int height)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must not be negative.");
        if (width == 0 || height == 0) return;
        if (srcStride < width * bpp)
            throw new ArgumentOutOfRangeException(nameof(srcStride), srcStride, $"Source stride is shorter than a row ({width * bpp} bytes).");
        if (dstStride < width * 4)
            throw new ArgumentOutOfRangeException(nameof(dstStride), dstStride, $"Destination stride is shorter than a row ({width * 4} bytes).");
        if ((long)srcStride * (height - 1) + (long)width * bpp > srcLen)
            throw new ArgumentException($"Source buffer is too short for {width}x{height} at stride {srcStride} (length {srcLen}).", "src");
        if ((long)dstStride * (height - 1) + (long)width * 4 > dstLen)
            throw new ArgumentException($"Destination buffer is too short for {width}x{height} at stride {dstStride} (length {dstLen}).", "dst");
    }

    // 스칼라 경로는 4바이트를 한 번에 쓴다 — BGRA 바이트 순서를 정수 하나로 만들 때 바이트 순서가 갈리므로 리틀 엔디언에서만 정수로 쓰고,
    // 그 밖에서는 바이트로 쓴다.

    private static void GrayRow(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var x = 0;
#if NET8_0_OR_GREATER
        // 16화소(16바이트) → 64바이트. 각 출력 화소 = (v, v, v, 0) 셔플 + 알파 OR. 셔플 색인 0x80(최상위 비트)은 0 을 낸다(x86·Arm 공통).
        if (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported)
        {
            var alpha = Vector128.Create(0xFF000000u).AsByte();
            var i0 = Vector128.Create((byte)0, 0, 0, 0x80, 1, 1, 1, 0x80, 2, 2, 2, 0x80, 3, 3, 3, 0x80);
            var i1 = Vector128.Create((byte)4, 4, 4, 0x80, 5, 5, 5, 0x80, 6, 6, 6, 0x80, 7, 7, 7, 0x80);
            var i2 = Vector128.Create((byte)8, 8, 8, 0x80, 9, 9, 9, 0x80, 10, 10, 10, 0x80, 11, 11, 11, 0x80);
            var i3 = Vector128.Create((byte)12, 12, 12, 0x80, 13, 13, 13, 0x80, 14, 14, 14, 0x80, 15, 15, 15, 0x80);
            ref var s = ref MemoryMarshal.GetReference(src);
            ref var d = ref MemoryMarshal.GetReference(dst);
            for (; x + 16 <= src.Length; x += 16)
            {
                var v = Vector128.LoadUnsafe(ref s, (nuint)x);
                var o = (nuint)(x * 4);
                (Shuffle(v, i0) | alpha).StoreUnsafe(ref d, o);
                (Shuffle(v, i1) | alpha).StoreUnsafe(ref d, o + 16);
                (Shuffle(v, i2) | alpha).StoreUnsafe(ref d, o + 32);
                (Shuffle(v, i3) | alpha).StoreUnsafe(ref d, o + 48);
            }
        }
#endif
        if (BitConverter.IsLittleEndian)
        {
            var d32 = MemoryMarshal.Cast<byte, uint>(dst);
            for (; x < src.Length; x++) d32[x] = 0xFF000000u | (src[x] * 0x010101u);
        }
        else
        {
            for (; x < src.Length; x++)
            {
                var v = src[x];
                var o = x * 4;
                dst[o] = v;
                dst[o + 1] = v;
                dst[o + 2] = v;
                dst[o + 3] = 255;
            }
        }
    }

    private static void BgrRow(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var w = src.Length / 3;
        var x = 0;
#if NET8_0_OR_GREATER
        // 4화소(12바이트)를 쓰려고 16바이트를 읽는다 — 원본 행 끝을 넘지 않게 뒤에 2화소 이상 남을 때만(x + 6 ≤ w).
        if (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported)
        {
            var alpha = Vector128.Create(0xFF000000u).AsByte();
            var idx = Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
            ref var s = ref MemoryMarshal.GetReference(src);
            ref var d = ref MemoryMarshal.GetReference(dst);
            for (; x + 6 <= w; x += 4)
            {
                var v = Vector128.LoadUnsafe(ref s, (nuint)(x * 3));
                (Shuffle(v, idx) | alpha).StoreUnsafe(ref d, (nuint)(x * 4));
            }
        }
#endif
        if (BitConverter.IsLittleEndian)
        {
            var d32 = MemoryMarshal.Cast<byte, uint>(dst);
            for (; x < w; x++)
            {
                var o = x * 3;
                d32[x] = 0xFF000000u | src[o] | ((uint)src[o + 1] << 8) | ((uint)src[o + 2] << 16);
            }
        }
        else
        {
            for (; x < w; x++)
            {
                var si = x * 3;
                var di = x * 4;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = 255;
            }
        }
    }

#if NET8_0_OR_GREATER
    // 하드웨어 바이트 셔플을 직접 부른다 — 범용 Vector128.Shuffle 은 색인이 상수로 접히지 않으면 소프트웨어 경로로 떨어질 수 있다.
    // 두 명령 모두 범위 밖 색인(0x80)에서 0 을 낸다.
    private static Vector128<byte> Shuffle(Vector128<byte> v, Vector128<byte> idx)
        => Ssse3.IsSupported ? Ssse3.Shuffle(v, idx) : AdvSimd.Arm64.VectorTableLookup(v, idx);
#endif
}
