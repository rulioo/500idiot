using IdiotSim.Knowledge;

namespace IdiotSim;

public enum ActionType : byte
{
    Idle = 0,
    Wander,
    Approach,        // 走向某实体（纯移动，不产生交互）
    Grab,
    DropHeld,
    Throw,
    TouchProp,       // 通用试探
    InsertNail,      // 铁钉插插座孔
    ContactHeater,   // 触摸电暖气外壳
    PlugIn,          // 电暖气插头插入插座
    TurnKnob,        // 转动电暖气旋钮
    PushObject,      // 推动电暖气
    Drill,           // 钻木
    PushAgent,       // 推别的白痴
    Cry,
    Flee,
    Rest,
}

public enum Emotion : byte
{
    Calm = 0,
    Seeking,
    Curious,
    Hurt,
    Fear,
    Panic,
    Comfort,
}

public enum DeathCause : byte
{
    None = 0,
    Drown,
    Shock,
    Injury,
}

public enum EntityType : byte
{
    Socket = 0,
    Heater,
    Nail,
    Plank,
    Stick,
    Weed,
    Fire,
}

public enum SocketState : byte
{
    Empty = 0,
    NailInLive,      // 铁钉插在火线孔（危险残留）
    NailInNeutral,   // 铁钉插在零线孔（安全痕迹）
    PluggedIn,       // 插着电暖气
}

public enum InjuryType : byte
{
    None = 0,
    Burn,     // 烫伤
    Shock,    // 电击
    Fall,     // 摔伤
    Bruise,   // 推搡挫伤
}

/// <summary>身体伤势条目（design.md §3.7）。</summary>
public struct Injury
{
    public InjuryType Type;
    public float Severity;   // 0..1
    public int Tick;
    public int SourceEntityId;
    public int HealTick;
}

/// <summary>
/// 尝试记录条目（design.md §6.5.3）。每个白痴保留最近 64 条（环形缓冲），
/// 完整历史由事件日志派生。
/// </summary>
public struct AttemptEntry
{
    public int Tick;
    public EntityType Prop;
    public int InstanceId;      // 实体 Id
    public int InstanceIndex;   // 该类道具内的序号（电暖气#3）
    public ActionType Action;
    public int TargetId;
    public int TargetArg;       // 例如插座的孔位：+1 = 右孔，-1 = 左孔
    public OutcomeType Outcome;
    public float DVitality;
    public float DPain;
    public float DComfort;
    public KnowledgeId LearnedId;
    public float DConfidence;
    public int WitnessCount;
}
