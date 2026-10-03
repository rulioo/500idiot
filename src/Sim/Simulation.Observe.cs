using IdiotSim.Agents;
using IdiotSim.Core;
using IdiotSim.Knowledge;
using IdiotSim.Recording;

namespace IdiotSim;

public sealed partial class Simulation
{
    /// <summary>
    /// 一次"事迹"的目击传播（design.md §4.3 / §4.4）。
    ///
    /// 这是全部社会学习的唯一入口：亲眼做的（PersonalExperience）由动作结算自己写，
    /// 而**看别人做的**全部走这里。清晰度决定学到什么：
    ///   clarity >= ClarityResolveThreshold  → 看清细节（是哪个孔、往哪个方向转）
    ///   clarity >= ClarityThreshold         → 能形成信念，但细节靠猜（§2.4 参照系误判）
    ///   clarity &lt;  ClarityThreshold         → 只留下模糊记忆，学不到东西
    ///
    /// 返回目击者人数（写入尝试记录用）。
    /// </summary>
    private int BroadcastDeed(Agent actor, ActionType act, int targetEntityId, OutcomeType outcome,
                              float salience, float dVitality, float dPain, float dComfort,
                              float actionSeconds)
    {
        // 高显著度的事件（死亡、起火、烫伤）自带"喊声"——传得更远
        float range = Cfg.SightRadius * (1f + salience * 0.6f);
        var cand = _grid.Query(actor.Pos, range);

        int witnesses = 0;
        for (int k = 0; k < cand.Count; k++)
        {
            int ai = cand[k];
            if (ai < 0 || ai >= Agents.Count) continue;
            var o = Agents[ai];
            if (!o.Alive || o.Id == actor.Id) continue;

            float d = Vec2.Dist(o.Pos, actor.Pos);
            if (d > range) continue;

            float clarity = ComputeClarity(o, actor.Id, actor.Pos, d, salience, range);
            if (clarity < Cfg.ClarityThreshold)
            {
                // 只写记忆：知道"那边发生了点什么"，但不知道是什么
                PushObservation(o, actor.Id, act, targetEntityId, outcome, clarity);
                continue;
            }

            witnesses++;
            PushObservation(o, actor.Id, act, targetEntityId, outcome, clarity);
            GrabAttention(o, actor.Id, act, targetEntityId, clarity, actionSeconds);
            TeachFromObservation(o, actor, act, targetEntityId, outcome, clarity);
        }
        return witnesses;
    }

    /// <summary>清晰度 = 显著度 × 距离衰减 × 视角权重 × 注意力聚焦（§3.2）。</summary>
    private float ComputeClarity(Agent o, int actorId, Vec2 at, float dist, float salience, float range)
    {
        float df = 1f - dist / MathF.Max(range, 1e-3f);
        if (df < 0f) df = 0f;
        df *= df;                                     // 平方衰减

        var to = at - o.Pos;
        float ang = 1f;
        if (to.LengthSq > 1e-5f)
        {
            float cos = Vec2.Dot(o.Facing, to.Normalized);
            // 身后也能感觉到（骚动），但看不清
            ang = cos > 0.35f ? 1f : (cos > -0.2f ? 0.55f : 0.25f);
        }

        // 已经在盯着这个人看的人，看得更清楚
        float att = (o.AttentionIsAgent && o.AttentionTarget == actorId) ? 1.4f : 1f;

        return Math.Clamp(salience * df * ang * att, 0f, 1f);
    }

    private void PushObservation(Agent o, int actorId, ActionType act, int targetEntityId,
                                 OutcomeType outcome, float clarity)
    {
        var obs = new Observation
        {
            Tick = Tick,
            ActorId = actorId,
            Action = act,
            TargetEntityId = targetEntityId,
            Outcome = outcome,
            Clarity = clarity,
        };
        if (o.RecentObs.Count >= 8) o.RecentObs.RemoveAt(0);
        o.RecentObs.Add(obs);
    }

    private void GrabAttention(Agent o, int actorId, ActionType act, int targetEntityId,
                               float clarity, float actionSeconds)
    {
        if (clarity < 0.5f) return;
        int until = Tick + Cfg.SecondsToTicks(Math.Max(3f, actionSeconds * clarity));
        if (until <= o.AttentionUntilTick) return;

        o.AttentionTarget = act == ActionType.Cry ? actorId : targetEntityId;
        o.AttentionIsAgent = act == ActionType.Cry;
        o.AttentionUntilTick = until;
        o.AttentionStrength = clarity;

        if (act == ActionType.Cry)
        {
            // 哭声会传染（§3.5）
            o.Fear = Math.Min(1f, o.Fear + 0.15f * clarity * (1f - o.PFearThreshold));
        }
    }

    /// <summary>从观察中学习（design.md §4.3）。清晰度不足时细节靠猜——这就是 §2.4 的参照系误判。</summary>
    private void TeachFromObservation(Agent o, Agent actor, ActionType act, int targetEntityId,
                                      OutcomeType outcome, float clarity)
    {
        // ---- 先学"动作"本身：模仿解锁动作候选集（"没有说明书"的技术实现）----
        if (clarity >= Cfg.ClarityThreshold && !o.KnowsAction(act))
        {
            if (o.Rng.Chance(o.PImitation * clarity * 0.8f))
            {
                o.LearnAction(act);
                Emit((ushort)o.Id, EventType.BeliefGain, o.Pos, (int)act, actor.Id, clarity);
            }
        }

        // ---- 再学"关于世界的事实"----
        int inst = targetEntityId;
        switch (act)
        {
            case ActionType.InsertNail:
            {
                if (outcome == OutcomeType.Death)
                {
                    // 看清了 = 知道哪个孔致命；没看清 = 只知道"铁钉会死"
                    if (clarity >= Cfg.ClarityResolveThreshold)
                    {
                        // 目击者站在自己的位置上——他看到的"左/右"未必是死者的"左/右"（§2.4）
                        bool flipped = o.Rng.Chance(MirrorFlipProbability(clarity));
                        Learn(o, flipped ? KnowledgeId.HoleLeftSafe : KnowledgeId.HoleRightDeadly,
                              -1, Cfg.ConfObserveDeath, actor.Id, clarity);
                        Learn(o, KnowledgeId.NailDangerous, -1, Cfg.ConfObserveDeath * 0.8f, actor.Id, clarity);
                    }
                    else
                    {
                        Learn(o, KnowledgeId.NailDangerous, -1, Cfg.ConfObserveDeath, actor.Id, clarity);
                    }
                }
                else if (outcome == OutcomeType.Success)
                {
                    if (clarity >= Cfg.ClarityResolveThreshold)
                    {
                        bool flipped = o.Rng.Chance(MirrorFlipProbability(clarity));
                        Learn(o, flipped ? KnowledgeId.HoleRightDeadly : KnowledgeId.HoleLeftSafe,
                              -1, Cfg.ConfObserveSuccess, actor.Id, clarity);
                    }
                }
                break;
            }

            case ActionType.TouchProp:
            case ActionType.ContactHeater:
            case ActionType.TurnKnob:
            {
                if (outcome == OutcomeType.Injury)
                {
                    Learn(o, KnowledgeId.HeaterHot, -1, Cfg.ConfObserveBurn * clarity, actor.Id, clarity);
                    o.DeathsWitnessed += 0;
                    o.Fear = Math.Min(1f, o.Fear + 0.25f * clarity * (1f - o.PFearThreshold));
                }
                else if (outcome == OutcomeType.Warm)
                {
                    Learn(o, KnowledgeId.HeaterWarm, -1, Cfg.ConfObserveSuccess * clarity, actor.Id, clarity);
                    if (clarity >= Cfg.ClarityResolveThreshold)
                        Learn(o, KnowledgeId.HeaterKnobExists, -1, Cfg.ConfObserveSuccess, actor.Id, clarity);
                }
                else if (outcome == OutcomeType.NoEffect && act == ActionType.ContactHeater)
                {
                    Learn(o, KnowledgeId.HeaterWarm, -1, Cfg.ConfObserveSuccess * 0.3f * clarity, actor.Id, clarity);
                }
                break;
            }

            case ActionType.PushObject:
                Learn(o, KnowledgeId.HeaterPush, -1, Cfg.ConfImitate * 4f * clarity, actor.Id, clarity);
                break;

            case ActionType.PlugIn:
                Learn(o, KnowledgeId.HeaterPlugFits, -1, Cfg.ConfImitate * 4f * clarity, actor.Id, clarity);
                break;

            case ActionType.Drill:
                Learn(o, KnowledgeId.DrillingMotion, -1, Cfg.ConfImitate * 3f * clarity, actor.Id, clarity);
                Learn(o, KnowledgeId.StickPlankTogether, -1, Cfg.ConfImitate * 2f * clarity, actor.Id, clarity);
                if (outcome == OutcomeType.Warm)
                    Learn(o, KnowledgeId.FireWarm, -1, Cfg.ConfObserveSuccess * 2f * clarity, actor.Id, clarity);
                break;

            case ActionType.Idle:   // 死亡广播
                break;
        }

        // ---- 死亡本身是最强的知识来源（§4.4）----
        if (outcome == OutcomeType.Death && actor.Death == DeathCause.Drown)
            Learn(o, KnowledgeId.WaterDanger, -1, Cfg.ConfObserveDeath * clarity, actor.Id, clarity);
        if (outcome == OutcomeType.Death && actor.Death == DeathCause.Shock)
            Learn(o, KnowledgeId.NailDangerous, -1, Cfg.ConfObserveDeath * 1.5f * clarity, actor.Id, clarity);

        if (outcome == OutcomeType.Death)
        {
            o.DeathsWitnessed++;
            o.Fear = Math.Min(1f, o.Fear + 0.4f * clarity * (1f - o.PFearThreshold));
            // 信任：一起看见死亡的人会更抱团（§3.5）
            o.Trust[actor.Id] = Math.Max(0f, o.GetTrust(actor.Id) - 0.15f * clarity);
        }
    }

    /// <summary>
    /// 中等清晰度下的左右误判率（design.md §2.4）。
    /// 白痴能看出"有人把东西插进插座"，但在 15 米外看不清是哪个孔——
    /// 距离越远、角度越偏，猜错的概率越接近纯随机 0.5。
    /// </summary>
    public float MirrorFlipProbability(float clarity)
    {
        if (clarity >= Cfg.ClarityResolveThreshold) return 0.02f;
        float t = (clarity - Cfg.ClarityThreshold) / MathF.Max(1e-4f, Cfg.ClarityResolveThreshold - Cfg.ClarityThreshold);
        t = Math.Clamp(t, 0f, 1f);
        return Cfg.MirrorProbMidRange + (0.5f - Cfg.MirrorProbMidRange) * (1f - t);
    }

    private void Learn(Agent o, KnowledgeId id, int inst, float baseDelta, int witnessId, float clarity)
    {
        if (id == KnowledgeId.None || baseDelta <= 0f) return;
        float before = o.ConfidenceOf(id);
        float actual = o.AddBelief(id, inst, baseDelta, BeliefSource.Observed, witnessId, Tick);
        if (actual > 0.001f)
        {
            Emit((ushort)o.Id, before <= 0f ? EventType.BeliefGain : EventType.BeliefUpdate,
                 o.Pos, (int)id, witnessId, o.ConfidenceOf(id), clarity);
        }
    }

    // =====================================================================
    //  记忆衰减与泛化（design.md §4.2）
    // =====================================================================
    /// <summary>
    /// 每 300 游戏秒：所有信念 ×DecayFactor；低于 ForgetBelow 的删除。
    /// 同一实例绑定信念重复出现 >= GeneralizeInstances 次 → 升格为类级知识。
    /// </summary>
    private void DecayAndGeneralize(Agent a)
    {
        for (int i = a.Beliefs.Count - 1; i >= 0; i--)
        {
            var b = a.Beliefs[i];
            b.Confidence *= Cfg.DecayFactor;
            if (b.Confidence < Cfg.ForgetBelow)
            {
                a.Beliefs.RemoveAt(i);
                Emit((ushort)a.Id, EventType.BeliefForget, a.Pos, (int)b.Id, b.InstanceId, 0f);
                continue;
            }
            a.Beliefs[i] = b;
        }

        // ---- 泛化：经历过 3 个不同实例 → 相信这是一类东西的规律 ----
        for (int k = 0; k < KnowledgeCatalog.Length; k++)
        {
            var id = KnowledgeCatalog[k];
            if (a.GeneralizedConfidenceOf(id) > 0f) continue;

            int instances = 0;
            float sum = 0f;
            for (int i = 0; i < a.Beliefs.Count; i++)
            {
                if (a.Beliefs[i].Id == id && a.Beliefs[i].InstanceId >= 0)
                {
                    instances++;
                    sum += a.Beliefs[i].Confidence;
                }
            }
            if (instances < Cfg.GeneralizeInstances) continue;

            float conf = Math.Min(0.55f, sum / instances);
            a.AddBelief(id, -1, conf, BeliefSource.PersonalExperience, 0, Tick);
            Emit((ushort)a.Id, EventType.BeliefGeneralize, a.Pos, (int)id, instances, conf);
        }
    }

    /// <summary>可泛化的知识原子（§4.1）。</summary>
    public static readonly KnowledgeId[] KnowledgeCatalog =
    {
        KnowledgeId.HeaterPush,
        KnowledgeId.HeaterPlugFits,
        KnowledgeId.HeaterKnobExists,
        KnowledgeId.HeaterWarm,
        KnowledgeId.HeaterHot,
        KnowledgeId.StickPlankTogether,
        KnowledgeId.DrillingMotion,
        KnowledgeId.FireWarm,
        KnowledgeId.HoleLeftSafe,
        KnowledgeId.HoleRightDeadly,
        KnowledgeId.NailDangerous,
        KnowledgeId.WaterDanger,
    };
}
