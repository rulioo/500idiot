using IdiotSim.Agents;
using IdiotSim.Core;
using IdiotSim.Knowledge;
using IdiotSim.Recording;
using IdiotSim.World;

namespace IdiotSim;

public sealed partial class Simulation
{
    // 感知暂存（单线程复用，零分配）
    private readonly List<int> _seenEnt = new(96);
    private readonly List<float> _seenDist = new(96);
    private readonly List<float> _seenNov = new(96);
    private readonly List<int> _seenAgt = new(64);
    private readonly List<int> _seenCorpse = new(16);

    private const int MaxCandidates = 28;
    private struct Cand
    {
        public ActionType Act;
        public int Target;
        public bool TargetIsAgent;
        public int Arg;
        public float Score;
    }
    private readonly Cand[] _cands = new Cand[MaxCandidates];
    private int _candN;

    private bool _entGridDirty = true;

    /// <summary>实体网格（实体几乎不动，仅在推动/丢弃/投掷时重建）。</summary>
    private void EnsureEntityGrid()
    {
        if (!_entGridDirty) return;
        _entityGrid.Clear();
        for (int i = 0; i < Entities.Count; i++) _entityGrid.Insert(i, Entities[i].Pos);
        _entGridDirty = false;
    }

    public void MarkEntityMoved() => _entGridDirty = true;

    // =====================================================================
    //  感知（design.md §3.2）
    // =====================================================================
    private void Perceive(Agent a)
    {
        EnsureEntityGrid();
        _seenEnt.Clear();
        _seenDist.Clear();
        _seenNov.Clear();
        _seenAgt.Clear();
        _seenCorpse.Clear();

        float cosHalf = MathF.Cos(Cfg.SightHalfAngleDeg * MathF.PI / 180f);
        float r2 = Cfg.SightRadius * Cfg.SightRadius;

        var cand = _entityGrid.Query(a.Pos, Cfg.SightRadius, 240);
        for (int k = 0; k < cand.Count; k++)
        {
            var e = Entities[cand[k]];
            if (e.Held || e.InPool) continue;
            if (e.Type == EntityType.Fire && !FireBurning) continue;
            float d2 = Vec2.DistSq(a.Pos, e.Pos);
            if (d2 > r2) continue;
            var dir = (e.Pos - a.Pos).Normalized;
            // 插座密集成墙，用更宽的感知角（余光可见）
            float c = e.Type == EntityType.Socket ? 0.35f : cosHalf;
            if (Vec2.Dot(a.Facing, dir) < c) continue;
            _seenEnt.Add(e.Id);
            // 距离与新颖度在这里算一次，后面四五个候选循环直接复用
            _seenDist.Add(MathF.Sqrt(d2));
            _seenNov.Add(Novelty(a, e));
            if (_seenEnt.Count > 200) break;
        }

        // 同伴的感知半径比"看东西"小 —— 你注意得到近处的人，看不清远处的人（§3.2）
        float sr = Cfg.SocialRadius;
        float sr2 = sr * sr;
        var near = _grid.Query(a.Pos, sr, 110);
        for (int k = 0; k < near.Count; k++)
        {
            int idx = near[k];
            if (idx < 0 || idx >= Agents.Count || idx == a.Id - 1) continue;
            var b = Agents[idx];
            if (Vec2.DistSq(a.Pos, b.Pos) > sr2) continue;
            if (!b.Alive) { _seenCorpse.Add(b.Id); continue; }
            _seenAgt.Add(b.Id);
        }
        if (_seenAgt.Count > 120) _seenAgt.RemoveRange(120, _seenAgt.Count - 120);

        // --- 注意力：被抢占过就维持；否则选最显著的一个 ---
        if (Tick > a.AttentionUntilTick || a.AttentionTarget < 0)
        {
            float best = 0f;
            int bestId = -1;
            bool bestIsAgent = false;

            for (int i = 0; i < _seenEnt.Count; i++)
            {
                var e = EntityById[_seenEnt[i]];
                float s = Salience(e);
                if (s > best) { best = s; bestId = e.Id; bestIsAgent = false; }
            }
            for (int i = 0; i < _seenCorpse.Count; i++)
            {
                if (0.5f > best) { best = 0.5f; bestId = _seenCorpse[i]; bestIsAgent = true; }
            }
            if (bestId >= 0)
            {
                a.AttentionTarget = bestId;
                a.AttentionIsAgent = bestIsAgent;
                a.AttentionStrength = best;
                a.AttentionUntilTick = Tick + Cfg.SecondsToTicks(a.PAttentionSpan);
            }
        }
    }

    private float Salience(Entity e) => e.Type switch
    {
        EntityType.Fire => 0.95f,
        EntityType.Heater => e.Running ? 0.85f : 0.35f,
        EntityType.Nail => 0.30f,
        EntityType.Plank => 0.22f,
        EntityType.Stick => 0.22f,
        EntityType.Weed => 0.15f,
        _ => 0.12f,
    };

    // =====================================================================
    //  决策（design.md §3.4）—— 效用函数，**无规划器**
    // =====================================================================
    private void Think(Agent a)
    {
        // ---- 情绪（§3.3）----
        a.Fear = Math.Max(0f, a.Fear - Cfg.TickSeconds * Cfg.BrainEveryTicks / 120f);
        float safety = Math.Min(1f, a.Fear);
        a.Safety = safety;

        if (a.Pain > Cfg.PainOverride)
            a.Emo = Emotion.Hurt;
        else if (a.Fear > a.PFearThreshold)
            a.Emo = a.Fear > 0.85f ? Emotion.Panic : Emotion.Fear;
        else if (a.Comfort <= 0.35f)
            a.Emo = Emotion.Seeking;
        else if (a.Comfort >= 1f && a.HasEverFeltWarm)
            a.Emo = Emotion.Comfort;
        else
            a.Emo = Emotion.Calm;

        // ---- 情绪接管（恐惧与疼痛压倒一切，§3.1）----
        if (a.Emo == Emotion.Hurt || a.Emo == Emotion.Fear || a.Emo == Emotion.Panic)
        {
            if (a.Pain > Cfg.PainOverride && a.PainSourceId >= 0)
            {
                var src = a.PainSourceId < EntityById.Length ? EntityById[a.PainSourceId] : null;
                a.FleeFrom = src != null ? src.Pos : a.Pos;
            }
            a.Action = ActionType.Flee;
            a.TargetId = -1;
            a.TargetIsAgent = false;
            a.ActionTimer = a.Emo == Emotion.Panic ? 6f : 4f;
            a.HasMoveTarget = false;
            a.RepeatLeft = 0;
            // 逃跑方向：远离恐惧源
            var away = (a.Pos - a.FleeFrom);
            if (away.LengthSq < 1e-4f) away = a.Rng.NextDir();
            a.MoveTarget = a.Pos + away.Normalized * 12f;
            ClampToRoom(ref a.MoveTarget);
            a.HasMoveTarget = true;
            if (a.Pain > Cfg.PainOverride) DoCry(a);
            return;
        }

        // ---- 执念：保持上一个动作（§3.1）----
        if (a.RepeatLeft > 0 && a.Action != ActionType.Idle && a.Action != ActionType.Wander
            && IsActionStillValid(a, a.Action, a.TargetId, a.TargetIsAgent))
        {
            a.RepeatLeft--;
            return;
        }

        // ---- 赶路承诺：已经在往目标走，就别在半路上改主意 ----
        // 实测：13314 次进入"搓火"，13254 次卡在"还太远"——不是走不过去，
        // 是每 4 tick 重新想一次，走到一半就忘了自己要干嘛。全程只有 60 次
        // 真的搓到了。这不是"傻得可爱"，是决策循环里少了一个承诺机制。
        if (a.Action == ActionType.Drill && a.HasMoveTarget)
        {
            if (Tick >= a.CommitUntilTick) DiagDrillCommitTimeout++;
            else if (!IsActionStillValid(a, a.Action, a.TargetId, a.TargetIsAgent)) DiagDrillCommitInvalid++;
            else DiagDrillCommitKept++;
        }
        if (a.HasMoveTarget && Tick < a.CommitUntilTick
            && IsActionStillValid(a, a.Action, a.TargetId, a.TargetIsAgent))
            return;

        // ---- 构造候选并打分 ----
        BuildCandidates(a);
        int n = _candN;
        if (n == 0) { a.Action = ActionType.Wander; PickWanderTarget(a); return; }

        int best = 0;
        for (int i = 1; i < n; i++) if (_cands[i].Score > _cands[best].Score) best = i;

        var c = _cands[best];
        if (c.Act == ActionType.Drill) DiagDrillChosen++;

        // 计时器归属于 (动作,目标) 这一对，不是"当前动作"。执念 RepeatLeft≈2 个大脑 tick
        // 就会重新决策一次，中间只要插进来任何一个别的动作，计时器就会被重置——
        // 结果是所有时长 >3 tick 的动作（搓火 5、插插头 3、推电暖气 3）永远做不完。
        bool sameAction = a.TimerAct == c.Act && a.TimerTarget == c.Target;

        a.Action = c.Act;
        a.TargetId = c.Target;
        a.TargetIsAgent = c.TargetIsAgent;
        a.TargetArg = c.Arg;
        if (!sameAction)
        {
            a.ActionTimer = ActionDuration(c.Act);
            a.TimerAct = c.Act;
            a.TimerTarget = c.Target;
        }
        a.HasMoveTarget = sameAction ? a.HasMoveTarget : false;
        a.RepeatLeft = (int)MathF.Round(a.PPerseveration * 5f);

        // 给这一趟路留够时间（1.5 倍余量 + 3 秒缓冲），走不到就重新想。
        // 没有这个上限，被人群卡住的人会永远"承诺"下去。
        if (!c.TargetIsAgent && c.Target > 0 && c.Target < EntityById.Length && EntityById[c.Target] != null)
        {
            float walk = Vec2.Dist(a.Pos, EntityById[c.Target].Pos) / Cfg.WalkSpeed;
            a.CommitUntilTick = Tick + Cfg.SecondsToTicks(3f + walk * 1.5f);
        }
        else if (c.TargetIsAgent && c.Target > 0 && c.Target <= Agents.Count)
        {
            float walk = Vec2.Dist(a.Pos, Agents[c.Target - 1].Pos) / Cfg.WalkSpeed;
            a.CommitUntilTick = Tick + Cfg.SecondsToTicks(3f + walk * 1.5f);
        }
        else a.CommitUntilTick = 0;
    }

    private float ActionDuration(ActionType t) => t switch
    {
        ActionType.Grab => 1f,
        ActionType.DropHeld => 0.5f,
        ActionType.Throw => 0.5f,
        ActionType.TouchProp => 0.5f,
        ActionType.InsertNail => 2f,
        ActionType.ContactHeater => 0.5f,
        ActionType.PlugIn => 3f,
        ActionType.TurnKnob => 1f,
        ActionType.PushObject => 3f,
        // "搓"是**短促反复**的动作，不是一搓五秒。做成 5 tick 的长动作，
        // 3 岁小孩的注意力根本撑不到结算——实测 209 次选中、0 次搓成。
        // 改成每 tick 一次，进度按 DrillSecondsPerAction 累加。
        ActionType.Drill => 1f,
        ActionType.PushAgent => 0.5f,
        ActionType.Cry => 2f,
        _ => 1f,
    };

    private void BuildCandidates(Agent a)
    {
        int n = 0;
        float noise = a.PNoiseSigma * NoiseMultFromCoreTemp(a.CoreTemp);
        float warmthNeed = Math.Clamp(a.Warmth, 0f, 1f);

        // 知识查询扫的是 List<Belief>，在候选构造里会被问到几十次。
        // 一次性取出来，热路径上就只剩浮点乘加。
        float kHot = a.ConfidenceOf(KnowledgeId.HeaterHot);
        float kWarm = a.ConfidenceOf(KnowledgeId.HeaterWarm);
        float kPush = a.ConfidenceOf(KnowledgeId.HeaterPush);
        float kPlug = a.ConfidenceOf(KnowledgeId.HeaterPlugFits);
        float kKnob = a.ConfidenceOf(KnowledgeId.HeaterKnobExists);
        float kDrill = a.ConfidenceOf(KnowledgeId.DrillingMotion);
        float kLeftSafe = a.ConfidenceOf(KnowledgeId.HoleLeftSafe);
        float kRightDead = a.ConfidenceOf(KnowledgeId.HoleRightDeadly);
        float kNailDanger = a.ConfidenceOf(KnowledgeId.NailDangerous);
        float heatPain = Math.Max(0f, kHot - a.PPainTolerance * 0.85f);
        // 烫伤的记忆会褪色，疤不会。
        // 只用 kHot 的话是这样一个循环：烫一次 → 躲开 → 信念按 0.97/300 游戏秒衰减 →
        // 三小时后掉到 0.3 以下 → 惩罚项归零 → 再摸一次。好奇不衰减，疼痛衰减，
        // 于是他们被反复烫到死：实测 3 小时 2837 次烫伤，350 人就这么慢慢烫没了。
        // 所以再叠一层"被烫过几次"——它只增不减，代表真正记住的那部分。
        heatPain = MathF.Max(heatPain, MathF.Min(1f, a.BurnsSuffered * 0.4f));

        // 注意：必须直接操作 a.Rng 这个字段。写成 `var rng = a.Rng` 会拷一份结构体，
        // 随机数流原地打转——所有人的噪声序列会完全一样。
        void Add(ActionType act, int target, bool isAgent, int arg, float score)
        {
            if (n >= MaxCandidates) return;
            _cands[n].Act = act;
            _cands[n].Target = target;
            _cands[n].TargetIsAgent = isAgent;
            _cands[n].Arg = arg;
            _cands[n].Score = score + (a.Rng.NextGaussian() * noise);
            n++;
        }

        // --- 永远可用的兜底 ---
        Add(ActionType.Wander, -1, false, 0, Cfg.W_Curio * (0.15f + a.PCuriosity * 0.35f));
        if (a.Comfort >= 0.9f) Add(ActionType.Rest, -1, false, 0, 0.35f);

        // --- 走向热源（取暖需求）---
        float bestHeatScore = 0f;
        int bestHeater = -1;
        for (int i = 0; i < _seenEnt.Count; i++)
        {
            var e = EntityById[_seenEnt[i]];
            if (e.Type != EntityType.Heater || !e.Running) continue;
            float sc = Cfg.W_Warm * warmthNeed * 1.4f - Cfg.W_Dist * _seenDist[i];
            if (sc > bestHeatScore) { bestHeatScore = sc; bestHeater = e.Id; }
        }
        if (bestHeater >= 0)
        {
            float sc = bestHeatScore + Cfg.W_Fam * kWarm * 0.6f;
            Add(ActionType.Approach, bestHeater, false, 0, sc);
        }

        // --- 走向火堆。火是最显眼、最暖和的东西，没有这一条，火就只是背景装饰 ---
        if (FireBurning && FireEntityId >= 0)
        {
            var fire = EntityById[FireEntityId];
            float d = Vec2.Dist(a.Pos, fire.Pos);
            if (d < Cfg.SightRadius)
            {
                float sc = Cfg.W_Warm * warmthNeed * 1.6f + Cfg.W_Fam * a.ConfidenceOf(KnowledgeId.FireWarm) * 0.5f
                         - Cfg.W_Dist * d;
                Add(ActionType.Approach, fire.Id, false, 0, sc);
            }
        }

        // --- 手上拿着东西 ---
        if (a.HeldId >= 0)
        {
            var held = EntityById[a.HeldId];

            if (held.Type == EntityType.Nail && a.KnowsAction(ActionType.InsertNail))
            {
                int sock = NearestSocket(a, Cfg.SightRadius);
                if (sock >= 0)
                {
                    // 早先 danger = kRightDead + kNailDanger*0.5，再减掉 PBoldness*0.9，
                    // 中等胆子的人算出来是负数——惩罚项完全不生效，4 小时里 180 个人
                    // 把自己插死了。"知道铁钉危险"必须真的能拦住人。
                    float danger = kRightDead * 1.4f + kNailDanger * 1.25f;
                    float benefit = kLeftSafe * 0.4f;
                    float sc = Cfg.W_Curio * (0.5f + a.PCuriosity)
                             + Cfg.W_Fam * benefit
                             - Cfg.W_Safe * Math.Max(0f, danger - a.PBoldness * 0.55f) * 2.4f
                             - Cfg.W_Dist * Vec2.Dist(a.Pos, EntityById[sock].Pos);
                    Add(ActionType.InsertNail, sock, false, 0, sc);
                }
            }

            if (held.Type == EntityType.Stick) DiagHeldStick++;
            if (held.Type == EntityType.Stick && a.KnowsAction(ActionType.Drill))
            {
                DiagHeldStickKnowsDrill++;
                // 目标选择**不设距离上限**。先写 6 米（要正好站在木板旁），
                // 后改 25 米（视野）——两次都是把距离当成硬门槛，可全屋只有
                // 10 根木棍 10 块木板，攥着棍子的人 76% 的时候视野里根本没有木板。
                // 距离本来就该由评分里的 -W_Dist*d 来表达：站在 30 米外自然不想去，
                // 走到 8 米内它就赢了。硬门槛只会把中间的整个过程删掉。
                int plank = NearestOfType(a, EntityType.Plank, float.MaxValue);
                if (plank >= 0)
                {
                    DiagDrillCand++;
                    // 熟悉度封顶 0.4：不然"会搓"这件事会自我强化到压过一切，
                    // 实测有人一辈子蹲在墙角搓了 11150 游戏秒。
                    float fam = Math.Min(kDrill, 0.4f);
                    float sc = Cfg.W_Curio * (0.6f + a.PCuriosity * 0.8f)
                             + Cfg.W_Fam * fam
                             - Cfg.W_Dist * Vec2.Dist(a.Pos, EntityById[plank].Pos);
                    // 屋里已经有火了，再搓一堆的意义就小多了
                    if (a.ConfidenceOf(KnowledgeId.FireWarm) > 0.15f) sc *= 0.35f;
                    Add(ActionType.Drill, plank, false, 0, sc);
                }
            }

            Add(ActionType.DropHeld, -1, false, 0, 0.10f);
            if (a.KnowsAction(ActionType.Throw)) Add(ActionType.Throw, -1, false, 0, Cfg.W_Curio * 0.25f);
        }
        else if (a.KnowsAction(ActionType.Drill) && a.ConfidenceOf(KnowledgeId.StickPlankTogether) > 0.05f)
        {
            // 学会了"搓"但手上没棍子——全屋只有 10 根木棍，谁先想到去拿谁才有火。
            // 没有这一条，"知道怎么搓"和"手上有棍"这两个条件几乎永远碰不上（实测 0 次）。
            int stick = NearestOfType(a, EntityType.Stick, float.MaxValue);
            if (stick >= 0)
            {
                float d = Vec2.Dist(a.Pos, EntityById[stick].Pos);
                float sc = Cfg.W_Curio * (0.55f + a.PCuriosity * 0.6f)
                         + Cfg.W_Fam * kDrill * 1.1f
                         + Cfg.W_Warm * warmthNeed * 0.6f
                         - Cfg.W_Dist * d;
                Add(ActionType.Grab, stick, false, 0, sc);
            }
        }
        else
        {
            // --- 空手：捡东西 ---
            int bestProp = -1; float bestPropScore = 0f;
            for (int i = 0; i < _seenEnt.Count; i++)
            {
                var e = EntityById[_seenEnt[i]];
                if (!e.IsProp) continue;
                float d = _seenDist[i];
                if (d > 20f) continue;
                float sc = Cfg.W_Curio * (0.35f + a.PCuriosity * 0.5f) - Cfg.W_Dist * d;
                if (sc > bestPropScore) { bestPropScore = sc; bestProp = e.Id; }
            }
            if (bestProp >= 0) Add(ActionType.Grab, bestProp, false, 0, bestPropScore);
        }

        // --- 电暖气相关（全部需要模仿解锁）---
        for (int i = 0; i < _seenEnt.Count; i++)
        {
            var e = EntityById[_seenEnt[i]];
            if (e.Type != EntityType.Heater || e.Broken) continue;
            float d = _seenDist[i];

            if (a.KnowsAction(ActionType.ContactHeater))
            {
                // 触摸：好奇心驱动，但被烫伤知识强烈抑制
                float sc = Cfg.W_Curio * (0.4f + a.PCuriosity * 0.7f)
                         - Cfg.W_Safe * heatPain * 2.6f
                         - Cfg.W_Dist * d;
                if (e.Running) sc -= 0.15f;   // 已经在发光的东西看起来更"烫"
                Add(ActionType.ContactHeater, e.Id, false, 0, sc);
            }

            if (!e.Plugged && a.KnowsAction(ActionType.PlugIn))
            {
                int sock = NearestEmptySocketTo(e.Pos, Cfg.HeaterPowerCordLength);
                if (sock >= 0)
                {
                    float sc = Cfg.W_Curio * (0.5f + a.PCuriosity * 0.6f)
                             + Cfg.W_Fam * kPlug * 0.8f
                             + Cfg.W_Warm * warmthNeed * 0.9f
                             - Cfg.W_Dist * (d + Vec2.Dist(e.Pos, EntityById[sock].Pos));
                    Add(ActionType.PlugIn, e.Id, false, 0, sc);
                }
            }

            if (e.Plugged && !e.Running && a.KnowsAction(ActionType.TurnKnob))
            {
                float sc = Cfg.W_Curio * (0.6f + a.PCuriosity * 0.7f)
                         + Cfg.W_Fam * kKnob * 0.9f
                         + Cfg.W_Warm * warmthNeed * 0.7f
                         - Cfg.W_Dist * d;
                Add(ActionType.TurnKnob, e.Id, false, 0, sc);
            }

            if (!e.Plugged && a.KnowsAction(ActionType.PushObject))
            {
                int sock = NearestSocketAnywhere(e.Pos, 12f);
                if (sock >= 0)
                {
                    var sp = EntityById[sock].Pos;
                    float curD = Vec2.Dist(e.Pos, sp);
                    // 推到电源线够得着为止（留 0.4m 余量，因为一次推动就是 0.4m）
                    if (curD > Cfg.HeaterPowerCordLength - 0.4f)
                    {
                        float sc = Cfg.W_Curio * (0.35f + a.PCuriosity * 0.5f)
                                 + Cfg.W_Fam * kPush * 1.0f
                                 + Cfg.W_Warm * warmthNeed * 0.6f
                                 - Cfg.W_Dist * d;
                        Add(ActionType.PushObject, e.Id, false, sock, sc);
                    }
                }
            }

            // 站到正在运转的电暖气旁边（取暖，不是交互）
            if (e.Running && kWarm > 0.3f && bestHeater != e.Id)
            {
                float sc = Cfg.W_Warm * warmthNeed * 1.1f - Cfg.W_Dist * d;
                Add(ActionType.Approach, e.Id, false, 0, sc);
            }
        }

        // --- 试探性触摸最近的未知实体（好奇心的默认出口，也是"发明"的引擎）---
        {
            int best = -1; float bestSc = 0f;
            for (int i = 0; i < _seenEnt.Count; i++)
            {
                var e = EntityById[_seenEnt[i]];
                if (e.Held) continue;
                float d = _seenDist[i];
                if (d > 18f) continue;
                float sc = Cfg.W_Curio * _seenNov[i] * (0.5f + a.PCuriosity) - Cfg.W_Dist * d;
                if (e.Type == EntityType.Heater) sc -= Cfg.W_Safe * heatPain * 1.8f;
                // 插座也得有危险项。钉子插进火线孔之后，那个插座看上去和别的插座
                // 一模一样（2 毫米的钉子肉眼根本分辨不出），只能靠"听说铁钉会电死人"
                // 这条知识把好奇压下去——原先这里只对电暖气扣分，4 小时里 167 个人
                // 就这么一个接一个伸手摸上去。
                // 系数给到 5：哪怕只是"听说隔壁有人摸插座死了"这种 0.2 的模糊信念，
                // 也足以让 3 岁小孩把手缩回去。要求信念很坚定才生效是不对的——
                // 恐惧不需要证据，谣言就够了。
                if (e.Type == EntityType.Socket)
                    sc -= Cfg.W_Safe * Math.Min(1f, kNailDanger * 5f) * 2.2f * (1f - a.PBoldness * 0.6f);
                if (e.Type == EntityType.Fire) sc += Cfg.W_Warm * warmthNeed * 0.5f;
                if (sc > bestSc) { bestSc = sc; best = e.Id; }
            }
            if (best >= 0) Add(ActionType.TouchProp, best, false, 0, bestSc);
        }

        // --- 模仿（知识传播的引擎，§3.4）---
        {
            float pull = 0f;
            int srcId = -1;
            ActionType srcAct = ActionType.Idle;
            for (int i = 0; i < _seenAgt.Count; i++)
            {
                var b = Agents[_seenAgt[i] - 1];
                if (b.Action == ActionType.Idle || b.Action == ActionType.Wander || b.Action == ActionType.Rest) continue;
                if (a.KnowsAction(b.Action)) { /* 已会，模仿欲低 */ }
                float attn = (a.AttentionIsAgent && a.AttentionTarget == b.Id) ? 1f : 0.25f;
                float cred = a.GetTrust(b.Id);
                float nov = a.KnowsAction(b.Action) ? 0.3f : 1f;
                float p = attn * cred * nov * a.PImitation;
                if (p > pull) { pull = p; srcId = b.Id; srcAct = b.Action; }
            }
            // 跟风加成：>=3 人做同一动作（§4.5）
            if (srcId >= 0)
            {
                int same = 0;
                for (int i = 0; i < _seenAgt.Count; i++)
                    if (Agents[_seenAgt[i] - 1].Action == srcAct) same++;
                if (same >= 3) pull *= Cfg.HerdBonus;

                int tgt = FindTargetForAction(a, srcAct);
                if (tgt >= 0)
                {
                    float sc = Cfg.W_Imit * pull * 1.5f;
                    Add(srcAct, tgt, false, 0, sc);
                }
            }
        }

        // --- 推搡最近的白痴（3 岁特征行为）---
        if (_seenAgt.Count > 0 && a.KnowsAction(ActionType.PushAgent))
        {
            int near = -1; float nd = 1.2f;
            for (int i = 0; i < _seenAgt.Count; i++)
            {
                float d = Vec2.Dist(a.Pos, Agents[_seenAgt[i] - 1].Pos);
                if (d < nd) { nd = d; near = _seenAgt[i]; }
            }
            if (near >= 0) Add(ActionType.PushAgent, near, true, 0, Cfg.W_Curio * 0.3f * a.PBoldness);
        }

        _candN = n;
    }

    /// <summary>新颖度：从没碰过的实例为 1，碰得越多越低（O(1)，见 Agent.Touched）。</summary>
    private static float Novelty(Agent a, Entity e) => 1f / (1f + a.TouchCount(e.Id) * 0.5f);

    private bool IsActionStillValid(Agent a, ActionType act, int target, bool isAgent)
    {
        if (target < 0) return act is ActionType.Wander or ActionType.Rest or ActionType.DropHeld or ActionType.Throw or ActionType.Cry;
        if (isAgent)
        {
            if (target <= 0 || target > Agents.Count) return false;
            return Agents[target - 1].Alive;
        }
        if (target >= EntityById.Length) return false;
        var e = EntityById[target];
        return e != null && !e.Held && !e.InPool;
    }

    private void PickWanderTarget(Agent a)
    {
        // 随手挑个地方晃过去——但不会主动把水池当成目的地。
        //
        // 注意不能在整间屋子里均匀取点：目标是均匀的话，"直线走过去"这个过程本身就会把人
        // 堆到屋子正中央——靠墙的位置只被极少数恰好伸得到那儿的路线覆盖，中心却被几乎所有
        // 路线覆盖，稳态密度在墙边趋于零。实测 1 游戏分钟内 500 人里有 115 个挤进 20m×20m
        // 的中央方块，而 x≈±35 的两列一共只有 22 人；偏偏电暖气、插座、木板、铁钉全在墙上，
        // 他们永远走不到跟前。所以改成"短途晃悠"：就近挑一个点，走过去再挑下一个。
        float m = Cfg.PoolHalf + 1.5f;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var p = a.Pos + a.Rng.NextDir() * a.Rng.Range(3f, 18f);
            ClampToRoom(ref p);
            if (Vec2.Dist(p, a.Pos) < 1.5f) continue;   // 贴着墙时会被夹回原地，换个方向再试
            if (MathF.Abs(p.X) < m && MathF.Abs(p.Z) < m) continue;
            a.MoveTarget = p;
            a.HasMoveTarget = true;
            return;
        }
        a.HasMoveTarget = false;   // 放弃这次，下个 tick 再说
    }

    private void ClampToRoom(ref Vec2 p)
    {
        float lim = Cfg.RoomHalf - 0.6f;
        p = new Vec2(Math.Clamp(p.X, -lim, lim), Math.Clamp(p.Z, -lim, lim));
    }

    // 全屋有 400 个插座，线性扫描是热路径上的大头（每 tick 上万次 × 400）。
    // 一律走实体网格：Query 半径 = maxD 时，返回集已经保证在半径内。
    private int NearestSocketWhere(Vec2 p, float maxD, bool mustBeEmpty)
    {
        EnsureEntityGrid();
        var cand = _entityGrid.Query(p, maxD);
        int best = -1; float bd = maxD;
        for (int k = 0; k < cand.Count; k++)
        {
            var s = Entities[cand[k]];
            if (s.Type != EntityType.Socket) continue;
            if (mustBeEmpty && s.SState != SocketState.Empty) continue;
            float d = Vec2.Dist(p, s.Pos);
            if (d < bd) { bd = d; best = s.Id; }
        }
        return best;
    }

    /// <summary>最近的一台正在运转的电暖气（用于"机器教人"那一步）。</summary>
    public int NearestRunningHeater(Vec2 p, float maxD)
    {
        int best = -1; float bd = maxD;
        for (int i = 0; i < HeaterIds.Count; i++)
        {
            var h = EntityById[HeaterIds[i]];
            if (!h.Running || h.Broken) continue;
            float d = Vec2.Dist(p, h.Pos);
            if (d < bd) { bd = d; best = h.Id; }
        }
        return best;
    }

    public int NearestSocket(Agent a, float maxD) => NearestSocketWhere(a.Pos, maxD, false);

    public int NearestSocketAnywhere(Vec2 p, float maxD) => NearestSocketWhere(p, maxD, false);

    public int NearestEmptySocketTo(Vec2 p, float maxD) => NearestSocketWhere(p, maxD, true);

    private int NearestOfType(Agent a, EntityType t, float maxD)
    {
        int best = -1; float bd = maxD;
        for (int i = 0; i < _seenEnt.Count; i++)
        {
            var e = EntityById[_seenEnt[i]];
            if (e.Type != t || e.Held) continue;
            float d = Vec2.Dist(a.Pos, e.Pos);
            if (d < bd) { bd = d; best = e.Id; }
        }
        return best;
    }

    /// <summary>为模仿找到一个合适的执行对象。</summary>
    private int FindTargetForAction(Agent a, ActionType act)
    {
        switch (act)
        {
            case ActionType.InsertNail:
                return a.HeldId >= 0 && EntityById[a.HeldId].Type == EntityType.Nail ? NearestSocket(a, 20f) : -1;
            case ActionType.Drill:
                return a.HeldId >= 0 && EntityById[a.HeldId].Type == EntityType.Stick ? NearestOfType(a, EntityType.Plank, 20f) : -1;
            case ActionType.ContactHeater:
            case ActionType.PlugIn:
            case ActionType.TurnKnob:
            case ActionType.PushObject:
            case ActionType.TouchProp:
                return NearestOfType(a, EntityType.Heater, 20f);
            default:
                return -1;
        }
    }
}
