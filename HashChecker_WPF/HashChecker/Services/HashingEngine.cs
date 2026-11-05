using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HashChecker.Models;

namespace HashChecker.Services
{
    /// <summary>单个文件的计算结果，成功或失败。</summary>
    public class HashOutcome
    {
        /// <summary>结果要写回的行。</summary>
        public CheckItem Item { get; set; }

        /// <summary>成功时为小写十六进制摘要值。</summary>
        public string Hash { get; set; }

        /// <summary>算出这个值用的算法。</summary>
        public HashAlgorithmInfo Algorithm { get; set; }

        /// <summary>本次算出的全部算法值（算法 id → 小写十六进制），用于多候选比对。</summary>
        public SortedDictionary<string, string> Digests { get; set; }

        /// <summary>失败原因；成功时为 null。</summary>
        public string Error { get; set; }

        public bool Success { get { return Error == null && !string.IsNullOrEmpty(Hash); } }
    }

    /// <summary>
    /// 多算法计算引擎。设计取舍：
    /// 大文件顺序读、1MB 缓冲区，边读边喂增量内核，内存占用恒定；
    /// 多个文件并行计算，线程数由选项决定（机械盘上过大反而因随机寻道变慢）；
    /// 进度按「已处理字节 / 总字节」算，一次任务内单调递增；
    /// 单个文件失败（被占用、无权限）只影响该行，不打断整体任务；
    /// 一行可能要算多个算法时只读一遍文件、每个算法各喂一份，不重复读盘。
    /// </summary>
    public sealed class HashingEngine
    {
        /// <summary>1MB 会落到大对象堆上，但顺序扫描大文件时比小缓冲区明显更快，取舍是故意的。</summary>
        private const int BufferSize = 1024 * 1024;

        /// <summary>FileStream 内部再向后多预读一块，顺序扫描时能减少系统调用次数。</summary>
        private const int ReadChunkSize = 1024 * 1024;

        /// <summary>进度回调节流：界面每秒刷新十来次足够，太频繁只会放大跨线程调度开销。</summary>
        private const int ProgressIntervalMs = 60;

        [ThreadStatic]
        /// <summary>
        /// 线程级的读缓冲复用槽。每个工作线程各持一块，避免「每个文件 new 一个 1MB 数组」；
        /// 用 [ThreadStatic] 而不用全局池，是为了不引入任何跨线程同步。
        /// </summary>
        private static byte[] _bufferSlot;

        private long _totalBytes;
        private long _processedBytes;
        private readonly object _progressLock = new object();

        /// <summary>进度上报的时间戳基准（Environment.TickCount），比 DateTime.UtcNow 便宜得多。</summary>
        private int _lastProgressTick = int.MinValue;

        private Action<int> _onProgress;

        /// <summary>本轮任务的总字节数。</summary>
        public long TotalBytes { get { return Interlocked.Read(ref _totalBytes); } }

        /// <summary>本轮任务已处理字节数。</summary>
        public long ProcessedBytes { get { return Interlocked.Read(ref _processedBytes); } }

        /// <summary>把「已处理 / 总数」换算成 0-100 的百分比。</summary>
        public static int ToPercent(long done, long total)
        {
            if (total <= 0) return 100;
            long percent = done * 100L / total;
            if (percent < 0) return 0;
            if (percent > 100) return 100;
            return (int)percent;
        }

        /// <summary>
        /// 计算一批文件。本方法阻塞，调用方应放到后台线程执行；
        /// 文件夹会被跳过但要计入进度，失败的文件同样计入，否则进度条永远到不了 100%。
        /// </summary>
        public void Run(
            IList<CheckItem> items,
            int maxThreads,
            Action<HashOutcome> onOutcome,
            Action<int> onProgress,
            CancellationToken token)
        {
            if (items == null || items.Count == 0) return;

            Interlocked.Exchange(ref _processedBytes, 0);
            Interlocked.Exchange(ref _totalBytes, 0);
            _onProgress = onProgress;
            _lastProgressTick = int.MinValue;

            long total = 0;
            foreach (CheckItem item in items)
            {
                if (item == null) continue;
                total += item.Size < 0 ? 0 : item.Size;
            }
            Interlocked.Exchange(ref _totalBytes, total);

            if (onProgress != null) onProgress(0);

            int threads = maxThreads < 1 ? 1 : maxThreads;
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = threads,
                CancellationToken = token
            };

            try
            {
                Parallel.ForEach(items, options, item =>
                {
                    if (item == null) return;

                    if (item.IsDirectory)
                    {
                        Interlocked.Add(ref _processedBytes, item.Size > 0 ? item.Size : 0);
                        return;
                    }

                    var outcome = new HashOutcome { Item = item };
                    try
                    {
                        IList<HashAlgorithmInfo> wanted = item.RequiredAlgorithms;
                        SortedDictionary<string, string> digests = HashFile(item.FullPath, wanted, token);

                        outcome.Algorithm = item.PrimaryAlgorithm;
                        outcome.Hash = digests.ContainsKey(outcome.Algorithm.Id)
                            ? digests[outcome.Algorithm.Id]
                            : null;
                        outcome.Digests = digests;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        outcome.Error = ex.Message;

                        Interlocked.Add(ref _processedBytes, item.Size > 0 ? item.Size : 0);
                    }

                    if (onOutcome != null) onOutcome(outcome);
                });
            }
            catch (AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.Flatten().InnerExceptions)
                {
                    if (inner is OperationCanceledException)
                    {
                        throw inner;
                    }
                }
                throw;
            }

            if (onProgress != null) onProgress(ToPercent(ProcessedBytes, TotalBytes));
        }

        /// <summary>
        /// 计算单个文件。读一遍文件、每个算法各喂一份，逐块上报进度；
        /// 一个算法都没有时退回默认算法，保证「只计算不比对」也有值。
        /// </summary>
        private SortedDictionary<string, string> HashFile(
            string path, IList<HashAlgorithmInfo> algorithms, CancellationToken token)
        {
            var wanted = new List<HashAlgorithmInfo>();
            if (algorithms != null)
            {
                foreach (HashAlgorithmInfo info in algorithms)
                {
                    if (info == null) continue;
                    wanted.Add(info);
                }
            }
            if (wanted.Count == 0) wanted.Add(HashAlgorithms.Default);

            var cores = new List<IHashCore>(wanted.Count);
            var ids = new List<string>(wanted.Count);
            foreach (HashAlgorithmInfo info in wanted)
            {
                IHashCore core = info.CreateCore();
                core.Initialize();
                cores.Add(core);
                ids.Add(info.Id);
            }

            byte[] buffer = RentBuffer();

            try
            {
                using (var stream = new FileStream(
                           path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                           ReadChunkSize, FileOptions.SequentialScan))
                {
                    int bytesRead;
                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();

                        for (int i = 0; i < cores.Count; i++)
                        {
                            cores[i].Append(buffer, 0, bytesRead);
                        }

                        Interlocked.Add(ref _processedBytes, bytesRead);
                        ReportProgressThrottled();
                    }
                }
            }
            finally
            {
                ReturnBuffer(buffer);
            }

            var results = new SortedDictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < cores.Count; i++)
            {
                results[ids[i]] = HashValues.ToHex(cores[i].Finish());
            }
            return results;
        }

        /// <summary>取一块读缓冲：优先复用本线程上一次用过的那块。</summary>
        private static byte[] RentBuffer()
        {
            byte[] buffer = _bufferSlot;
            if (buffer != null)
            {
                _bufferSlot = null;
                return buffer;
            }
            return new byte[BufferSize];
        }

        /// <summary>归还读缓冲供本线程下次复用。</summary>
        private static void ReturnBuffer(byte[] buffer)
        {
            if (buffer != null && buffer.Length == BufferSize) _bufferSlot = buffer;
        }

        /// <summary>
        /// 节流后的进度回调。每次读块都会调到这里，所以必须便宜：
        /// 用 Environment.TickCount 代替 DateTime.UtcNow（少一次系统调用与结构体构造），
        /// 锁里只做一次整数比较，绝不调用户回调。
        /// </summary>
        private void ReportProgressThrottled()
        {
            Action<int> handler = _onProgress;
            if (handler == null) return;

            int now = Environment.TickCount;
            bool due;

            lock (_progressLock)
            {
                due = now - _lastProgressTick >= ProgressIntervalMs;
                if (due) _lastProgressTick = now;
            }

            if (due) handler(ToPercent(ProcessedBytes, TotalBytes));
        }
    }
}
