using IdiotSim.Agents;

namespace IdiotSim.Knowledge;

/// <summary>知识原子（design.md §4.1）。</summary>
public enum KnowledgeId : byte
{
    None = 0,

    // 电暖气链
    HeaterPush,          // 这东西推得动
    HeaterPlugFits,      // 插头能插进插座
    HeaterKnobExists,    // 侧面有个能转的东西
    HeaterWarm,          // 开着的电暖气会让人变暖   <- H4，G1 判定依据
    HeaterHot,           // 开着的电暖气烫人         <- 与上条形成悲剧张力

    // 火链
    StickPlankTogether,  // 木棍和木板可以放一起
    DrillingMotion,      // "搓"这个动作
    FireWarm,            // 火很暖和                 <- G2 判定依据

    // 危险链
    HoleLeftSafe,        // 左孔安全
    HoleRightDeadly,     // 右孔致命
    NailDangerous,       // 铁钉危险（粗糙、未区分孔位）
    WaterDanger,         // 水会淹死人
    WaterBody,           // 水面上那个东西是（曾经的）同类
}

public enum BeliefSource : byte
{
    PersonalExperience = 0,
    Observed = 1,
    Imitated = 2,
    Rumor = 3,
}

/// <summary>信念（design.md §4.2）。</summary>
public struct Belief
{
    public KnowledgeId Id;
    public int InstanceId;       // -1 = 已泛化为类级知识；否则绑定具体实例
    public float Confidence;     // 0..1
    public BeliefSource Source;
    public int WitnessId;        // 从谁那里学到的（0 = 无）
    public int LearnTick;
    public byte ReinforceCount;
    public bool Generalized;

    public static bool SameKey(in Belief b, KnowledgeId id, int instanceId)
        => b.Id == id && b.InstanceId == instanceId;
}

/// <summary>观察记忆（design.md §4.3）。</summary>
public struct Observation
{
    public int Tick;
    public int ActorId;
    public ActionType Action;
    public int TargetEntityId;
    public OutcomeType Outcome;
    public float Clarity;
}

public enum OutcomeType : byte
{
    None = 0,
    Success = 1,
    NoEffect = 2,
    Injury = 3,
    Death = 4,
    Warm = 5,
    Startled = 6,
}
