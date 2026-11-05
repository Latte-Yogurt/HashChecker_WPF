using System;
using System.Security.Cryptography;

namespace HashChecker.Services
{
    /// <summary>
    /// 增量哈希内核：分块喂数据，最后取结果。实现必须可复用（Initialize 之后能重新开始）。
    /// </summary>
    public interface IHashCore
    {
        /// <summary>重置到初始状态。</summary>
        void Initialize();

        /// <summary>喂入一段数据。count 为 0 时什么都不做。</summary>
        void Append(byte[] buffer, int offset, int count);

        /// <summary>结束并返回原始摘要字节。</summary>
        byte[] Finish();
    }

    /// <summary>
    /// .NET 自带算法的适配层。MD5 / SHA-1 / SHA-256 / SHA-384 / SHA-512 直接用框架实现，
    /// 不自研：框架里已有经过验证的实现，重写只引入风险。
    /// </summary>
    public sealed class BuiltInCore : IHashCore
    {
        private readonly Func<HashAlgorithm> _factory;
        private HashAlgorithm _algorithm;

        public BuiltInCore(Func<HashAlgorithm> factory)
        {
            if (factory == null) throw new ArgumentNullException("factory");
            _factory = factory;
            Initialize();
        }

        /// <summary>
        /// 每次重置都新建算法实例：HashAlgorithm 在 TransformFinalBlock 之后状态就已结束，
        /// 复用同一对象容易在「算完一个文件再算下一个」时踩到；新建开销相比读盘可以忽略。
        /// </summary>
        public void Initialize()
        {
            if (_algorithm != null)
            {
                _algorithm.Dispose();
            }
            _algorithm = _factory();
        }

        public void Append(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) return;
            _algorithm.TransformBlock(buffer, offset, count, null, 0);
        }

        /// <summary>
        /// 补位：0x80 + 若干 0 + 128 位消息长度（这里只写低 64 位，高位按 0 处理），
        /// 输出摘要后立刻恢复初始状态，保证同一实例可以继续算下一个文件。
        /// </summary>
        public byte[] Finish()
        {
            _algorithm.TransformFinalBlock(new byte[0], 0, 0);
            byte[] result = _algorithm.Hash;
            return result == null ? new byte[0] : (byte[])result.Clone();
        }
    }

    /// <summary>SHA-2 家族的 64 位字变体共用一套压缩函数，SHA-512 与 SHA-512/t 靠不同 IV 与截断区分。</summary>
    public abstract class Sha512FamilyCore : IHashCore
    {
        private const int BlockBytes = 128;

        private static readonly ulong[] _k = BuildConstants();

        private readonly ulong[] _h = new ulong[8];
        private readonly ulong[] _initial;
        private readonly byte[] _block = new byte[BlockBytes];
        private readonly ulong[] _w = new ulong[80];
        private int _blockLength;
        private long _totalLength;

        protected Sha512FamilyCore(ulong[] initial)
        {
            if (initial == null || initial.Length != 8) throw new ArgumentException("initial");
            _initial = (ulong[])initial.Clone();
            Initialize();
        }

        /// <summary>输出多少字节的摘要：SHA-512 全 64 字节，/224 取 28，/256 取 32。</summary>
        protected abstract int OutputBytes { get; }

        public void Initialize()
        {
            Array.Copy(_initial, _h, 8);
            _blockLength = 0;
            _totalLength = 0;
            Array.Clear(_block, 0, _block.Length);
        }

        public void Append(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) return;

            _totalLength += count;

            while (count > 0)
            {
                int take = BlockBytes - _blockLength;
                if (take > count) take = count;

                Buffer.BlockCopy(buffer, offset, _block, _blockLength, take);
                _blockLength += take;
                offset += take;
                count -= take;

                if (_blockLength == BlockBytes)
                {
                    Compress(_block, 0);
                    _blockLength = 0;
                }
            }
        }

        public byte[] Finish()
        {
            long bitLength = _totalLength * 8;

            _block[_blockLength++] = 0x80;
            if (_blockLength > BlockBytes - 16)
            {
                while (_blockLength < BlockBytes) _block[_blockLength++] = 0;
                Compress(_block, 0);
                _blockLength = 0;
            }

            while (_blockLength < BlockBytes - 16) _block[_blockLength++] = 0;

            for (int i = 0; i < 8; i++)
            {
                _block[BlockBytes - 16 + i] = 0;
            }
            for (int i = 0; i < 8; i++)
            {
                _block[BlockBytes - 8 + i] = (byte)((bitLength >> (56 - 8 * i)) & 0xFF);
            }

            Compress(_block, 0);
            _blockLength = 0;

            var output = new byte[64];
            for (int i = 0; i < 8; i++)
            {
                output[i * 8] = (byte)((_h[i] >> 56) & 0xFF);
                output[i * 8 + 1] = (byte)((_h[i] >> 48) & 0xFF);
                output[i * 8 + 2] = (byte)((_h[i] >> 40) & 0xFF);
                output[i * 8 + 3] = (byte)((_h[i] >> 32) & 0xFF);
                output[i * 8 + 4] = (byte)((_h[i] >> 24) & 0xFF);
                output[i * 8 + 5] = (byte)((_h[i] >> 16) & 0xFF);
                output[i * 8 + 6] = (byte)((_h[i] >> 8) & 0xFF);
                output[i * 8 + 7] = (byte)(_h[i] & 0xFF);
            }

            int want = OutputBytes;
            if (want <= 0 || want > 64) want = 64;

            var trimmed = new byte[want];
            Array.Copy(output, trimmed, want);

            Array.Copy(_initial, _h, 8);
            return trimmed;
        }

        private void Compress(byte[] block, int offset)
        {
            for (int i = 0; i < 16; i++)
            {
                int at = offset + i * 8;
                _w[i] = ((ulong)block[at] << 56)
                    | ((ulong)block[at + 1] << 48)
                    | ((ulong)block[at + 2] << 40)
                    | ((ulong)block[at + 3] << 32)
                    | ((ulong)block[at + 4] << 24)
                    | ((ulong)block[at + 5] << 16)
                    | ((ulong)block[at + 6] << 8)
                    | (ulong)block[at + 7];
            }

            for (int i = 16; i < 80; i++)
            {
                ulong s0 = Rotate(_w[i - 15], 1) ^ Rotate(_w[i - 15], 8) ^ (_w[i - 15] >> 7);
                ulong s1 = Rotate(_w[i - 2], 19) ^ Rotate(_w[i - 2], 61) ^ (_w[i - 2] >> 6);
                _w[i] = _w[i - 16] + s0 + _w[i - 7] + s1;
            }

            ulong a = _h[0], b = _h[1], c = _h[2], d = _h[3];
            ulong e = _h[4], f = _h[5], g = _h[6], h = _h[7];

            for (int i = 0; i < 80; i++)
            {
                ulong s1 = Rotate(e, 14) ^ Rotate(e, 18) ^ Rotate(e, 41);
                ulong ch = (e & f) ^ ((~e) & g);
                ulong temp1 = h + s1 + ch + _k[i] + _w[i];
                ulong s0 = Rotate(a, 28) ^ Rotate(a, 34) ^ Rotate(a, 39);
                ulong maj = (a & b) ^ (a & c) ^ (b & c);
                ulong temp2 = s0 + maj;

                h = g;
                g = f;
                f = e;
                e = d + temp1;
                d = c;
                c = b;
                b = a;
                a = temp1 + temp2;
            }

            _h[0] += a;
            _h[1] += b;
            _h[2] += c;
            _h[3] += d;
            _h[4] += e;
            _h[5] += f;
            _h[6] += g;
            _h[7] += h;
        }

        private static ulong Rotate(ulong value, int bits)
        {
            return (value >> bits) | (value << (64 - bits));
        }

        /// <summary>前 80 个素数立方根的高 64 位，SHA-512 家族的标准常量表。</summary>
        private static ulong[] BuildConstants()
        {
            return new ulong[]
            {
                0x428a2f98d728ae22UL, 0x7137449123ef65cdUL, 0xb5c0fbcfec4d3b2fUL, 0xe9b5dba58189dbbcUL,
                0x3956c25bf348b538UL, 0x59f111f1b605d019UL, 0x923f82a4af194f9bUL, 0xab1c5ed5da6d8118UL,
                0xd807aa98a3030242UL, 0x12835b0145706fbeUL, 0x243185be4ee4b28cUL, 0x550c7dc3d5ffb4e2UL,
                0x72be5d74f27b896fUL, 0x80deb1fe3b1696b1UL, 0x9bdc06a725c71235UL, 0xc19bf174cf692694UL,
                0xe49b69c19ef14ad2UL, 0xefbe4786384f25e3UL, 0x0fc19dc68b8cd5b5UL, 0x240ca1cc77ac9c65UL,
                0x2de92c6f592b0275UL, 0x4a7484aa6ea6e483UL, 0x5cb0a9dcbd41fbd4UL, 0x76f988da831153b5UL,
                0x983e5152ee66dfabUL, 0xa831c66d2db43210UL, 0xb00327c898fb213fUL, 0xbf597fc7beef0ee4UL,
                0xc6e00bf33da88fc2UL, 0xd5a79147930aa725UL, 0x06ca6351e003826fUL, 0x142929670a0e6e70UL,
                0x27b70a8546d22ffcUL, 0x2e1b21385c26c926UL, 0x4d2c6dfc5ac42aedUL, 0x53380d139d95b3dfUL,
                0x650a73548baf63deUL, 0x766a0abb3c77b2a8UL, 0x81c2c92e47edaee6UL, 0x92722c851482353bUL,
                0xa2bfe8a14cf10364UL, 0xa81a664bbc423001UL, 0xc24b8b70d0f89791UL, 0xc76c51a30654be30UL,
                0xd192e819d6ef5218UL, 0xd69906245565a910UL, 0xf40e35855771202aUL, 0x106aa07032bbd1b8UL,
                0x19a4c116b8d2d0c8UL, 0x1e376c085141ab53UL, 0x2748774cdf8eeb99UL, 0x34b0bcb5e19b48a8UL,
                0x391c0cb3c5c95a63UL, 0x4ed8aa4ae3418acbUL, 0x5b9cca4f7763e373UL, 0x682e6ff3d6b2b8a3UL,
                0x748f82ee5defb2fcUL, 0x78a5636f43172f60UL, 0x84c87814a1f0ab72UL, 0x8cc702081a6439ecUL,
                0x90befffa23631e28UL, 0xa4506cebde82bde9UL, 0xbef9a3f7b2c67915UL, 0xc67178f2e372532bUL,
                0xca273eceea26619cUL, 0xd186b8c721c0c207UL, 0xeada7dd6cde0eb1eUL, 0xf57d4f7fee6ed178UL,
                0x06f067aa72176fbaUL, 0x0a637dc5a2c898a6UL, 0x113f9804bef90daeUL, 0x1b710b35131c471bUL,
                0x28db77f523047d84UL, 0x32caab7b40c72493UL, 0x3c9ebe0a15c9bebcUL, 0x431d67c49c100d4cUL,
                0x4cc5d4becb3e42b6UL, 0x597f299cfc657e2aUL, 0x5fcb6fab3ad6faecUL, 0x6c44198c4a475817UL
            };
        }
    }

    /// <summary>SHA-512（64 字节输出）。</summary>
    public sealed class Sha512Core : Sha512FamilyCore
    {
        public Sha512Core()
            : base(new ulong[]
            {
                0x6a09e667f3bcc908UL, 0xbb67ae8584caa73bUL, 0x3c6ef372fe94f82bUL, 0xa54ff53a5f1d36f1UL,
                0x510e527fade682d1UL, 0x9b05688c2b3e6c1fUL, 0x1f83d9abfb41bd6bUL, 0x5be0cd19137e2179UL
            })
        {
        }

        protected override int OutputBytes { get { return 64; } }
    }

    /// <summary>SHA-512/224（28 字节输出）。</summary>
    public sealed class Sha512T224Core : Sha512FamilyCore
    {
        public Sha512T224Core()
            : base(new ulong[]
            {
                0x8C3D37C819544DA2UL, 0x73E1996689DCD4D6UL, 0x1DFAB7AE32FF9C82UL, 0x679DD514582F9FCFUL,
                0x0F6D2B697BD44DA8UL, 0x77E36F7304C48942UL, 0x3F9D85A86A1D36C8UL, 0x1112E6AD91D692A1UL
            })
        {
        }

        protected override int OutputBytes { get { return 28; } }
    }

    /// <summary>SHA-512/256（32 字节输出）。</summary>
    public sealed class Sha512T256Core : Sha512FamilyCore
    {
        public Sha512T256Core()
            : base(new ulong[]
            {
                0x22312194FC2BF72CUL, 0x9F555FA3C84C64C2UL, 0x2393B86B6F53B151UL, 0x963877195940EABDUL,
                0x96283EE2A88EFFE3UL, 0xBE5E1E2553863992UL, 0x2B0199FC2C85B8AAUL, 0x0EB72DDC81C52CA2UL
            })
        {
        }

        protected override int OutputBytes { get { return 32; } }
    }

    /// <summary>
    /// SHA-2 家族的 32 位字变体。SHA-256 用框架自带的，这里只补框架缺的 SHA-224，
    /// 两者共用同一套压缩函数与常量表。
    /// </summary>
    public sealed class Sha224Core : IHashCore
    {
        private const int BlockBytes = 64;

        private static readonly uint[] _k =
        {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
        };

        private static readonly uint[] _initial =
        {
            0xc1059ed8, 0x367cd507, 0x3070dd17, 0xf70e5939,
            0xffc00b31, 0x68581511, 0x64f98fa7, 0xbefa4fa4
        };

        private readonly uint[] _h = new uint[8];
        private readonly byte[] _block = new byte[BlockBytes];
        private readonly uint[] _w = new uint[64];
        private int _blockLength;
        private long _totalLength;

        public Sha224Core()
        {
            Initialize();
        }

        public void Initialize()
        {
            Array.Copy(_initial, _h, 8);
            _blockLength = 0;
            _totalLength = 0;
            Array.Clear(_block, 0, _block.Length);
        }

        public void Append(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) return;

            _totalLength += count;

            while (count > 0)
            {
                int take = BlockBytes - _blockLength;
                if (take > count) take = count;

                Buffer.BlockCopy(buffer, offset, _block, _blockLength, take);
                _blockLength += take;
                offset += take;
                count -= take;

                if (_blockLength == BlockBytes)
                {
                    Compress(_block, 0);
                    _blockLength = 0;
                }
            }
        }

        public byte[] Finish()
        {
            long bitLength = _totalLength * 8;

            _block[_blockLength++] = 0x80;
            if (_blockLength > BlockBytes - 8)
            {
                while (_blockLength < BlockBytes) _block[_blockLength++] = 0;
                Compress(_block, 0);
                _blockLength = 0;
            }

            while (_blockLength < BlockBytes - 8) _block[_blockLength++] = 0;

            for (int i = 0; i < 8; i++)
            {
                _block[BlockBytes - 8 + i] = (byte)((bitLength >> (56 - 8 * i)) & 0xFF);
            }

            Compress(_block, 0);
            _blockLength = 0;

            // SHA-224 只输出 7 个字（28 字节），第 8 个字 _h[7] 丢弃。
            var output = new byte[28];
            for (int i = 0; i < 7; i++)
            {
                output[i * 4] = (byte)((_h[i] >> 24) & 0xFF);
                output[i * 4 + 1] = (byte)((_h[i] >> 16) & 0xFF);
                output[i * 4 + 2] = (byte)((_h[i] >> 8) & 0xFF);
                output[i * 4 + 3] = (byte)(_h[i] & 0xFF);
            }

            Array.Copy(_initial, _h, 8);
            return output;
        }

        private void Compress(byte[] block, int offset)
        {
            for (int i = 0; i < 16; i++)
            {
                int at = offset + i * 4;
                _w[i] = ((uint)block[at] << 24)
                    | ((uint)block[at + 1] << 16)
                    | ((uint)block[at + 2] << 8)
                    | (uint)block[at + 3];
            }

            for (int i = 16; i < 64; i++)
            {
                uint s0 = Rotate(_w[i - 15], 7) ^ Rotate(_w[i - 15], 18) ^ (_w[i - 15] >> 3);
                uint s1 = Rotate(_w[i - 2], 17) ^ Rotate(_w[i - 2], 19) ^ (_w[i - 2] >> 10);
                _w[i] = _w[i - 16] + s0 + _w[i - 7] + s1;
            }

            uint a = _h[0], b = _h[1], c = _h[2], d = _h[3];
            uint e = _h[4], f = _h[5], g = _h[6], h = _h[7];

            for (int i = 0; i < 64; i++)
            {
                uint s1 = Rotate(e, 6) ^ Rotate(e, 11) ^ Rotate(e, 25);
                uint ch = (e & f) ^ ((~e) & g);
                uint temp1 = h + s1 + ch + _k[i] + _w[i];
                uint s0 = Rotate(a, 2) ^ Rotate(a, 13) ^ Rotate(a, 22);
                uint maj = (a & b) ^ (a & c) ^ (b & c);
                uint temp2 = s0 + maj;

                h = g;
                g = f;
                f = e;
                e = d + temp1;
                d = c;
                c = b;
                b = a;
                a = temp1 + temp2;
            }

            _h[0] += a;
            _h[1] += b;
            _h[2] += c;
            _h[3] += d;
            _h[4] += e;
            _h[5] += f;
            _h[6] += g;
            _h[7] += h;
        }

        private static uint Rotate(uint value, int bits)
        {
            return (value >> bits) | (value << (32 - bits));
        }
    }
}
