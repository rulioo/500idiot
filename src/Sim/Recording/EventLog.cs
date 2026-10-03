using IdiotSim.Core;

namespace IdiotSim.Recording;

/// <summary>事件类型（design.md §6.3）。</summary>
public enum EventType : byte
{
    Spawn = 0,
    DeathDrown,
    DeathShock,
    DeathInjury,
    DeathFall,

    // 道具交互 —— 全部计入"尝试记录"（§6.5.2）
    Grab,
    Drop,
    Throw,
    TouchProp,
    PushAgent,
    PushObject,
    InsertNailSafe,
    InsertNailLethal,
    NailPulledOut,   // 有人把插在插座上的铁钉拔走了 —— 插座不再是致死陷阱
    HeaterPush,
    HeaterPlug,
    HeaterKnob,
    HeaterStart,
    HeaterStop,
    HeaterFallIntoPool,
    ContactHeater,
    Burn,
    DrillTick,
    DrillSuccess,
    FireStart,
    FireOut,
    EnterPool,
    ExitPool,
    PropInPool,      // 道具掉进水里 —— 从此捞不回来，也不再是目标

    // 身体
    Injury,
    Heal,
    VitalityLow,
    HypothermiaLevelChange,

    // 认知
    Observe,
    BeliefGain,
    BeliefUpdate,
    BeliefForget,
    BeliefGeneralize,
    AttentionGrab,
    StateChange,
    EmotionChange,

    // 全局
    TempSample,
    ComfortReached,
    ComfortLost,
    Milestone,
    FirstEvent,
}

/// <summary>事件记录（design.md §6.2，24B + payload）。</summary>
public struct EventRecord
{
    public int Tick;
    public ushort AgentId;      // 0 = 环境事件
    public EventType Type;
    public Vec2 Pos;
    public int TargetA;
    public int TargetB;
    public float Value;
    public float Value2;
}

/// <summary>轨迹采样（design.md §6.2）。</summary>
public struct TrajSample
{
    public int Tick;
    public float X, Z;
    public byte Heading;
    public byte State;      // 0=Idle 1=Walk 2=Interact 3=Hurt 4=Fear 5=Panic 6=Dead
    public byte Flags;      // 持有物
}

/// <summary>身体状态采样（design.md §6.2，用于时间轴回看）。</summary>
public struct BodySample
{
    public int Tick;
    public byte AgentId;
    public byte Vitality;      // 0..100
    public byte CoreTempQ;     // 体温*5
    public byte Pain;          // 0..255
    public byte InjuryCount;
    public byte LastInjuryType;
}

/// <summary>
/// 追加写的事件日志。全局唯一真相源——尝试记录（§6.5）由它派生。
/// </summary>
public sealed class EventLog
{
    private EventRecord[] _items;
    public int Count { get; private set; }

    public EventLog(int capacity = 1 << 20)
    {
        _items = new EventRecord[capacity];
    }

    public void Add(in EventRecord e)
    {
        if (Count >= _items.Length) Array.Resize(ref _items, _items.Length * 2);
        _items[Count++] = e;
    }

    public ref EventRecord At(int i) => ref _items[i];
    public ReadOnlySpan<EventRecord> Span => new ReadOnlySpan<EventRecord>(_items, 0, Count);

    public void Clear() => Count = 0;
}

/// <summary>轨迹与身体采样的存储（按 agentId 分块的 SoA）。</summary>
public sealed class SampleStore
{
    private readonly List<TrajSample>[] _traj;
    private readonly List<BodySample>[] _body;

    public SampleStore(int agentCount)
    {
        _traj = new List<TrajSample>[agentCount + 1];
        _body = new List<BodySample>[agentCount + 1];
        for (int i = 0; i <= agentCount; i++)
        {
            _traj[i] = new List<TrajSample>(4096);
            _body[i] = new List<BodySample>(512);
        }
    }

    public void AddTraj(int agentId, in TrajSample s) => _traj[agentId].Add(s);
    public void AddBody(int agentId, in BodySample s) => _body[agentId].Add(s);

    public List<TrajSample> Traj(int agentId) => _traj[agentId];
    public List<BodySample> Body(int agentId) => _body[agentId];

    /// <summary>二分查找 <= tick 的最后一条轨迹采样。</summary>
    public static TrajSample? TrajAt(List<TrajSample> list, int tick)
    {
        if (list.Count == 0) return null;
        int lo = 0, hi = list.Count - 1, res = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (list[mid].Tick <= tick) { res = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (res < 0) return list[0];
        return list[res];
    }

    public static BodySample? BodyAt(List<BodySample> list, int tick)
    {
        if (list.Count == 0) return null;
        int lo = 0, hi = list.Count - 1, res = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (list[mid].Tick <= tick) { res = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (res < 0) return list[0];
        return list[res];
    }
}
