// 표시 프레임 → BGRA 행 변환(CvInspect.Display.CvPixConv)의 회귀. 화면 비트맵을 BGRA 한 형식으로만 두는 UI 어댑터가
// Gray8·Bgr24 프레임을 그 잠금 버퍼에 옮길 때 쓴다 — 틀리면 예외 없이 색만 틀린 그림이 나와 "정상처럼 보이는" 결함이 된다.
// 못 박는 것: 값 규칙(Gray B=G=R=v·A=255, Bgr 채널 순서 그대로·A=255, Bgra 알파까지 그대로) · 벡터 경로와 꼬리 스칼라 경로의
// 경계(폭 1..40, 1001) · 원본·대상 행 보폭이 서로 다른 경우(대상 행 끝 패딩은 건드리지 않는다) · 마지막 행이 패딩 없이 끝나는 원본 ·
// OpenCV cvtColor 와 바이트 대조(독립 기준) · 짧은 버퍼·보폭은 던진다.
// 못 박지 못하는 것: 원본 행·배열 끝을 넘어 읽기 자체(Compare 의 주석) — 출력에 섞이지 않는 넘어 읽기는 관리 배열로는 보이지 않는다.
using CvInspect.Display;
using OpenCvSharp;
using Xunit;

namespace CvInspect.Tests;

public class CvPixConvTests
{
    private const byte Pad = 0xA5;   // 대상 행 끝 패딩 표식 — 변환이 이 바이트를 덮으면 이웃 행·비트맵 정렬 영역을 깬 것이다

    /// <summary>바이트 하나씩 정의대로 만든 기준 — 시험 대상과 다른 방식(정수 묶음·셔플 없음)이라 같은 실수를 공유하지 않는다.</summary>
    private static void Reference(int ch, byte[] src, int srcStride, byte[] dst, int dstStride, int w, int h)
    {
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var s = y * srcStride + x * ch;
            var d = y * dstStride + x * 4;
            switch (ch)
            {
                case 1:
                    dst[d] = dst[d + 1] = dst[d + 2] = src[s];
                    dst[d + 3] = 255;
                    break;
                case 3:
                    dst[d] = src[s];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                    dst[d + 3] = 255;
                    break;
                default:
                    dst[d] = src[s];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                    dst[d + 3] = src[s + 3];
                    break;
            }
        }
    }

    /// <summary>원본(마지막 행은 패딩 없이 끝남)·대상(패딩 표식으로 채움)을 만들어 변환과 기준을 대조한다.</summary>
    private static void Compare(int ch, int w, int h, int srcPad, int dstPad, int seed)
    {
        var srcStride = w * ch + srcPad;
        // 마지막 행 뒤 여유 없음 — 길이 검사(Check)가 꼭 맞는 길이를 받아들이는지를 본다. 넘어 읽기를 잡지는 못한다: 벡터 경로는
        // LoadUnsafe 라 경계 검사가 없어 배열 끝을 넘어 읽어도 던지지 않고, Bgr 경로는 16바이트를 읽어 12바이트만 쓰므로 넘어 읽은
        // 4바이트는 출력에 섞이지 않는다. 그래서 읽기 쪽 행 경계는 코드의 루프 조건(x + 6 ≤ w, x + 16 ≤ 행 길이)에 기대고,
        // 이 시험이 잡는 것은 넘어 읽은 값이 출력에 섞이는 경우(무작위 원본·패딩과 기준 대조)와 대상 쪽 넘어 쓰기(Pad 표식)까지다.
        var src = new byte[srcStride * (h - 1) + w * ch];
        new Random(seed).NextBytes(src);
        var dstStride = w * 4 + dstPad;
        var got = new byte[dstStride * h];
        var want = new byte[dstStride * h];
        got.AsSpan().Fill(Pad);
        want.AsSpan().Fill(Pad);

        CvPixConv.ToBgra(ch, src, srcStride, got, dstStride, w, h);
        Reference(ch, src, srcStride, want, dstStride, w, h);

        for (var i = 0; i < want.Length; i++)
        {
            if (got[i] == want[i]) continue;
            var y = i / dstStride;
            var col = i % dstStride;
            var where = col >= w * 4 ? $"row {y} padding byte {col - w * 4}" : $"pixel ({col / 4},{y}) channel {col % 4}";
            Assert.Fail($"ch={ch} w={w} h={h} srcPad={srcPad} dstPad={dstPad}: byte {i} at {where} is {got[i]}, expected {want[i]} — " +
                "a wrong byte here is a frame shown with wrong colors or a corrupted neighbor row, and nothing throws.");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryWidthAcrossTheVectorAndTailPathsMatchesTheDefinition(int ch)
    {
        // 폭 1..40 은 벡터 한 묶음(Gray 16화소, Bgr 4화소 + 2화소 여유) 앞뒤의 꼬리 경로를 전부 지난다.
        for (var w = 1; w <= 40; w++) Compare(ch, w, 3, 0, 0, w);
        Compare(ch, 1001, 4, 0, 0, 1001);
    }

    [Theory]
    [InlineData(1, 0, 4)]
    [InlineData(1, 3, 0)]
    [InlineData(1, 7, 12)]
    [InlineData(3, 0, 4)]
    [InlineData(3, 1, 0)]
    [InlineData(3, 5, 12)]
    [InlineData(4, 0, 4)]
    [InlineData(4, 4, 0)]
    [InlineData(4, 8, 12)]
    public void SourceAndDestinationStridesMayDiffer(int ch, int srcPad, int dstPad)
    {
        // 비트맵 잠금 버퍼는 행을 정렬해 주므로 원본(공급자 Stride, 또는 Mat 의 타이트 행)과 보폭이 다르다 — 폭 1001 이 그 예다.
        foreach (var w in new[] { 1, 15, 17, 1001 }) Compare(ch, w, 5, srcPad, dstPad, w * 31 + srcPad);
    }

    private static byte[] Bytes(Mat m)
    {
        var a = new byte[m.Total() * m.ElemSize()];
        System.Runtime.InteropServices.Marshal.Copy(m.Data, a, 0, a.Length);
        return a;
    }

    [Fact]
    public void MatchesOpenCvColorConversionByteForByte()
    {
        // 독립 기준 — 위 기준 함수와 시험 대상이 같은 오해(채널 순서 등)를 함께 할 수 있으니 OpenCV 변환과도 맞춘다.
        const int w = 1001, h = 7;
        using var gray = new Mat(h, w, MatType.CV_8UC1);
        using var bgr = new Mat(h, w, MatType.CV_8UC3);
        Cv2.Randu(gray, Scalar.All(0), Scalar.All(256));
        Cv2.Randu(bgr, Scalar.All(0), Scalar.All(256));
        foreach (var (mat, code, ch) in new[] { (gray, ColorConversionCodes.GRAY2BGRA, 1), (bgr, ColorConversionCodes.BGR2BGRA, 3) })
        {
            var src = Bytes(mat);
            using var cv = new Mat();
            Cv2.CvtColor(mat, cv, code);
            var want = Bytes(cv);
            var got = new byte[w * 4 * h];
            CvPixConv.ToBgra(ch, src, w * ch, got, w * 4, w, h);
            Assert.True(want.AsSpan().SequenceEqual(got),
                $"ch={ch}: BGRA conversion differs from OpenCV {code} — the display would show a frame whose colors differ from what the tools measured.");
        }
    }

    [Fact]
    public void ShortBuffersAndStridesThrowInsteadOfWritingPastTheEnd()
    {
        var src = new byte[10 * 3];
        var dst = new byte[10 * 4 * 3];
        Assert.Throws<ArgumentException>(() => CvPixConv.ToBgra(1, src, 10, dst, 40, 10, 4));   // 원본 3행분뿐
        Assert.Throws<ArgumentException>(() => CvPixConv.ToBgra(1, src, 10, new byte[40 * 2], 40, 10, 3));   // 대상 2행분뿐
        Assert.Throws<ArgumentOutOfRangeException>(() => CvPixConv.ToBgra(3, src, 9, dst, 40, 10, 1));   // 원본 보폭 < 폭×3
        Assert.Throws<ArgumentOutOfRangeException>(() => CvPixConv.ToBgra(1, src, 10, dst, 39, 10, 1));   // 대상 보폭 < 폭×4
        Assert.Throws<ArgumentOutOfRangeException>(() => CvPixConv.ToBgra(2, src, 10, dst, 40, 5, 1));   // 2채널은 표시 형식이 아니다
        // 대조군 — 길이가 꼭 맞으면(마지막 행 패딩 없음) 던지지 않는다. 이것까지 던지면 위 단언들은 검사기가 무엇이든 던지는 것일 뿐이다.
        CvPixConv.ToBgra(1, src, 10, dst, 40, 10, 3);
        CvPixConv.ToBgra(1, src, 10, dst, 40, 0, 0);
    }
}
