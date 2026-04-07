using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// TimeService：统一对外提供“可信时间”
/// 优先级：
/// 1) 服务器时间（如果曾经设置过）
/// 2) NTP 校准时间（如果 NtpTimeSync 可用）
/// 3) 本地系统时间（最后兜底）
///
/// 同时提供：
/// - UTC 秒 / UTC 毫秒
/// - 本地(非UTC) 秒 / 本地(非UTC) 毫秒
/// </summary>
public static class TimeService
{
    // ====== 配置项 ======
    /// <summary>如果服务器时间与本机差得离谱，是否忽略服务器时间（防止误设置）</summary>
    public static bool EnableServerSanityCheck = true;

    /// <summary>服务器时间与本机 UTC 的最大允许偏差（超过则认为服务器时间无效）</summary>
    public static TimeSpan ServerMaxSkew = TimeSpan.FromDays(7);

    /// <summary>NTP 同步超时(ms)</summary>
    public static int NtpTimeoutMs = 1500;

    // ====== 内部状态：服务器时间锚点 ======
    // 服务器时间用“锚点”方式：记录当时服务器给的 utc，以及本地 monotonic/utc
    // 之后通过 DateTime.UtcNow 推算当前服务器时间，避免每帧都依赖网络。
    private static bool _hasServerAnchor;
    private static DateTime _serverAnchorUtc;      // 服务器给的 UTC 时间（锚点）
    private static DateTime _localAnchorUtc;       // 设置锚点那一刻的本机 UTC
    private static long _localAnchorRealtimeMs;    // 设置锚点那一刻的 Unity realtime(ms)（更抗系统改时）

    private const string PrefHasServer = "TS_HAS_SERVER";
    private const string PrefServerAnchorUtcTicks = "TS_SERVER_ANCHOR_UTC_TICKS";
    private const string PrefLocalAnchorUtcTicks = "TS_LOCAL_ANCHOR_UTC_TICKS";
    private const string PrefLocalAnchorRtMs = "TS_LOCAL_ANCHOR_RT_MS";

    // ====== Epoch 常量 ======
    private static readonly DateTime UnixEpochUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ====== 生命周期 ======
    /// <summary>
    /// 建议在游戏启动时调用一次：加载服务器锚点 & 预热NTP（非强制）
    /// </summary>
    public static async Task InitializeAsync(bool warmupNtp = true, CancellationToken ct = default)
    {
        LoadServerAnchor();

        if (warmupNtp)
        {
            try
            {
                // 只要 NtpTimeSync.cs 在工程里，这里就能调用
                await NtpTimeSync.AutoSync(force: false, timeoutMs: NtpTimeoutMs, ct: ct);
            }
            catch
            {
                // ignore：初始化不该因为NTP失败阻塞
            }
        }
    }

    /// <summary>
    /// App 回前台时可以调用，按需刷新 NTP（不强制）
    /// </summary>
    public static async Task OnResumeAsync(bool forceNtp = false, CancellationToken ct = default)
    {
        try
        {
            await NtpTimeSync.AutoSync(force: forceNtp, timeoutMs: NtpTimeoutMs, ct: ct);
        }
        catch
        {
            // ignore
        }
    }

    // ====== 服务器时间设置接口（你要的多种形式） ======

    /// <summary>设置：服务器 UTC 秒级时间戳</summary>
    public static void SetServerUtcSeconds(long utcSeconds)
    {
        SetServerUtcMilliseconds(checked(utcSeconds * 1000L));
    }

    /// <summary>设置：服务器 UTC 毫秒级时间戳</summary>
    public static void SetServerUtcMilliseconds(long utcMilliseconds)
    {
        var serverUtc = UnixEpochUtc.AddMilliseconds(utcMilliseconds);
        SetServerUtcDateTime(serverUtc);
    }

    /// <summary>设置：服务器“本地(非UTC)”秒级时间戳（不推荐用于网络协议）</summary>
    public static void SetServerLocalSeconds(long localSeconds)
    {
        SetServerLocalMilliseconds(checked(localSeconds * 1000L));
    }

    /// <summary>设置：服务器“本地(非UTC)”毫秒级时间戳（不推荐用于网络协议）</summary>
    public static void SetServerLocalMilliseconds(long localMilliseconds)
    {
        // 这里把“本地epoch”解释为：以本地时区的 1970-01-01 00:00:00 作为起点
        // 即：DateTimeKind.Local 下的 epoch
        var localEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var serverLocal = localEpoch.AddMilliseconds(localMilliseconds);

        // 转成 UTC 作为内部统一表示
        var serverUtc = serverLocal.ToUniversalTime();
        SetServerUtcDateTime(serverUtc);
    }

    /// <summary>
    /// 直接设置服务器 UTC 时间（内部统一入口）
    /// </summary>
    public static void SetServerUtcDateTime(DateTime serverUtc)
    {
        if (serverUtc.Kind != DateTimeKind.Utc)
            serverUtc = DateTime.SpecifyKind(serverUtc, DateTimeKind.Utc);

        if (EnableServerSanityCheck)
        {
            var skew = (serverUtc - DateTime.UtcNow).Duration();
            if (skew > ServerMaxSkew)
            {
                Debug.LogWarning($"[TimeService] Ignore server time due to huge skew: {skew.TotalHours:F1}h");
                return;
            }
        }

        _hasServerAnchor = true;
        _serverAnchorUtc = serverUtc;
        _localAnchorUtc = DateTime.UtcNow;
        _localAnchorRealtimeMs = GetRealtimeMs();

        SaveServerAnchor();
    }

    /// <summary>清除服务器时间（回到 NTP->本地兜底）</summary>
    public static void ClearServerTime()
    {
        _hasServerAnchor = false;
        PlayerPrefs.DeleteKey(PrefHasServer);
        PlayerPrefs.DeleteKey(PrefServerAnchorUtcTicks);
        PlayerPrefs.DeleteKey(PrefLocalAnchorUtcTicks);
        PlayerPrefs.DeleteKey(PrefLocalAnchorRtMs);
        PlayerPrefs.Save();
    }

    // ====== 核心：获取“可信时间” ======

    
    /// <summary>当前可信 UTC 时间</summary>
    public static DateTime UtcNow
    {
        get
        {
            // 1) Server
            if (TryGetServerUtc(out var serverUtc))
                return serverUtc;

            // 2) NTP
            try
            {
                // NtpTimeSync 内部：DateTime.UtcNow + offset
                // 若从未成功同步，offset 默认为 0，此时等价本机 UTC
                // 你如果想更严格（没同步过就不用），可以改成判断 LastSyncUtc != MinValue
                return NtpTimeSync.UtcNowCorrected;
            }
            catch
            {
                // 3) Local
                return DateTime.UtcNow;
            }
        }
    }

    
    public static long UtcNowTicks => UtcNow.Ticks;
    public static long NowTicks => Now.Ticks;

    /// <summary>当前可信本地时间（非UTC）</summary>
    public static DateTime Now => UtcNow.ToLocalTime();

    // ====== 你要的四种时间戳接口 ======

    /// <summary>UTC 秒级时间戳（可信时间）</summary>
    public static long UtcSeconds => ToUnixSeconds(UtcNow);

    /// <summary>UTC 毫秒级时间戳（可信时间）</summary>
    public static long UtcMilliseconds => ToUnixMilliseconds(UtcNow);

    /// <summary>本地(非UTC) 秒级时间戳（可信时间的本地表示）</summary>
    public static long LocalSeconds => ToLocalEpochSeconds(Now);

    /// <summary>本地(非UTC) 毫秒级时间戳（可信时间的本地表示）</summary>
    public static long LocalMilliseconds => ToLocalEpochMilliseconds(Now);

    // ====== 状态/调试 ======
    public static bool HasServerTime => _hasServerAnchor;

    /// <summary>是否有过成功的 NTP 同步（你可以用它决定展示“已校时”标识）</summary>
    public static bool HasNtpSync => NtpTimeSync.LastSyncUtc != DateTime.MinValue;

    public static double LastNtpRttMs => NtpTimeSync.LastRttMs;
    public static double LastNtpEstimatedErrorMs => NtpTimeSync.LastEstimatedErrorMs;

    // ====== 内部实现 ======

    private static bool TryGetServerUtc(out DateTime serverUtc)
    {
        serverUtc = default;
        if (!_hasServerAnchor) return false;

        // 用 realtimeSinceStartup 推算（更抗系统改时）
        long nowRtMs = GetRealtimeMs();
        long deltaMs = nowRtMs - _localAnchorRealtimeMs;

        // realtime 可能因为某些平台重启/溢出/异常出现负值，这里兜底成 DateTime.UtcNow
        if (deltaMs < 0 || deltaMs > (long)TimeSpan.FromDays(3650).TotalMilliseconds)
        {
            var deltaUtc = DateTime.UtcNow - _localAnchorUtc;
            serverUtc = _serverAnchorUtc + deltaUtc;
            return true;
        }

        serverUtc = _serverAnchorUtc.AddMilliseconds(deltaMs);
        return true;
    }

    private static long GetRealtimeMs()
    {
        // realtimeSinceStartupAsDouble 更平滑，避免 float 精度问题
#if UNITY_2020_2_OR_NEWER
        return (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
#else
        return (long)(Time.realtimeSinceStartup * 1000f);
#endif
    }

    private static long ToUnixSeconds(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc) utc = utc.ToUniversalTime();
        return (long)(utc - UnixEpochUtc).TotalSeconds;
    }

    private static long ToUnixMilliseconds(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc) utc = utc.ToUniversalTime();
        return (long)(utc - UnixEpochUtc).TotalMilliseconds;
    }

    private static long ToLocalEpochSeconds(DateTime localTime)
    {
        // 以本地时区的 epoch 为起点（1970-01-01 00:00:00 Local）
        var localEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
        if (localTime.Kind != DateTimeKind.Local) localTime = localTime.ToLocalTime();
        return (long)(localTime - localEpoch).TotalSeconds;
    }

    private static long ToLocalEpochMilliseconds(DateTime localTime)
    {
        var localEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
        if (localTime.Kind != DateTimeKind.Local) localTime = localTime.ToLocalTime();
        return (long)(localTime - localEpoch).TotalMilliseconds;
    }

    private static void SaveServerAnchor()
    {
        PlayerPrefs.SetInt(PrefHasServer, _hasServerAnchor ? 1 : 0);
        PlayerPrefs.SetString(PrefServerAnchorUtcTicks, _serverAnchorUtc.Ticks.ToString());
        PlayerPrefs.SetString(PrefLocalAnchorUtcTicks, _localAnchorUtc.Ticks.ToString());
        PlayerPrefs.SetString(PrefLocalAnchorRtMs, _localAnchorRealtimeMs.ToString());
        PlayerPrefs.Save();
    }

    private static void LoadServerAnchor()
    {
        _hasServerAnchor = PlayerPrefs.GetInt(PrefHasServer, 0) == 1;
        if (!_hasServerAnchor) return;

        if (long.TryParse(PlayerPrefs.GetString(PrefServerAnchorUtcTicks, ""), out var sTicks))
            _serverAnchorUtc = new DateTime(sTicks, DateTimeKind.Utc);
        else
            _hasServerAnchor = false;

        if (long.TryParse(PlayerPrefs.GetString(PrefLocalAnchorUtcTicks, ""), out var lTicks))
            _localAnchorUtc = new DateTime(lTicks, DateTimeKind.Utc);

        if (long.TryParse(PlayerPrefs.GetString(PrefLocalAnchorRtMs, ""), out var rtMs))
            _localAnchorRealtimeMs = rtMs;

        // 兜底：如果 anchor 不完整，直接判无效
        if (_serverAnchorUtc == default || _localAnchorUtc == default)
            _hasServerAnchor = false;
    }
}
