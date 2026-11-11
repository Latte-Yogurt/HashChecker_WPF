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
    ///
    /// **保序并行**：多线程算、但结果严格按调用方给的列表顺序落盘。
    /// 不用 Parallel.ForEach —— 它按「谁先算完谁先回调」交付，大文件越多乱序越明显，
    /// 界面看起来就是结果一行一行乱跳。这里改成「取号 + 按号交付」：
    /// 工作线程从队列里领第 N 个任务去算，算完放进按 N 索引的槽位，
    /// 由交付线程从第 0 号开始按顺序 flush 出去，保证「第 k 个结果一定在第 k+1 个之前交付」。
    ///
    /// **大文件吞吐**：1MB 缓冲顺序读、边读边喂增量内核，内存占用恒定；
    /// 一行要算多个算法时只读一遍文件、每个算法各喂一份，不重复读盘。
    ///
    /// **进度真实可用**：已处理字节由各工作线程累加，节流窗口内只做一次整数比较，
    /// 时间基准用 Stopwatch 而不是 TickCount —— 后者在 int 上做差会溢出（曾导致进度一次都不刷新）。
    ///
    /// **单个文件失败**只影响该行，不打断整体任务。
    /// </summary>
    public sealed class HashingEngine
    {
        /// <summary>1MB 会落到大对象堆上，但顺序扫描大文件时比小缓冲区明显更快，取舍是故意的。</summary>
        private const int BufferSize = 1024 * 1024;

        /// <summary>FileStream 内部再向后多预读一块，顺序扫描时能减少系统调用次数。</summary>
        private const int ReadChunkSize = 1024 * 1024;

        /// <summary>进度回调节流：界面每秒刷新十来次足够，太频繁只会放大跨线程调度开销。</summary>
        private const double ProgressIntervalMs = 60.0;

        [ThreadStatic]
        /// <summary>
        /// 线程级的读缓冲复用槽。每个工作线程各持一块，避免「每个文件 new 一个 1MB 数组」；
        /// 用 [ThreadStatic] 而不用全局池，是为了不引入任何跨线程同步。
        /// </summary>
        private static byte[] _bufferSlot;

        private long _totalBytes;
        private long _processedBytes;

        /// <summary>
        /// 进度上报的时间基准。用 Stopwatch 的单调滴答数而不是 Environment.TickCount：
        /// TickCount 是 int，「当前 − 基准」在基准取极值时会整数溢出成负数，
        /// 于是 `差值 >= 间隔` 永远为假、进度一次都不上报（这是曾经的缺陷）。
        /// </summary>
        private long _lastProgressTicks;

        private Action<int> _onProgress;

        /// <summary>本轮任务的总字节数。</summary>
        public long TotalBytes { get { return Interlocked.Read(ref _totalBytes); } }

        /// <summary>本轮任务已处理字节数。</summary>
        public long ProcessedBytes { get { return Interlocked.Read(ref _processedBytes); } }

        /// <summary>把「已处理 / 总数」换算成 0-100 的百分比。</summary>
        public static int ToPercent(long done, long total)
        {
            if (total <= 0) return 100;
            if (done <= 0) return 0;
            if (done >= total) return 100;

            // 先除后乘会让小进度全部归零；用 double 一次算完，再夹到 0-100。
            double percent = done * 100.0 / total;
            if (percent < 0) return 0;
            if (percent > 100) return 100;
            return (int)percent;
        }

        /// <summary>
        /// 计算一批文件。本方法阻塞，调用方应放到后台线程执行；
        /// 文件夹会被跳过但要计入进度，失败的文件同样计入，否则进度条永远到不了 100%。
        ///
        /// 结果严格按 <paramref name="items"/> 的顺序调用 <paramref name="onOutcome"/>：
        /// 第 k 个交付的 outcome 一定对应 items[k]（跳过的 null 与文件夹不占交付位）。
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
            _lastProgressTicks = 0;

            // 参与计算的下标（跳过 null 与文件夹），文件夹的体积也要计入总量，否则进度到不了 100。
            long total = 0;
            var work = new List<int>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                CheckItem item = items[i];
                if (item == null) continue;

                if (item.IsDirectory)
                {
                    if (item.Size > 0) total += item.Size;
                    continue;
                }

                work.Add(i);
                if (item.Size > 0) total += item.Size;
            }

            Interlocked.Exchange(ref _totalBytes, total);

            if (_onProgress != null) _onProgress(0);

            if (work.Count == 0)
            {
                // 只有目录（没有可算的文件）：目录体积已计入总量，这里一并算作已处理，
                // 否则进度会停在 0%——「只有目录的任务」也是要跑到 100% 的。
                Interlocked.Exchange(ref _processedBytes, total);
                if (_onProgress != null) _onProgress(ToPercent(ProcessedBytes, TotalBytes));
                return;
            }

            int threads = maxThreads < 1 ? 1 : maxThreads;
            if (threads > work.Count) threads = work.Count;

            RunOrdered(items, work, threads, onOutcome, token);

            if (_onProgress != null) _onProgress(ToPercent(ProcessedBytes, TotalBytes));
        }

        /// <summary>
        /// 取号 + 按号交付。工作线程领任务下标去算，结果进按位置索引的槽位；
        /// 交付线程从 nextDeliver 开始，只要槽位连续就依次 flush 出去。
        /// 这样既保留了多线程吞吐，又保证交付顺序与列表一致。
        ///
        /// 取消语义：取消异常在**工作线程内部**捕获（未捕获的线程异常会直接杀掉进程），
        /// 置 _canceled 标志并唤醒交付线程，由交付线程在调用方栈上抛 OperationCanceledException，
        /// 这样调用方看到的仍是标准契约。
        /// </summary>
        private void RunOrdered(
            IList<CheckItem> items,
            List<int> work,
            int threads,
            Action<HashOutcome> onOutcome,
            CancellationToken token)
        {
            var outcomes = new HashOutcome[work.Count];
            var ready = new bool[work.Count];

            var gate = new object();
            int nextDeliver = 0;

            // 这些标志要被多个工作线程的闭包写，所以放进一个共享的盒子而不是局部变量。
            var state = new RunState();

            // 任务游标：工作线程用 Interlocked 领号，天然无锁。
            int nextWork = -1;

            var workers = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                var worker = new Thread(delegate()
                {
                    try
                    {
                        while (true)
                        {
                            if (token.IsCancellationRequested)
                            {
                                MarkCanceled(gate, state);
                                return;
                            }

                            int slot = Interlocked.Increment(ref nextWork);
                            if (slot >= work.Count) return;

                            int itemIndex = work[slot];
                            HashOutcome outcome = Compute(items[itemIndex], token);

                            lock (gate)
                            {
                                outcomes[slot] = outcome;
                                ready[slot] = true;
                                Monitor.PulseAll(gate);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        MarkCanceled(gate, state);
                    }
                    catch (Exception ex)
                    {
                        lock (gate)
                        {
                            if (state.Failure == null) state.Failure = ex;
                            state.Stopped = true;
                            Monitor.PulseAll(gate);
                        }
                    }
                });

                worker.IsBackground = true;
                worker.Name = "HashWorker" + t;
                workers[t] = worker;
                worker.Start();
            }

            // 交付在调用线程上做：调用方本来就在后台线程（Task.Run 里），不占 UI 线程。
            Exception failure = null;
            try
            {
                int delivered = 0;
                while (delivered < work.Count)
                {
                    HashOutcome outcome = null;

                    lock (gate)
                    {
                        while (nextDeliver < work.Count && ready[nextDeliver])
                        {
                            outcome = outcomes[nextDeliver];
                            outcomes[nextDeliver] = null;
                            nextDeliver++;
                            delivered++;
                            break;
                        }

                        if (outcome == null)
                        {
                            if (state.Canceled) throw new OperationCanceledException(token);
                            if (state.Stopped) break;
                            Monitor.Wait(gate, 50);
                            continue;
                        }
                    }

                    if (onOutcome != null) onOutcome(outcome);
                }

                if (state.Canceled) throw new OperationCanceledException(token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                lock (gate)
                {
                    state.Stopped = true;
                    Monitor.PulseAll(gate);
                }

                foreach (Thread worker in workers)
                {
                    if (worker != null) worker.Join(2000);
                }
            }

            if (state.Failure != null) throw state.Failure;
            if (failure != null) throw failure;
        }

        /// <summary>一次 Run 的共享状态：取消 / 中止 / 首个失败原因。</summary>
        private sealed class RunState
        {
            public volatile bool Canceled;
            public volatile bool Stopped;
            public Exception Failure;
        }

        /// <summary>工作线程发现取消时统一走这里：置标志、唤醒交付线程。</summary>
        private static void MarkCanceled(object gate, RunState state)
        {
            lock (gate)
            {
                state.Canceled = true;
                state.Stopped = true;
                Monitor.PulseAll(gate);
            }
        }

        /// <summary>工作线程发现取消时统一走这里：置标志、唤醒交付线程。</summary>
        private static void MarkCanceled(object gate, ref bool canceled, ref bool stopped)
        {
            lock (gate)
            {
                canceled = true;
                stopped = true;
                Monitor.PulseAll(gate);
            }
        }

        /// <summary>算一个文件，把异常收进 outcome，绝不抛给调度层（取消除外）。</summary>
        private HashOutcome Compute(CheckItem item, CancellationToken token)
        {
            var outcome = new HashOutcome { Item = item };

            try
            {
                IList<HashAlgorithmInfo> wanted = item.RequiredAlgorithms;
                SortedDictionary<string, string> digests = HashFile(item.FullPath, wanted, item.Size, token);

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

                // 失败的也要把体积计入已处理，否则进度条永远差一截。
                Interlocked.Add(ref _processedBytes, item.Size > 0 ? item.Size : 0);
                ReportProgressThrottled();
            }

            return outcome;
        }

        /// <summary>
        /// 计算单个文件。读一遍文件、每个算法各喂一份，逐块上报进度；
        /// 一个算法都没有时退回默认算法，保证「只计算不比对」也有值。
        /// </summary>
        private SortedDictionary<string, string> HashFile(
            string path, IList<HashAlgorithmInfo> algorithms, long knownSize, CancellationToken token)
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

            // 已知大小可能为 0（目录项还没读到体积）或与实际不符（计算期间文件被改）。
            // 记下实际读到的字节数，收尾时把「总量」补齐或回退，进度才不会卡在 99% 或冲到 100% 后继续涨。
            long expected = knownSize > 0 ? knownSize : 0;
            long actual = 0;

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

                        actual += bytesRead;
                        Interlocked.Add(ref _processedBytes, bytesRead);
                        ReportProgressThrottled();
                    }
                }
            }
            finally
            {
                ReturnBuffer(buffer);

                if (expected != actual)
                {
                    // 体积猜错时校正总量：多算了就补回来，少算了就加进去。
                    Interlocked.Add(ref _totalBytes, actual - expected);
                }
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
        /// 用单调滴答数做差（不会溢出）、不做除法以外的浮点运算，
        /// 记录基准用 Interlocked，避免多个工作线程同时判定「到点了」而重复上报。
        /// </summary>
        private void ReportProgressThrottled()
        {
            Action<int> handler = _onProgress;
            if (handler == null) return;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref _lastProgressTicks);

            if (last != 0)
            {
                double elapsedMs = (now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (elapsedMs < ProgressIntervalMs) return;
            }

            // 抢到「更新基准」的那一个线程才真正上报，其余线程直接返回。
            if (Interlocked.CompareExchange(ref _lastProgressTicks, now, last) != last) return;

            handler(ToPercent(ProcessedBytes, TotalBytes));
        }
    }
}
