using System.Diagnostics;
using IdiotSim;
using IdiotSim.Agents;
using IdiotSim.Core;
using IdiotSim.Recording;

namespace IdiotSim.Viewer;

/// <summary>
/// 后台仿真线程 + 回放数据访问（design.md §5.3 / §7.2）。
///
/// 核心约定：**观测不进入仿真层**。玩家怎么走、看哪、拖时间轴，
/// 都不产生任何一次 Step() 之外的副作用（§7.5.1 确定性要求 A1）。
/// 时间轴回看完全走已录制的 Samples + Events，不重跑仿真。
/// </summary>
public sealed class SimRunner : IDisposable
{
    public readonly Simulation Sim;
    public readonly SimConfig Cfg;

    private readonly Thread _thread;
    private volatile bool _quit;
    private readonly object _lock = new();

    /// <summary>播放倍速：1.0 = 需求定义的"1 真实秒 = 60 游戏秒"。</summary>
    public volatile float Speed = 1f;
    public volatile bool Paused;

    /// <summary>仿真头（已经算到哪一 tick）。</summary>
    public int HeadTick => Sim.Tick;

    /// <summary>本次实验的总时长（游戏秒），到点自动暂停。</summary>
    public readonly float TargetGameSeconds;
    public int TargetTick => Cfg.SecondsToTicks(TargetGameSeconds);
    public bool Finished { get; private set; }

    /// <summary>用户拖动的查看位置。负数表示"跟随仿真头"。</summary>
    public int ScrubTick = -1;

    /// <summary>当前应该渲染的 tick。</summary>
    public int ViewTick
    {
        get
        {
            int s = ScrubTick;
            return s >= 0 ? Math.Min(s, Sim.Tick) : Sim.Tick;
        }
    }

    public bool IsLive => ScrubTick < 0;

    /// <summary>实时性能：上一个真实秒里算了多少 tick。</summary>
    public float TicksPerRealSecond { get; private set; }
    public float UsPerTick { get; private set; }
    public bool Ready { get; private set; }

    public SimRunner(SimConfig cfg, float targetGameSeconds)
    {
        Cfg = cfg;
        TargetGameSeconds = targetGameSeconds;
        Sim = new Simulation(cfg);
        _thread = new Thread(Loop) { IsBackground = true, Name = "sim" };
        _thread.Start();
    }

    private void Loop()
    {
        var sw = new Stopwatch();
        var window = new Stopwatch();
        window.Start();
        int ticksInWindow = 0;

        while (!_quit)
        {
            if (Paused || Finished || ScrubTick >= 0)
            {
                // 暂停 / 回看时不推进仿真——回放走录制数据，不重跑
                Thread.Sleep(8);
                window.Restart();
                ticksInWindow = 0;
                continue;
            }

            // 本批要算多少 tick：按倍速折算到真实时间。
            // 1 真实秒 = 60 游戏秒 = 240 tick，所以 speed=1 时每秒 240 tick。
            int batch = Math.Max(1, (int)(240f * Speed / 60f));
            sw.Restart();
            for (int i = 0; i < batch && !_quit; i++)
            {
                if (Sim.Tick >= TargetTick) { Finished = true; break; }
                lock (_lock) Sim.Step();
            }
            sw.Stop();

            ticksInWindow += batch;
            if (window.Elapsed.TotalSeconds >= 1.0)
            {
                // 按真实经过时间给出"这一秒实际推进了多少游戏时间"
                TicksPerRealSecond = (float)(ticksInWindow / window.Elapsed.TotalSeconds);
                UsPerTick = TicksPerRealSecond > 0 ? 1e6f / TicksPerRealSecond : 0f;
                window.Restart();
                ticksInWindow = 0;
            }
            Ready = true;

            // 稳定在 60fps 附近的推进节奏，别把 CPU 打满
            double targetMs = batch / (240.0 * Speed / 60.0 * 60.0) * 1000.0;
            int sleep = (int)Math.Clamp(targetMs - sw.Elapsed.TotalMilliseconds, 0, 50);
            if (sleep > 0 && !_quit) Thread.Sleep(sleep);
        }
    }

    /// <summary>直接推进到指定 tick（无倍速限制），用于"跳到里程碑"。</summary>
    public void SkipTo(int tick)
    {
        tick = Math.Clamp(tick, 0, TargetTick);
        ScrubTick = -1;
        Finished = false;
        while (Sim.Tick < tick && !_quit)
        {
            lock (_lock)
            {
                int n = Math.Min(2000, tick - Sim.Tick);
                for (int i = 0; i < n; i++) Sim.Step();
            }
        }
    }

    /// <summary>回到某个历史时刻：暂停并跟随仿真头。</summary>
    public void JumpTo(int tick)
    {
        ScrubTick = Math.Clamp(tick, 0, Sim.Tick);
        Paused = true;
    }

    public void GoLive()
    {
        ScrubTick = -1;
    }

    // =====================================================================
    //  回放查询（全部走录制数据，不触碰仿真状态）
    // =====================================================================

    /// <summary>某个白痴在指定 tick 的位置。超出录制范围时取最近的一条。</summary>
    public bool TryGetPose(int agentId, int tick, out Vec2 pos, out byte state)
    {
        pos = default; state = 0;
        if (agentId <= 0 || agentId > Cfg.AgentCount) return false;
        var list = Sim.Samples.Traj(agentId);
        var s = SampleStore.TrajAt(list, tick);
        if (s == null) return false;
        pos = new Vec2(s.Value.X, s.Value.Z);
        state = s.Value.State;
        return true;
    }

    public BodySample? GetBody(int agentId, int tick)
    {
        if (agentId <= 0 || agentId > Cfg.AgentCount) return null;
        return SampleStore.BodyAt(Sim.Samples.Body(agentId), tick);
    }

    /// <summary>
    /// 某个白痴截至 tick 的道具尝试记录（design.md §6.5）。
    /// 由事件日志派生——它是唯一真相源，agent 里的环形缓冲只是缓存。
    /// </summary>
    public List<EventRecord> AttemptsUpTo(int agentId, int tick, int max = 400)
    {
        var res = new List<EventRecord>();
        var span = Sim.Events.Span;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var e = ref span[i];
            if (e.Tick > tick) break;              // 事件按 tick 追加，天然有序
            if (e.AgentId != agentId) continue;
            if (!IsAttemptEvent(e.Type)) continue;
            res.Add(e);
        }
        // 最近的在最上面
        res.Reverse();
        if (res.Count > max) res.RemoveRange(max, res.Count - max);
        return res;
    }

    public static bool IsAttemptEvent(EventType t) => t switch
    {
        EventType.Grab or EventType.Drop or EventType.Throw or EventType.TouchProp
            or EventType.PushAgent or EventType.PushObject
            or EventType.InsertNailSafe or EventType.InsertNailLethal
            or EventType.HeaterPush or EventType.HeaterPlug or EventType.HeaterKnob
            or EventType.HeaterStart or EventType.ContactHeater or EventType.Burn
            or EventType.DrillTick or EventType.DrillSuccess or EventType.FireStart
            or EventType.DeathDrown or EventType.DeathShock or EventType.DeathInjury
            or EventType.EnterPool => true,
        _ => false,
    };

    /// <summary>某个白痴截至 tick 的伤势列表。</summary>
    public List<EventRecord> InjuriesUpTo(int agentId, int tick, int max = 12)
    {
        var res = new List<EventRecord>();
        var span = Sim.Events.Span;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var e = ref span[i];
            if (e.Tick > tick) break;
            if (e.AgentId != agentId) continue;
            if (e.Type is EventType.Injury or EventType.Burn) res.Add(e);
        }
        res.Reverse();
        if (res.Count > max) res.RemoveRange(max, res.Count - max);
        return res;
    }

    /// <summary>某个白痴是否在 tick 之前已经死亡，以及死因。</summary>
    public bool IsDeadAt(int agentId, int tick, out DeathCause cause)
    {
        cause = DeathCause.None;
        if (agentId <= 0 || agentId > Cfg.AgentCount) return false;
        var span = Sim.Events.Span;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var e = ref span[i];
            if (e.Tick > tick) break;
            if (e.AgentId != agentId) continue;
            if (e.Type == EventType.DeathDrown) { cause = DeathCause.Drown; return true; }
            if (e.Type == EventType.DeathShock) { cause = DeathCause.Shock; return true; }
            if (e.Type == EventType.DeathInjury) { cause = DeathCause.Injury; return true; }
        }
        return false;
    }

    public int AliveAt(int tick)
    {
        int dead = 0;
        for (int id = 1; id <= Cfg.AgentCount; id++)
            if (IsDeadAt(id, tick, out _)) dead++;
        return Cfg.AgentCount - dead;
    }

    public void Dispose()
    {
        _quit = true;
        try { _thread.Join(1500); } catch { }
    }
}
