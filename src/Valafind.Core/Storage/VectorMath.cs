using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Valafind.Core.Storage;

/// <summary>SIMD helpers for float / int8 vectors used by the in-memory index and the search service.</summary>
internal static class VectorMath
{
    /// <summary>Largest magnitude of an int8 code. -128 is never produced so products fit in a signed 16-bit lane.</summary>
    public const int Int8Max = 127;

    /// <summary>Dot product of two equally long float vectors.</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have the same length.", nameof(b));

        var n = a.Length;
        var i = 0;
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        float sum;

        if (Vector256.IsHardwareAccelerated && n >= Vector256<float>.Count)
        {
            var acc = Vector256<float>.Zero;
            for (; i <= n - Vector256<float>.Count; i += Vector256<float>.Count)
                acc += Vector256.LoadUnsafe(ref ra, (nuint)i) * Vector256.LoadUnsafe(ref rb, (nuint)i);
            sum = Vector256.Sum(acc);
        }
        else if (Vector128.IsHardwareAccelerated && n >= Vector128<float>.Count)
        {
            var acc = Vector128<float>.Zero;
            for (; i <= n - Vector128<float>.Count; i += Vector128<float>.Count)
                acc += Vector128.LoadUnsafe(ref ra, (nuint)i) * Vector128.LoadUnsafe(ref rb, (nuint)i);
            sum = Vector128.Sum(acc);
        }
        else
        {
            sum = 0;
        }

        for (; i < n; i++)
            sum += Unsafe.Add(ref ra, i) * Unsafe.Add(ref rb, i);
        return sum;
    }

    /// <summary>Exact integer dot product of two int8 vectors whose components lie in [-127, 127].</summary>
    public static int DotInt8(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have the same length.", nameof(b));

        var n = a.Length;
        var i = 0;
        var sum = 0;
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);

        if (Avx2.IsSupported && n >= 32)
        {
            // Sign-extend 16 int8 lanes to int16 (vpmovsxbw), then vpmaddwd: widening multiply + pairwise add into int32.
            var acc0 = Vector256<int>.Zero;
            var acc1 = Vector256<int>.Zero;
            for (; i <= n - 32; i += 32)
            {
                var a0 = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref ra, (nuint)i));
                var b0 = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref rb, (nuint)i));
                var a1 = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref ra, (nuint)(i + 16)));
                var b1 = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref rb, (nuint)(i + 16)));
                acc0 = Avx2.Add(acc0, Avx2.MultiplyAddAdjacent(a0, b0));
                acc1 = Avx2.Add(acc1, Avx2.MultiplyAddAdjacent(a1, b1));
            }
            sum = Vector256.Sum(acc0 + acc1);
        }
        else if (Vector128.IsHardwareAccelerated && n >= 16)
        {
            // Portable path (ARM64 NEON, SSE without AVX2): widen to int16, multiply (|p| <= 16129 fits), widen to int32.
            var acc = Vector128<int>.Zero;
            for (; i <= n - 16; i += 16)
            {
                var (aLo, aHi) = Vector128.Widen(Vector128.LoadUnsafe(ref ra, (nuint)i));
                var (bLo, bHi) = Vector128.Widen(Vector128.LoadUnsafe(ref rb, (nuint)i));
                var (p0, p1) = Vector128.Widen(aLo * bLo);
                var (p2, p3) = Vector128.Widen(aHi * bHi);
                acc += (p0 + p1) + (p2 + p3);
            }
            sum = Vector128.Sum(acc);
        }

        for (; i < n; i++)
            sum += Unsafe.Add(ref ra, i) * Unsafe.Add(ref rb, i);
        return sum;
    }

    /// <summary>L2-normalises <paramref name="v"/> in place. Returns false (leaving it unchanged) for zero / non-finite vectors.</summary>
    public static bool TryNormalize(Span<float> v)
    {
        var sq = Dot(v, v);
        if (!float.IsFinite(sq) || sq <= 0f)
            return false;
        var inv = 1f / MathF.Sqrt(sq);
        for (var i = 0; i < v.Length; i++)
            v[i] *= inv;
        return true;
    }

    /// <summary>Symmetric per-vector int8 quantisation: code = round(v / scale), scale = maxabs / 127.
    /// Returns the scale (0 for an all-zero / non-finite vector, whose codes are cleared).</summary>
    public static float Quantize(ReadOnlySpan<float> v, Span<sbyte> codes)
    {
        if (codes.Length != v.Length)
            throw new ArgumentException("Destination length must match the vector length.", nameof(codes));

        var maxAbs = 0f;
        foreach (var x in v)
        {
            if (!float.IsFinite(x))
            {
                codes.Clear();
                return 0f;
            }
            var ax = MathF.Abs(x);
            if (ax > maxAbs)
                maxAbs = ax;
        }

        if (!float.IsFinite(maxAbs) || maxAbs <= 0f)
        {
            codes.Clear();
            return 0f;
        }

        var inv = Int8Max / maxAbs;
        for (var i = 0; i < v.Length; i++)
        {
            var q = (int)MathF.Round(v[i] * inv, MidpointRounding.ToEven);
            codes[i] = (sbyte)Math.Clamp(q, -Int8Max, Int8Max);
        }
        return maxAbs / Int8Max;
    }

    /// <summary>Encodes a vector as a float32 little-endian blob.</summary>
    public static byte[] ToBlob(ReadOnlySpan<float> v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(v).CopyTo(bytes);
        }
        else
        {
            for (var i = 0; i < v.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), v[i]);
        }
        return bytes;
    }

    /// <summary>Decodes a float32 little-endian blob into <paramref name="destination"/>.
    /// Returns false when the blob does not hold exactly <c>destination.Length</c> floats.</summary>
    public static bool TryReadBlob(ReadOnlySpan<byte> blob, Span<float> destination)
    {
        if (blob.Length != destination.Length * sizeof(float))
            return false;
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, float>(blob).CopyTo(destination);
        }
        else
        {
            for (var i = 0; i < destination.Length; i++)
                destination[i] = BinaryPrimitives.ReadSingleLittleEndian(blob[(i * sizeof(float))..]);
        }
        return true;
    }
}
