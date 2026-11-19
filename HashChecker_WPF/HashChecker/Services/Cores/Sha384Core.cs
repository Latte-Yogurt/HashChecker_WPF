using System.Security.Cryptography;

namespace HashChecker.Services
{
    /// <summary>SHA-384（48 字节输出）。SHA-512 的截断变体，走框架实现。</summary>
    public sealed class Sha384Core : FrameworkCore
    {
        protected override HashAlgorithm CreateAlgorithm()
        {
            return SHA384.Create();
        }
    }
}
