using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public static class NtpTimeSync
{
    // ===== 常见公共 NTP（你可替换成自建或更稳定的池） =====
    private static readonly string[] AllServers =
    {
        // 稳定高优先级
        "pool.ntp.org",
        "time.google.com",
        "time.windows.com",

        // 区域池
        "asia.pool.ntp.org",
        "europe.pool.ntp.org",
        "north-america.pool.ntp.org",
        "oceania.pool.ntp.org",
        
        // 国内云/高校（如果你目标用户在国内，这些可能更稳）
        "ntp.aliyun.com",
        "time1.cloud.tencent.com",
        "time2.cloud.tencent.com",
        "time3.cloud.tencent.com",
        "time4.cloud.tencent.com",
        "time5.cloud.tencent.com",
        "ntp.tuna.tsinghua.edu.cn", // 清华 TUNA
        "time.pool.ac.cn", // 中国科学院
        "ntp1.huawei.com",
        "ntp2.huawei.com",
        "ntp3.huawei.com",

        // Apple / Google
        "time1.apple.com",
        "time2.apple.com",
        "time3.apple.com",
        "time4.apple.com",
        "time5.apple.com",
        "time6.apple.com",
        "time7.apple.com",

        "time1.google.com",
        "time2.google.com",
        "time3.google.com",
        "time4.google.com",
        
        "ntp.nict.jp", // 日本 NICT
     
        // 全球志愿者集群 自动就近
        "1.pool.ntp.org",
        "2.pool.ntp.org",
        "3.pool.ntp.org",

    };

    // 1900-01-01 到 1970-01-01 的秒差
    private const ulong NtpToUnixEpochSeconds = 2208988800UL;

    // ===== 对外状态 =====
    public static TimeSpan ResyncInterval { get; set; } = TimeSpan.FromHours(1);
    public static TimeSpan UtcOffset { get; private set; } = TimeSpan.Zero;

    public static double LastRttMs { get; private set; }
    public static DateTime LastSyncUtc { get; private set; } = DateTime.MinValue;

    // 粗略“估计误差”（工程尺度）
    public static double LastEstimatedErrorMs { get; private set; } = double.NaN;

    /// <summary>校准后的 UTC（不每次请求NTP，只用 offset 修正）</summary>
    public static DateTime UtcNowCorrected => DateTime.UtcNow + UtcOffset;

    /// <summary>校准后的本地时间</summary>
    public static DateTime NowCorrected => UtcNowCorrected.ToLocalTime();

    // ===== 并发控制 / 防抖 =====
    private static readonly SemaphoreSlim _syncLock = new SemaphoreSlim(1, 1);
    private static DateTime _lastAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan _minAttemptGap = TimeSpan.FromSeconds(5);

    // ===== 入口：你只需要调用它 =====
    /// <summary>自动校时：先 LoadOffset 再按需 Sync（支持 force）</summary>
    public static async Task<bool> AutoSync(bool force = false, int timeoutMs = 1500, CancellationToken ct = default)
    {
        LoadOffset();
        return await SyncIfNeededAsync(force, timeoutMs, ResyncInterval, ct);
    }

    public static bool ShouldSync(DateTime utcNow, TimeSpan? interval = null)
    {
        var itv = interval ?? ResyncInterval;
        if (LastSyncUtc == DateTime.MinValue) return true;
        return (utcNow - LastSyncUtc) >= itv;
    }

    public static async Task<bool> SyncIfNeededAsync(
        bool force = false,
        int timeoutMs = 1500,
        TimeSpan? interval = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        if (!force && !ShouldSync(now, interval))
            return true;

        // 防抖：短时间内不要重复发UDP
        if ((now - _lastAttemptUtc) < _minAttemptGap)
            return false;

        _lastAttemptUtc = now;

        await _syncLock.WaitAsync(ct);
        try
        {
            // 二次检查（防止等待锁期间已经被别的调用同步过）
            now = DateTime.UtcNow;
            if (!force && !ShouldSync(now, interval))
                return true;

            var (ok, bestUtc, bestOffset, bestRttMs, estErrMs) =
                await SyncOnceMultiServerAsync(timeoutMs, AllServers, ct);

            if (!ok) return false;

            UtcOffset = bestOffset;
            LastRttMs = bestRttMs;
            LastSyncUtc = bestUtc;
            LastEstimatedErrorMs = estErrMs;

            SaveOffset();
            return true;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    // ===== 多服务器同步核心 =====

    private struct Sample
    {
        public string server;
        public TimeSpan offset; // serverUtc - localMidUtc
        public double rttMs;
        public DateTime serverUtc;
    }

    /// <summary>
    /// 并发请求多个 NTP，做鲁棒聚合，得到更可信的 offset
    /// </summary>
    private static async Task<(bool ok, DateTime bestUtc, TimeSpan bestOffset, double bestRttMs, double estErrMs)>
        SyncOnceMultiServerAsync(int timeoutMs, string[] servers, CancellationToken ct)
    {
        // 并发上限：别把UDP打成烟花
        int maxParallel = 8;

        // 目标至少拿到几个有效样本（少于则退化为“取 RTT 最小”）
        int minSamples = 3;

        // RTT 过大直接丢（你可按网络调）
        double maxRttMs = 400;

        var samples = new List<Sample>();
        var tasks = new List<Task>();

        using var sem = new SemaphoreSlim(maxParallel);

        foreach (var s in servers)
        {
            await sem.WaitAsync(ct);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var (serverUtc, rttMs, offset) = await QueryServerAndComputeOffsetAsync(s, timeoutMs, ct);

                    if (rttMs <= maxRttMs)
                    {
                        lock (samples)
                        {
                            samples.Add(new Sample
                            {
                                server = s,
                                serverUtc = serverUtc,
                                rttMs = rttMs,
                                offset = offset
                            });
                        }
                    }
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    sem.Release();
                }
            }, ct));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // ignore
        }

        if (samples.Count < 1)
            return (false, DateTime.MinValue, TimeSpan.Zero, 0, double.NaN);

        // 样本不足：退化策略 -> 选 RTT 最小
        if (samples.Count < minSamples)
        {
            var best = samples.OrderBy(x => x.rttMs).First();
            return (true, best.serverUtc, best.offset, best.rttMs, best.rttMs * 0.5);
        }

        // 1) median(offset) 作为共识中心
        var offsetsMs = samples.Select(x => x.offset.TotalMilliseconds).OrderBy(x => x).ToList();
        double median = Median(offsetsMs);

        // 2) 离群检测：计算偏离 median 的绝对误差
        var withDev = samples.Select(x => new
        {
            sample = x,
            devMs = Math.Abs(x.offset.TotalMilliseconds - median)
        }).ToList();

        // 3) MAD: median absolute deviation
        var devList = withDev.Select(x => x.devMs).OrderBy(x => x).ToList();
        double mad = Median(devList);

        // 4) 离群阈值：max(150ms, 3*MAD)
        double outlierThreshold = Math.Max(150.0, mad * 3.0);

        var inliers = withDev
            .Where(x => x.devMs <= outlierThreshold)
            .Select(x => x.sample)
            .ToList();

        if (inliers.Count < 2)
        {
            var best = samples.OrderBy(x => x.rttMs).First();
            return (true, best.serverUtc, best.offset, best.rttMs, best.rttMs * 0.5);
        }

        // 5) RTT 加权平均（RTT 越小权重越大）
        // weight = 1/(rtt+1)^2
        double sumW = 0;
        double sumOffsetMs = 0;
        double bestRtt = inliers.Min(x => x.rttMs);

        foreach (var x in inliers)
        {
            double w = 1.0 / Math.Pow((x.rttMs + 1.0), 2);
            sumW += w;
            sumOffsetMs += x.offset.TotalMilliseconds * w;
        }

        double fusedOffsetMs = sumOffsetMs / sumW;
        var fusedOffset = TimeSpan.FromMilliseconds(fusedOffsetMs);

        // 6) 估计误差（工程粗估）：inliers 的 MAD + bestRtt/2
        var inlierDev = inliers
            .Select(x => Math.Abs(x.offset.TotalMilliseconds - fusedOffsetMs))
            .OrderBy(x => x)
            .ToList();
        double inlierMad = Median(inlierDev);
        double estErrMs = inlierMad + bestRtt * 0.5;

        // 代表性 bestUtc：当前本机 UTC + fusedOffset
        var bestUtc = DateTime.UtcNow + fusedOffset;

        return (true, bestUtc, fusedOffset, bestRtt, estErrMs);
    }

    private static double Median(List<double> sorted)
    {
        if (sorted == null || sorted.Count == 0) return double.NaN;
        int n = sorted.Count;
        if (n % 2 == 1) return sorted[n / 2];
        return (sorted[n / 2 - 1] + sorted[n / 2]) * 0.5;
    }

    // ===== NTP 查询 + offset 计算（关键：中点法） =====

    private static async Task<(DateTime serverUtc, double rttMs, TimeSpan offset)> QueryServerAndComputeOffsetAsync(
        string server, int timeoutMs, CancellationToken ct)
    {
        const int ntpPort = 123;

        // NTP request 48 bytes
        // 0x23 = LI=0, VN=4, Mode=3 (client)
        byte[] request = new byte[48];
        request[0] = 0x23;

        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = timeoutMs;
        udp.Client.SendTimeout = timeoutMs;

        var addresses = await Dns.GetHostAddressesAsync(server);
        if (addresses == null || addresses.Length == 0)
            throw new Exception($"DNS resolve failed: {server}");

        // 优先 IPv4（很多移动/部分环境 IPv6 不稳定）
        var addrList = addresses
            .OrderByDescending(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();

        Exception lastEx = null;

        foreach (var addr in addrList)
        {
            try
            {
                var endPoint = new IPEndPoint(addr, ntpPort);

                var t0 = DateTime.UtcNow;
                await udp.SendAsync(request, request.Length, endPoint);

                var receiveTask = udp.ReceiveAsync();
                var completed = await Task.WhenAny(receiveTask, Task.Delay(timeoutMs, ct));
                if (completed != receiveTask)
                    throw new TimeoutException($"NTP timeout: {server} ({addr})");

                var t1 = DateTime.UtcNow;
                var response = receiveTask.Result.Buffer;

                if (response == null || response.Length < 48)
                    throw new Exception($"Invalid NTP response: {server} ({addr})");

                // Transmit Timestamp [40..47]
                ulong intPart =
                    ((ulong)response[40] << 24) |
                    ((ulong)response[41] << 16) |
                    ((ulong)response[42] << 8) |
                    ((ulong)response[43]);

                ulong fracPart =
                    ((ulong)response[44] << 24) |
                    ((ulong)response[45] << 16) |
                    ((ulong)response[46] << 8) |
                    ((ulong)response[47]);

                ulong unixSeconds = intPart - NtpToUnixEpochSeconds;

                // frac / 2^32 秒 -> 毫秒
                double fracMs = (fracPart / 4294967296.0) * 1000.0;

                var serverUtc = DateTimeOffset
                    .FromUnixTimeSeconds((long)unixSeconds)
                    .UtcDateTime
                    .AddMilliseconds(fracMs);

                double rttMs = (t1 - t0).TotalMilliseconds;
                var localMidUtc = t0 + TimeSpan.FromMilliseconds(rttMs * 0.5);

                var offset = serverUtc - localMidUtc;
                return (serverUtc, rttMs, offset);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                // try next address
            }
        }

        throw lastEx ?? new Exception($"NTP query failed: {server}");
    }

    // ===== 持久化 =====
    private const string PrefKeyTicks = "NTP_UTC_OFFSET_TICKS";
    private const string PrefKeySyncUtcTicks = "NTP_LAST_SYNC_UTC_TICKS";
    private const string PrefKeyErrMs = "NTP_LAST_ERR_MS";
    private const string PrefKeyRttMs = "NTP_LAST_RTT_MS";

    public static void SaveOffset()
    {
        PlayerPrefs.SetString(PrefKeyTicks, UtcOffset.Ticks.ToString());
        PlayerPrefs.SetString(PrefKeySyncUtcTicks, LastSyncUtc.Ticks.ToString());
        PlayerPrefs.SetString(PrefKeyErrMs, double.IsNaN(LastEstimatedErrorMs) ? "" : LastEstimatedErrorMs.ToString("F3"));
        PlayerPrefs.SetString(PrefKeyRttMs, LastRttMs.ToString("F3"));
        PlayerPrefs.Save();
    }

    public static bool LoadOffset()
    {
        if (!PlayerPrefs.HasKey(PrefKeyTicks)) return false;

        if (long.TryParse(PlayerPrefs.GetString(PrefKeyTicks), out var ticks))
        {
            UtcOffset = TimeSpan.FromTicks(ticks);

            if (long.TryParse(PlayerPrefs.GetString(PrefKeySyncUtcTicks), out var syncTicks))
                LastSyncUtc = new DateTime(syncTicks, DateTimeKind.Utc);

            if (double.TryParse(PlayerPrefs.GetString(PrefKeyErrMs), out var err))
                LastEstimatedErrorMs = err;

            if (double.TryParse(PlayerPrefs.GetString(PrefKeyRttMs), out var rtt))
                LastRttMs = rtt;

            return true;
        }

        return false;
    }
}