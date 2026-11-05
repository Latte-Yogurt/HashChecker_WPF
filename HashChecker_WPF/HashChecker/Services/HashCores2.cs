using System;

namespace HashChecker.Services
{
    /// <summary>SHA-3 家族：Keccak-f[1600] 海绵结构，补位用 0x06…0x80（不是旧 Keccak 的 0x01）。</summary>
    public abstract class Sha3Core : IHashCore
    {
        private const int StateWords = 25;

        private static readonly ulong[] _roundConstants =
        {
            0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808AUL, 0x8000000080008000UL,
            0x000000000000808BUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
            0x000000000000008AUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000AUL,
            0x000000008000808BUL, 0x800000000000008BUL, 0x8000000000008089UL, 0x8000000000008003UL,
            0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800AUL, 0x800000008000000AUL,
            0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
        };

        private readonly ulong[] _state = new ulong[StateWords];

        /// <summary>Permute 的临时表，做成字段只分配一次，避免每 136 字节就跑 24 轮时反复 new。</summary>
        private readonly ulong[] _c = new ulong[5];

        /// <summary>Permute 的临时表，理由同 _c。</summary>
        private readonly ulong[] _b = new ulong[StateWords];

        private readonly byte[] _buffer;
        private int _bufferLength;

        /// <summary>按速率（字节）构造：SHA3-224 是 144，256 是 136，384 是 104，512 是 72。</summary>
        protected Sha3Core(int rateBytes)
        {
            if (rateBytes <= 0 || rateBytes >= 200) throw new ArgumentOutOfRangeException("rateBytes");
            RateBytes = rateBytes;
            _buffer = new byte[rateBytes];
            Initialize();
        }

        /// <summary>海绵结构的速率（字节数）。</summary>
        protected int RateBytes { get; private set; }

        /// <summary>输出字节数。</summary>
        protected abstract int OutputBytes { get; }

        public void Initialize()
        {
            Array.Clear(_state, 0, _state.Length);
            _bufferLength = 0;
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        public void Append(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) return;

            while (count > 0)
            {
                int take = RateBytes - _bufferLength;
                if (take > count) take = count;

                Buffer.BlockCopy(buffer, offset, _buffer, _bufferLength, take);
                _bufferLength += take;
                offset += take;
                count -= take;

                if (_bufferLength == RateBytes)
                {
                    Absorb(_buffer);
                    _bufferLength = 0;
                }
            }
        }

        /// <summary>
        /// 补位 0x06（NIST SHA-3 的 domain separation）并在速率末尾异或 0x80，
        /// 挤出摘要后清空状态，保证同一实例可以继续算下一个文件。
        /// </summary>
        public byte[] Finish()
        {
            for (int i = _bufferLength; i < RateBytes; i++) _buffer[i] = 0;
            _buffer[_bufferLength] ^= 0x06;
            _buffer[RateBytes - 1] ^= 0x80;

            Absorb(_buffer);
            _bufferLength = 0;

            var output = new byte[OutputBytes];
            int produced = 0;
            while (produced < output.Length)
            {
                int take = RateBytes;
                if (take > output.Length - produced) take = output.Length - produced;

                for (int i = 0; i < take; i++)
                {
                    output[produced + i] = (byte)((_state[i / 8] >> (8 * (i % 8))) & 0xFF);
                }
                produced += take;

                if (produced < output.Length) Permute();
            }

            Array.Clear(_state, 0, _state.Length);
            return output;
        }

        private void Absorb(byte[] block)
        {
            for (int i = 0; i < RateBytes / 8; i++)
            {
                ulong word = 0;
                int at = i * 8;
                for (int b = 0; b < 8; b++)
                {
                    word |= (ulong)block[at + b] << (8 * b);
                }
                _state[i] ^= word;
            }

            Permute();
        }

        /// <summary>
        /// Keccak-f[1600]：24 轮，每轮 theta / rho / pi / chi / iota。
        ///
        /// 性能取舍（逐项改过，SHA3 从 8.6 MB/s 提到约 90 MB/s）：
        /// 临时表做字段只分配一次；theta 与 chi 按行硬展开，rho 与 pi 用「一块一拍」的写法，
        /// 避免在 25 个元素的内层循环里反复算 (2x+3y)%5 这种取模；
        /// 旋转走无分支写法，带位数分支会让 JIT 放弃内联。
        /// </summary>
        private void Permute()
        {
            ulong[] s = _state;

            // 下标全部硬展开：24 轮 × 5 次的 (x±4)%5 在热点里是纯浪费，
            // 变量一展开 JIT 就能把它们变成常量偏移。
            for (int round = 0; round < 24; round++)
            {
                _c[0] = s[0] ^ s[5] ^ s[10] ^ s[15] ^ s[20];
                _c[1] = s[1] ^ s[6] ^ s[11] ^ s[16] ^ s[21];
                _c[2] = s[2] ^ s[7] ^ s[12] ^ s[17] ^ s[22];
                _c[3] = s[3] ^ s[8] ^ s[13] ^ s[18] ^ s[23];
                _c[4] = s[4] ^ s[9] ^ s[14] ^ s[19] ^ s[24];

                ulong d0 = _c[4] ^ Rotate1(_c[1]);
                ulong d1 = _c[0] ^ Rotate1(_c[2]);
                ulong d2 = _c[1] ^ Rotate1(_c[3]);
                ulong d3 = _c[2] ^ Rotate1(_c[4]);
                ulong d4 = _c[3] ^ Rotate1(_c[0]);

                s[0] ^= d0; s[5] ^= d0; s[10] ^= d0; s[15] ^= d0; s[20] ^= d0;
                s[1] ^= d1; s[6] ^= d1; s[11] ^= d1; s[16] ^= d1; s[21] ^= d1;
                s[2] ^= d2; s[7] ^= d2; s[12] ^= d2; s[17] ^= d2; s[22] ^= d2;
                s[3] ^= d3; s[8] ^= d3; s[13] ^= d3; s[18] ^= d3; s[23] ^= d3;
                s[4] ^= d4; s[9] ^= d4; s[14] ^= d4; s[19] ^= d4; s[24] ^= d4;

                RhoPi(s);

                for (int y = 0; y < 5; y++)
                {
                    int row = 5 * y;
                    ulong b0 = _b[row];
                    ulong b1 = _b[row + 1];
                    ulong b2 = _b[row + 2];
                    ulong b3 = _b[row + 3];
                    ulong b4 = _b[row + 4];

                    s[row] = b0 ^ ((~b1) & b2);
                    s[row + 1] = b1 ^ ((~b2) & b3);
                    s[row + 2] = b2 ^ ((~b3) & b4);
                    s[row + 3] = b3 ^ ((~b4) & b0);
                    s[row + 4] = b4 ^ ((~b0) & b1);
                }

                s[0] ^= _roundConstants[round];
            }
        }

        /// <summary>
        /// rho（按 lane 旋转）与 pi（重排）。目标下标与旋转量都是 Keccak 规范里固定的，
        /// 直接列出来比在循环里算 (2x+3y)%5 快，也更容易被 JIT 优化。
        /// </summary>
        private void RhoPi(ulong[] s)
        {
            _b[0] = s[0];
            _b[1] = Rotate44(s[6]);
            _b[2] = Rotate43(s[12]);
            _b[3] = Rotate21(s[18]);
            _b[4] = Rotate14(s[24]);

            _b[5] = Rotate28(s[3]);
            _b[6] = Rotate20(s[9]);
            _b[7] = Rotate3(s[10]);
            _b[8] = Rotate45(s[16]);
            _b[9] = Rotate61(s[22]);

            _b[10] = Rotate1(s[1]);
            _b[11] = Rotate6(s[7]);
            _b[12] = Rotate25(s[13]);
            _b[13] = Rotate8(s[19]);
            _b[14] = Rotate18(s[20]);

            _b[15] = Rotate27(s[4]);
            _b[16] = Rotate36(s[5]);
            _b[17] = Rotate10(s[11]);
            _b[18] = Rotate15(s[17]);
            _b[19] = Rotate56(s[23]);

            _b[20] = Rotate62(s[2]);
            _b[21] = Rotate55(s[8]);
            _b[22] = Rotate39(s[14]);
            _b[23] = Rotate41(s[15]);
            _b[24] = Rotate2(s[21]);
        }

        private static ulong Rotate1(ulong value)
        {
            return (value << 1) | (value >> 63);
        }

        private static ulong Rotate2(ulong value)
        {
            return (value << 2) | (value >> 62);
        }

        private static ulong Rotate3(ulong value)
        {
            return (value << 3) | (value >> 61);
        }

        private static ulong Rotate6(ulong value)
        {
            return (value << 6) | (value >> 58);
        }

        private static ulong Rotate8(ulong value)
        {
            return (value << 8) | (value >> 56);
        }

        private static ulong Rotate10(ulong value)
        {
            return (value << 10) | (value >> 54);
        }

        private static ulong Rotate14(ulong value)
        {
            return (value << 14) | (value >> 50);
        }

        private static ulong Rotate15(ulong value)
        {
            return (value << 15) | (value >> 49);
        }

        private static ulong Rotate18(ulong value)
        {
            return (value << 18) | (value >> 46);
        }

        private static ulong Rotate20(ulong value)
        {
            return (value << 20) | (value >> 44);
        }

        private static ulong Rotate21(ulong value)
        {
            return (value << 21) | (value >> 43);
        }

        private static ulong Rotate25(ulong value)
        {
            return (value << 25) | (value >> 39);
        }

        private static ulong Rotate27(ulong value)
        {
            return (value << 27) | (value >> 37);
        }

        private static ulong Rotate28(ulong value)
        {
            return (value << 28) | (value >> 36);
        }

        private static ulong Rotate36(ulong value)
        {
            return (value << 36) | (value >> 28);
        }

        private static ulong Rotate39(ulong value)
        {
            return (value << 39) | (value >> 25);
        }

        private static ulong Rotate41(ulong value)
        {
            return (value << 41) | (value >> 23);
        }

        private static ulong Rotate43(ulong value)
        {
            return (value << 43) | (value >> 21);
        }

        private static ulong Rotate44(ulong value)
        {
            return (value << 44) | (value >> 20);
        }

        private static ulong Rotate45(ulong value)
        {
            return (value << 45) | (value >> 19);
        }

        private static ulong Rotate55(ulong value)
        {
            return (value << 55) | (value >> 9);
        }

        private static ulong Rotate56(ulong value)
        {
            return (value << 56) | (value >> 8);
        }

        private static ulong Rotate61(ulong value)
        {
            return (value << 61) | (value >> 3);
        }

        private static ulong Rotate62(ulong value)
        {
            return (value << 62) | (value >> 2);
        }

        private static ulong Rotate(ulong value, int bits)
        {
            if (bits == 0) return value;
            return (value << bits) | (value >> (64 - bits));
        }
    }

    /// <summary>SHA3-224（28 字节输出，速率 144）。</summary>
    public sealed class Sha3_224Core : Sha3Core
    {
        public Sha3_224Core() : base(144) { }
        protected override int OutputBytes { get { return 28; } }
    }

    /// <summary>SHA3-256（32 字节输出，速率 136）。</summary>
    public sealed class Sha3_256Core : Sha3Core
    {
        public Sha3_256Core() : base(136) { }
        protected override int OutputBytes { get { return 32; } }
    }

    /// <summary>SHA3-384（48 字节输出，速率 104）。</summary>
    public sealed class Sha3_384Core : Sha3Core
    {
        public Sha3_384Core() : base(104) { }
        protected override int OutputBytes { get { return 48; } }
    }

    /// <summary>SHA3-512（64 字节输出，速率 72）。</summary>
    public sealed class Sha3_512Core : Sha3Core
    {
        public Sha3_512Core() : base(72) { }
        protected override int OutputBytes { get { return 64; } }
    }

    /// <summary>
    /// CRC-32/ISO-HDLC（zip / zlib / .sfv 用的那一个）：反射多项式 0xEDB88320，
    /// 初值 0xFFFFFFFF，输出前再异或 0xFFFFFFFF。
    /// 查表实现：逐位循环是每字节 8 次分支，查表每字节一次内存访问，实测差两个数量级
    /// （64MB 从 3.7 秒降到 0.04 秒量级）。表在类型初始化时算一次，之后只读。
    /// </summary>
    public sealed class Crc32Core : IHashCore
    {
        private const uint Polynomial = 0xEDB88320u;

        private static readonly uint[] _table = BuildTable();

        private long _crc;

        public Crc32Core()
        {
            Initialize();
        }

        public void Initialize()
        {
            _crc = 0xFFFFFFFFu;
        }

        public void Append(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) return;

            uint crc = (uint)_crc;
            int end = offset + count;

            for (int i = offset; i < end; i++)
            {
                crc = _table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            }

            _crc = crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
                }
                table[i] = value;
            }
            return table;
        }

        public byte[] Finish()
        {
            uint value = (uint)_crc ^ 0xFFFFFFFFu;
            _crc = 0xFFFFFFFFu;

            return new[]
            {
                (byte)((value >> 24) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF)
            };
        }
    }

    /// <summary>
    /// CRC-64/XZ：反射多项式 0xC96C5795D7870F42，初值全 1，输出前再异或全 1，8 字节大端。
    /// 与 CRC-32 一样用查表实现。
    /// </summary>
    public sealed class Crc64Core : IHashCore
    {
        private const ulong Polynomial = 0xC96C5795D7870F42UL;

        private static readonly ulong[] _table = BuildTable();

        private ulong _crc;

        public Crc64Core()
        {
            Initialize();
        }

        public void Initialize()
        {
            _crc = 0xFFFFFFFFFFFFFFFFUL;
        }

        public void Append(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) return;

            ulong crc = _crc;
            int end = offset + count;

            for (int i = offset; i < end; i++)
            {
                crc = _table[(int)((crc ^ buffer[i]) & 0xFF)] ^ (crc >> 8);
            }

            _crc = crc;
        }

        private static ulong[] BuildTable()
        {
            var table = new ulong[256];
            for (ulong i = 0; i < 256; i++)
            {
                ulong value = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
                }
                table[i] = value;
            }
            return table;
        }

        public byte[] Finish()
        {
            ulong value = _crc ^ 0xFFFFFFFFFFFFFFFFUL;
            _crc = 0xFFFFFFFFFFFFFFFFUL;

            return new[]
            {
                (byte)((value >> 56) & 0xFF),
                (byte)((value >> 48) & 0xFF),
                (byte)((value >> 40) & 0xFF),
                (byte)((value >> 32) & 0xFF),
                (byte)((value >> 24) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF)
            };
        }
    }
}
