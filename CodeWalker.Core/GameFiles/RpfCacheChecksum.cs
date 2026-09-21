using System;
using System.IO;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA V Enhanced HSHR checksum.
    /// </summary>
    public static class RpfCacheChecksum
    {
        private const ulong K0 = 0xC3A5C85C97CB3127UL;
        private const ulong K1 = 0xB492B66FBE98F273UL;
        private const ulong K2 = 0x9AE16A3B2F90404FUL;
        private const ulong KM = 0x9DDFEA08EB382D69UL;

        public static ulong Compute(byte[] cache)
        {
            ValidateHeader(cache);
            ulong seed = MakeSeed(Read32(cache, 16));
            byte[] work = (byte[])cache.Clone();
            Write64(work, 8, seed);
            return CityHash64WithSeed(work, seed);
        }

        public static ulong UpdateInPlace(byte[] cache)
        {
            ulong value = Compute(cache);
            Write64(cache, 8, value);
            return value;
        }

        public static bool Verify(byte[] cache)
        {
            ValidateHeader(cache);
            return Read64(cache, 8) == Compute(cache);
        }

        public static ulong MakeSeed(uint rootCount)
        {
            unchecked
            {
                ulong seed = 0;
                for (uint i = 1; i <= 7; i++)
                    seed = (seed << 8) | (ulong)(rootCount - i);
                return seed << 8;
            }
        }

        private static void ValidateHeader(byte[] cache)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (cache.Length < 20 || Read32(cache, 0) != 0x52485348U)
                throw new InvalidDataException("Expected an HSHR cache header.");
            if (20L + 8L * Read32(cache, 16) > cache.LongLength)
                throw new InvalidDataException("The HSHR root index is truncated.");
        }

        public static ulong CityHash64WithSeed(byte[] data, ulong seed)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            unchecked { return Hash16(CityHash64(data) - K2, seed); }
        }

        private struct Pair
        {
            public ulong First;
            public ulong Second;
            public Pair(ulong first, ulong second) { First = first; Second = second; }
        }

        private static ulong Rotate(ulong x, int shift)
        {
            return shift == 0 ? x : (x >> shift) | (x << (64 - shift));
        }

        private static ulong ShiftMix(ulong x) { return x ^ (x >> 47); }

        private static ulong Hash16(ulong u, ulong v, ulong mul = KM)
        {
            unchecked
            {
                ulong a = (u ^ v) * mul;
                a ^= a >> 47;
                ulong b = (v ^ a) * mul;
                b ^= b >> 47;
                return b * mul;
            }
        }

        private static Pair Weak32(byte[] data, int p, ulong a, ulong b)
        {
            unchecked
            {
                ulong w = Read64(data, p), x = Read64(data, p + 8);
                ulong y = Read64(data, p + 16), z = Read64(data, p + 24);
                a += w;
                b = Rotate(b + a + z, 21);
                ulong c = a;
                a += x + y;
                b += Rotate(a, 44);
                return new Pair(a + z, b + c);
            }
        }

        private static ulong CityHash64(byte[] data)
        {
            unchecked
            {
                int n = data.Length;
                if (n <= 16)
                {
                    if (n >= 8)
                    {
                        ulong mul = K2 + 2UL * (ulong)n;
                        ulong a = Read64(data, 0) + K2;
                        ulong b = Read64(data, n - 8);
                        ulong c = Rotate(b, 37) * mul + a;
                        ulong d = (Rotate(a, 25) + b) * mul;
                        return Hash16(c, d, mul);
                    }
                    if (n >= 4)
                        return Hash16((ulong)n + ((ulong)Read32(data, 0) << 3),
                            Read32(data, n - 4), K2 + 2UL * (ulong)n);
                    if (n > 0)
                    {
                        uint y = (uint)data[0] + ((uint)data[n >> 1] << 8);
                        uint z = (uint)n + ((uint)data[n - 1] << 2);
                        return ShiftMix((ulong)y * K2 ^ (ulong)z * K0) * K2;
                    }
                    return K2;
                }
                if (n <= 32)
                {
                    ulong mul = K2 + 2UL * (ulong)n;
                    ulong a = Read64(data, 0) * K1;
                    ulong b = Read64(data, 8);
                    ulong c = Read64(data, n - 8) * mul;
                    ulong d = Read64(data, n - 16) * K2;
                    return Hash16(Rotate(a + b, 43) + Rotate(c, 30) + d,
                        a + Rotate(b + K2, 18) + c, mul);
                }
                if (n <= 64)
                {
                    ulong mul = K2 + 2UL * (ulong)n;
                    ulong a = Read64(data, 0) * K2;
                    ulong b = Read64(data, 8);
                    ulong c = Read64(data, n - 24);
                    ulong d = Read64(data, n - 32);
                    ulong e = Read64(data, 16) * K2;
                    ulong f = Read64(data, 24) * 9;
                    ulong g = Read64(data, n - 8);
                    ulong h = Read64(data, n - 16) * mul;
                    ulong u = Rotate(a + g, 43) + (Rotate(b, 30) + c) * 9;
                    ulong v = ((a + g) ^ d) + f + 1;
                    ulong w = Swap((u + v) * mul) + h;
                    ulong x = Rotate(e + f, 42) + c;
                    ulong y = (Swap((v + w) * mul) + g) * mul;
                    ulong z = e + f + c;
                    a = Swap((x + z) * mul + y) + b;
                    b = ShiftMix((z + a) * mul + d + h) * mul;
                    return b + x;
                }

                ulong xx = Read64(data, n - 40);
                ulong yy = Read64(data, n - 16) + Read64(data, n - 56);
                ulong zz = Hash16(Read64(data, n - 48) + (ulong)n, Read64(data, n - 24));
                Pair vv = Weak32(data, n - 64, (ulong)n, zz);
                Pair ww = Weak32(data, n - 32, yy + K1, xx);
                xx = xx * K1 + Read64(data, 0);
                int remaining = (n - 1) & ~63;
                int p = 0;
                do
                {
                    xx = Rotate(xx + yy + vv.First + Read64(data, p + 8), 37) * K1;
                    yy = Rotate(yy + vv.Second + Read64(data, p + 48), 42) * K1;
                    xx ^= ww.Second;
                    yy += vv.First + Read64(data, p + 40);
                    zz = Rotate(zz + ww.First, 33) * K1;
                    vv = Weak32(data, p, vv.Second * K1, xx + ww.First);
                    ww = Weak32(data, p + 32, zz + ww.Second, yy + Read64(data, p + 16));
                    ulong temp = xx; xx = zz; zz = temp;
                    p += 64;
                    remaining -= 64;
                } while (remaining != 0);
                return Hash16(Hash16(vv.First, ww.First) + ShiftMix(yy) * K1 + zz,
                    Hash16(vv.Second, ww.Second) + xx);
            }
        }

        private static uint Read32(byte[] b, int p)
        {
            return (uint)b[p] | ((uint)b[p + 1] << 8) |
                ((uint)b[p + 2] << 16) | ((uint)b[p + 3] << 24);
        }

        private static ulong Read64(byte[] b, int p)
        {
            return (ulong)Read32(b, p) | ((ulong)Read32(b, p + 4) << 32);
        }

        private static void Write64(byte[] b, int p, ulong value)
        {
            unchecked
            {
                for (int i = 0; i < 8; i++) b[p + i] = (byte)(value >> (8 * i));
            }
        }

        private static ulong Swap(ulong v)
        {
            v = ((v & 0x00FF00FF00FF00FFUL) << 8) | ((v >> 8) & 0x00FF00FF00FF00FFUL);
            v = ((v & 0x0000FFFF0000FFFFUL) << 16) | ((v >> 16) & 0x0000FFFF0000FFFFUL);
            return (v << 32) | (v >> 32);
        }
    }
}
