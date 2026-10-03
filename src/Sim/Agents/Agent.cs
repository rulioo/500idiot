using IdiotSim.Core;
using IdiotSim.Knowledge;

namespace IdiotSim.Agents;

/// <summary>
/// 一个白痴。躯体成年、心智 3 岁（design.md §3.1）。
/// 注意：此类中**没有任何规划器**——行为完全由 §3.4 的效用函数涌现。
/// </summary>
public sealed class Agent
{
    public int Id;                      // 1..500，身上携带的编号
    public Vec2 Pos;
    public Vec2 Facing = new(0, 1);
    public bool Alive = true;
    public DeathCause Death;
    public int DeathTick;

    // ---------- 身体（design.md §3.7）----------
    public float Vitality = 100f;
    public float CoreTemp = 36.8f;
    public float Pain;
    public float Dexterity = 1f;        // 由 coreTemp 与 pain 导出，只读
    public readonly List<Injury> Injuries = new(4);

    // ---------- 需求与情绪（§3.3）----------
    public float Comfort;
    public float Warmth;
    public float Safety;
    public float Fear;
    public Emotion Emo = Emotion.Calm;
    public float LocalTemp = 15f;

    // ---------- 个体参数（§3.6）----------
    public float PCuriosity, PBoldness, PImitation, PPerseveration;
    public float PAttentionSpan, PNoiseSigma, PSocial, PFearThreshold, PPainTolerance;

    // ---------- 当前行为 ----------
    public ActionType Action = ActionType.Idle;
    public int TargetId = -1;           // 目标实体 Id（或目标白痴 Id）
    public bool TargetIsAgent;
    public int TargetArg;               // 附加参数（如推动方向索引、孔位）
    public float ActionTimer;           // 剩余游戏秒
    /// <summary>
    /// 计时器归属：当前 ActionTimer 是在为哪个 (动作,目标) 计时。
    /// 一个动作被打断（去干别的）之后再回来，计时器要能接着走——
    /// 否则时长 &gt;3 个大脑 tick 的动作永远做不完（搓火需要 5 个）。
    /// </summary>
    public ActionType TimerAct = ActionType.Idle;
    public int TimerTarget = -1;
    public int RepeatLeft;              // 执念剩余重复次数（§3.1）
    /// <summary>
    /// 赶路承诺：在走到目标之前不再重新决策。3 岁小孩每 4 tick 就想一次
    /// "我现在到底想干嘛"，20 米的路要走 17 次决策，半路必然被自己劝退。
    /// </summary>
    public int CommitUntilTick;
    public int HeldId = -1;
    public float DrillProgress;         // 钻木取火的累计进度（游戏秒）

    // ---------- 注意力（§3.2）----------
    public int AttentionTarget = -1;    // 实体 Id 或白痴 Id
    public bool AttentionIsAgent;
    public int AttentionUntilTick;
    public float AttentionStrength;

    // ---------- 疼痛/威胁来源 ----------
    public int PainSourceId = -1;
    public int ThreatId = -1;
    public Vec2 FleeFrom;
    public bool Fleeing;
    /// <summary>被人推搡后的"踉跄期"。这期间在池边站不稳，会被推下水。</summary>
    public int PushedUntilTick;

    // ---------- 移动 ----------
    public Vec2 MoveTarget;
    public bool HasMoveTarget;

    // ---------- 认知（§4）----------
    public readonly List<Belief> Beliefs = new(24);
    public readonly List<Observation> RecentObs = new(8);
    public readonly List<int> KnownActions = new(8);   // 模仿解锁的动作（§3.4 动作候选集）

    // ---------- 尝试记录（§6.5）----------
    public const int LedgerCapacity = 64;
    public readonly AttemptEntry[] Ledger = new AttemptEntry[LedgerCapacity];
    public int LedgerHead;              // 下一个写入位置
    public int LedgerCount;
    public int LedgerTotal;             // 历史总数（含被覆盖的）

    // ---------- 随机流（§5.2）----------
    public Rng Rng;

    // ---------- 统计 ----------
    public int DeathsWitnessed;
    public int BurnsSuffered;
    public float LastWarmTick = -1e9f;
    public bool HasEverFeltWarm;

    // ---------- 依恋 ----------
    public readonly Dictionary<int, float> Trust = new();

    public float DistanceTo(Vec2 p) => Vec2.Dist(Pos, p);

    public float GetTrust(int otherId) => Trust.TryGetValue(otherId, out var t) ? t : 0.5f;

    public bool KnowsAction(ActionType a) => KnownActions.Contains((int)a);

    public void LearnAction(ActionType a)
    {
        if (!KnownActions.Contains((int)a)) KnownActions.Add((int)a);
    }

    /// <summary>查询信念（instanceId = -1 表示类级泛化信念）。</summary>
    public bool TryGetBelief(KnowledgeId id, int instanceId, out Belief b)
    {
        for (int i = 0; i < Beliefs.Count; i++)
        {
            if (Beliefs[i].Id == id && Beliefs[i].InstanceId == instanceId) { b = Beliefs[i]; return true; }
        }
        b = default;
        return false;
    }

    public float ConfidenceOf(KnowledgeId id)
    {
        float best = 0f;
        for (int i = 0; i < Beliefs.Count; i++)
            if (Beliefs[i].Id == id && Beliefs[i].Confidence > best) best = Beliefs[i].Confidence;
        return best;
    }

    /// <summary>类级泛化信念（不含实例绑定的）。</summary>
    public float GeneralizedConfidenceOf(KnowledgeId id)
    {
        for (int i = 0; i < Beliefs.Count; i++)
            if (Beliefs[i].Id == id && Beliefs[i].InstanceId == -1) return Beliefs[i].Confidence;
        return 0f;
    }

    /// <summary>写入/更新一条信念，返回实际置信度增量。</summary>
    public float AddBelief(KnowledgeId id, int instanceId, float delta, BeliefSource src, int witnessId, int tick)
    {
        for (int i = 0; i < Beliefs.Count; i++)
        {
            if (Beliefs[i].Id == id && Beliefs[i].InstanceId == instanceId)
            {
                var b = Beliefs[i];
                float old = b.Confidence;
                b.Confidence = Math.Clamp(b.Confidence + delta, 0f, 1f);
                b.Source = src;
                b.WitnessId = witnessId;
                if (b.ReinforceCount < 255) b.ReinforceCount++;
                Beliefs[i] = b;
                return b.Confidence - old;
            }
        }
        if (Beliefs.Count >= 64) return 0f;   // 定长上限（§8.2）
        Beliefs.Add(new Belief
        {
            Id = id,
            InstanceId = instanceId,
            Confidence = Math.Clamp(delta, 0f, 1f),
            Source = src,
            WitnessId = witnessId,
            LearnTick = tick,
            ReinforceCount = 1,
        });
        return Math.Clamp(delta, 0f, 1f);
    }

    /// <summary>
    /// 每个实体的接触次数。用于"新颖度"评分——老扫描 64 条环形缓冲太慢，
    /// 这是每 tick 上千次的热路径，改成 O(1) 查表。
    /// </summary>
    private readonly Dictionary<int, int> _touched = new(16);

    public int TouchCount(int entityId) => _touched.TryGetValue(entityId, out var n) ? n : 0;

    /// <summary>写入一条尝试记录（环形缓冲，零分配）。</summary>
    public void PushAttempt(in AttemptEntry e)
    {
        Ledger[LedgerHead] = e;
        LedgerHead = (LedgerHead + 1) % LedgerCapacity;
        if (LedgerCount < LedgerCapacity) LedgerCount++;
        LedgerTotal++;

        int key = e.InstanceId;
        _touched[key] = (_touched.TryGetValue(key, out var n) ? n : 0) + 1;
    }

    /// <summary>按时间倒序读取第 i 条（i = 0 为最近一条）。</summary>
    public bool TryGetAttemptFromRecent(int i, out AttemptEntry e)
    {
        if (i < 0 || i >= LedgerCount) { e = default; return false; }
        int idx = (LedgerHead - 1 - i + LedgerCapacity * 2) % LedgerCapacity;
        e = Ledger[idx];
        return true;
    }
}
