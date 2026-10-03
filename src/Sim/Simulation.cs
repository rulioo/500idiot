using IdiotSim.Agents;
using IdiotSim.Core;
using IdiotSim.Knowledge;
using IdiotSim.Recording;
using IdiotSim.World;

namespace IdiotSim;

/// <summary>
/// 仿真主体。design.md §8.1：本类及其全部依赖位于 `Sim` 程序集，零 UnityEngine 依赖，
/// 可无头运行、可用 Burst 加速、可移植到其他引擎。
///
/// 确定性契约（§5.2）：相同 worldSeed + 相同初始状态 + 相同 tick 序列 → 完全相同结果。
/// 因此本文件禁止出现 DateTime.Now / System.Random / 并行归约。
/// </summary>
public sealed partial class Simulation
{
    public readonly SimConfig Cfg;
    public readonly List<Agent> Agents = new();
    public readonly List<Entity> Entities = new();
    public Entity[] EntityById;
    public List<int> SocketIds = new();
    public List<int> HeaterIds = new();
    public List<int> NailIds = new();
    public List<int> PlankIds = new();
    public List<int> StickIds = new();
    public List<int> WeedIds = new();

    public readonly EventLog Events = new();
    public SampleStore Samples;

    public int Tick;
    public float GlobalTemp;
    public int FireEntityId = -1;
    public float FireFuel;
    public bool FireBurning;

    // 白痴：5m 格子（密集、需要精确的邻居）；实体：10m 格子（稀疏、几乎不动）
    private readonly SpatialGrid _grid = new(5f);
    private readonly SpatialGrid _entityGrid = new(10f);

    // ---------- KPI（design.md §10.1）----------
    public int T_FirstHeaterPush = -1;
    public int T_FirstHeaterPlug = -1;
    public int T_FirstHeaterKnob = -1;
    public int T_FirstHeaterOn = -1;
    public int T_FirstBurn = -1;
    public int T_FirstInsertNail = -1;
    public int T_FirstDeath = -1;
    public int T_FirstDrown = -1;
    public int T_FirstShock = -1;
    public int T_FirstFire = -1;
    public int T_Comfort = -1;
    public int T_FearCross = -1;          // K_heater_warm 持有者数上穿 K_heater_hot 的那一刻
    public int FirstHeaterAgent = -1;
    public int FirstBurnAgent = -1;
    public int FirstFireAgent = -1;
    public int FirstInsertAgent = -1;

    public int DeathsDrown, DeathsShock, DeathsInjury;
    public int BurnEvents;
    public float MaxDrillProgress;        // 诊断：全屋搓到过的最大进度
    public int DrillNoFuel;               // 诊断：搓够了却找不到引火物而白搓的次数

    // 诊断计数器（只在无头调参时看）
    public int DiagDrillCand, DiagDrillChosen, DiagHeldStick, DiagHeldStickKnowsDrill;
    public int DiagDrillCommitKept, DiagDrillCommitTimeout, DiagDrillCommitInvalid;
    public int DiagPoolBlockNow, DiagPoolBlockMax;
    public int ComfortOscillations;
    private bool _wasComfortable;

    // 知识扩散曲线采样
    public readonly List<(int tick, float temp)> TempCurve = new();
    public readonly List<(int tick, int warm, int hot, int deadly, int water, int fire)> KnowledgeCurve = new();
    public int _comfortableSinceTick = -1;

    public int AliveCount { get; private set; }

    public Simulation(SimConfig cfg)
    {
        Cfg = cfg;
        GlobalTemp = cfg.T_Out;
        Arena.Build(this);
        Samples = new SampleStore(cfg.AgentCount);
        AliveCount = Agents.Count;

        // 初始轨迹 / 身体采样
        for (int i = 0; i < Agents.Count; i++)
        {
            RecordTraj(Agents[i]);
            RecordBody(Agents[i]);
        }
        TempCurve.Add((0, GlobalTemp));
    }

    public float GameSeconds => Tick * Cfg.TickSeconds;
    public float GameMinutes => GameSeconds / 60f;
    public float GameHours => GameSeconds / 3600f;

    public static string FormatGameTime(int tick, SimConfig cfg)
    {
        float sec = tick * cfg.TickSeconds;
        int h = (int)(sec / 3600f);
        int m = (int)((sec - h * 3600f) / 60f);
        int s = (int)(sec - h * 3600f - m * 60f);
        return $"{h:00}:{m:00}:{s:00}";
    }

    // =====================================================================
    //  主循环
    // =====================================================================
    public void Run(int gameSeconds)
    {
        int ticks = Cfg.SecondsToTicks(gameSeconds);
        for (int i = 0; i < ticks; i++) Step();
    }

    public void Step()
    {
        Tick++;
        DiagPoolBlockNow = 0;

        UpdateHeaters();
        UpdateFire();
        if (Tick % Cfg.BrainEveryTicks == 0) UpdateTemperature();

        // 运动（每 tick）
        for (int i = 0; i < Agents.Count; i++)
        {
            var a = Agents[i];
            if (a.Alive) Motor(a);
        }
        ResolveCollisions();
        PoolEdgeBrace();
        CheckPool();

        // 大脑（错峰：每 tick 只有 1/4 的人决策，§8.2）
        int phase = Tick % Cfg.BrainEveryTicks;
        for (int i = 0; i < Agents.Count; i++)
        {
            var a = Agents[i];
            if (!a.Alive) continue;
            if ((a.Id % Cfg.BrainEveryTicks) != phase) continue;
            Perceive(a);
            Think(a);
            Act(a);
        }

        // 身体（错峰）
        if (Tick % Cfg.BodyEveryTicks == 0)
        {
            int bodyPhase = (Tick / Cfg.BodyEveryTicks) % 4;
            for (int i = 0; i < Agents.Count; i++)
            {
                var a = Agents[i];
                if (!a.Alive) continue;
                if ((a.Id % 4) != bodyPhase) continue;
                UpdateBody(a);
            }
        }

        // 记忆衰减 + 泛化（每 300 游戏秒）
        if (Tick % Cfg.SecondsToTicks(Cfg.DecayPeriodSeconds) == 0)
        {
            for (int i = 0; i < Agents.Count; i++)
                if (Agents[i].Alive) DecayAndGeneralize(Agents[i]);
        }

        // 记录
        if (Tick % Cfg.TrajEveryTicks == 0)
        {
            for (int i = 0; i < Agents.Count; i++) RecordTraj(Agents[i]);
            TempCurve.Add((Tick, GlobalTemp));
        }
        if (Tick % Cfg.BodySampleEveryTicks == 0)
            for (int i = 0; i < Agents.Count; i++) RecordBody(Agents[i]);

        if (Tick % Cfg.SecondsToTicks(60f) == 0) SampleKnowledgeCurve();
    }

    // =====================================================================
    //  温度（design.md §2.2）
    // =====================================================================
    private void UpdateTemperature()
    {
        float dt = Cfg.BrainEveryTicks * Cfg.TickSeconds;   // 1 游戏秒

        float q = AliveCount * Cfg.Q_Body + Cfg.Q_Pool;
        for (int i = 0; i < HeaterIds.Count; i++)
        {
            var h = EntityById[HeaterIds[i]];
            if (h.Running && !h.Broken && !h.ThermostatCut) q += Cfg.Q_Heater * h.HeatRamp;
        }
        if (FireBurning) q += Cfg.Q_Fire;

        float dT = (q - Cfg.K_Dissipation * (GlobalTemp - Cfg.T_Out)) / Cfg.C_HeatCapacity * dt;
        GlobalTemp += dT;
        if (GlobalTemp > Cfg.T_Max) GlobalTemp = Cfg.T_Max;

        // G3：全局温度首次进入舒适区间（并持续）
        if (GlobalTemp >= Cfg.ComfortLow && GlobalTemp <= Cfg.ComfortHigh + 6f)
        {
            if (_comfortableSinceTick < 0) _comfortableSinceTick = Tick;
            else if (T_Comfort < 0 && Tick - _comfortableSinceTick >= Cfg.SecondsToTicks(60f))
            {
                T_Comfort = Tick;
                Emit(0, EventType.ComfortReached, Vec2.Zero, 0, 0, GlobalTemp);
                Emit(0, EventType.Milestone, Vec2.Zero, (int)MilestoneId.Comfort, 0, GameSeconds);
            }
            _wasComfortable = true;
        }
        else
        {
            if (_wasComfortable && GlobalTemp < Cfg.ComfortLow - 0.5f)
            {
                ComfortOscillations++;
                _wasComfortable = false;
                Emit(0, EventType.ComfortLost, Vec2.Zero, 0, 0, GlobalTemp);
            }
            _comfortableSinceTick = -1;
        }
    }

    /// <summary>局部温度 = 全局基线 + 热源点衰减（design.md §2.2）。</summary>
    public float LocalTemperatureAt(Vec2 p)
    {
        float t = GlobalTemp;
        for (int i = 0; i < HeaterIds.Count; i++)
        {
            var h = EntityById[HeaterIds[i]];
            if (!h.Running || h.Broken) continue;
            float d = Vec2.Dist(p, h.Pos);
            if (d >= Cfg.HeaterLocalRadius) continue;
            float f = 1f - d / Cfg.HeaterLocalRadius;
            t += Cfg.HeaterLocalDeltaT * f * f * h.HeatRamp;
        }
        if (FireBurning && FireEntityId >= 0)
        {
            float d = Vec2.Dist(p, EntityById[FireEntityId].Pos);
            if (d < Cfg.FireLocalRadius)
            {
                float f = 1f - d / Cfg.FireLocalRadius;
                t += Cfg.FireLocalDeltaT * f * f;
            }
        }
        return t;
    }

    private void UpdateHeaters()
    {
        float dt = Cfg.TickSeconds;
        for (int i = 0; i < HeaterIds.Count; i++)
        {
            var h = EntityById[HeaterIds[i]];
            if (h.Broken) { h.Running = false; h.SurfaceTemp = Math.Max(Cfg.T_Out, h.SurfaceTemp - dt * 2f); continue; }

            bool powerOn = h.Plugged && h.Knob >= 1;
            if (powerOn && !h.Running && T_FirstHeaterOn < 0)
            {
                T_FirstHeaterOn = Tick;
                FirstHeaterAgent = 0;
            }
            h.Running = powerOn;

            // 温控器：到温度就断，掉下来再接上。这不是"白痴变聪明了"，
            // 是电暖气自带的机械装置——它的存在让屋子停在一个平衡点，
            // 而不是一路烧到 35°C 天花板。
            if (h.ThermostatCut) { if (GlobalTemp <= Cfg.HeaterThermostatOn) h.ThermostatCut = false; }
            else { if (GlobalTemp >= Cfg.HeaterThermostatOff) h.ThermostatCut = true; }
            bool heating = h.Running && !h.ThermostatCut;

            if (heating)
            {
                if (h.HeatRamp < 1f) h.HeatRamp = Math.Min(1f, h.HeatRamp + dt / Cfg.HeaterRampSeconds);
                // 外壳温度 30 游戏秒内升到 75~90°C
                h.SurfaceTemp = Cfg.T_Out + (82f - Cfg.T_Out) * h.HeatRamp;
            }
            else
            {
                h.HeatRamp = Math.Max(0f, h.HeatRamp - dt / Cfg.HeaterRampSeconds);
                h.SurfaceTemp += (Cfg.T_Out - h.SurfaceTemp) * dt / 120f;
            }
        }
    }

    private void UpdateFire()
    {
        if (!FireBurning) return;
        FireFuel -= Cfg.TickSeconds;
        if (FireFuel <= 0f)
        {
            FireBurning = false;
            var f = EntityById[FireEntityId];
            Emit(0, EventType.FireOut, f.Pos, FireEntityId, 0, GameSeconds);
        }
    }

    // =====================================================================
    //  身体（design.md §3.7）
    // =====================================================================
    private void UpdateBody(Agent a)
    {
        float dt = Cfg.BodyEveryTicks * Cfg.TickSeconds;   // 4 游戏秒
        a.LocalTemp = LocalTemperatureAt(a.Pos);

        // --- 核心体温 ---
        // 平衡点：T_local + 20.3（15°C -> 35.3°C），上限 37°C。见 design.md §3.7.2
        float target = Math.Clamp(a.LocalTemp + 20.3f, 33.4f, 37.0f);
        if (a.Action == ActionType.Flee || a.Action == ActionType.Wander) target += 0.15f;
        a.CoreTemp += (target - a.CoreTemp) / Cfg.BodyTau * dt;
        a.CoreTemp = Math.Clamp(a.CoreTemp, 32f, 38f);

        // --- 敏捷度 ---
        float dex = DexFromCoreTemp(a.CoreTemp);
        a.Dexterity = Math.Max(0.1f, dex * (1f - a.Pain * 0.4f));

        // --- 疼痛衰减 ---
        if (a.Pain > 0f) a.Pain = Math.Max(0f, a.Pain - Cfg.PainDecayPerSecond * dt);

        // --- 舒适度 ---
        float t = a.LocalTemp;
        float comfort;
        if (t < 15f) comfort = 0f;
        else if (t < 18f) comfort = 0.3f * (t - 15f) / 3f;
        else if (t < 20f) comfort = 0.3f + 0.3f * (t - 18f) / 2f;
        else if (t <= 26f) comfort = 1.0f;
        else if (t <= 32f) comfort = 1.0f - 0.3f * (t - 26f) / 6f;
        else comfort = 0.7f - 0.4f * Math.Min(1f, (t - 32f) / 8f);
        a.Comfort = comfort;
        a.Warmth = 1f - comfort;

        // --- 伤势痊愈 ---
        for (int i = a.Injuries.Count - 1; i >= 0; i--)
        {
            var inj = a.Injuries[i];
            if (Tick >= inj.HealTick)
            {
                a.Injuries.RemoveAt(i);
                Emit((ushort)a.Id, EventType.Heal, a.Pos, 0, (int)inj.Type, GameSeconds);
            }
        }

        // --- 生命恢复：仅当舒适（design.md §3.7.1）---
        if (a.Comfort >= 0.6f && a.Vitality < 100f)
        {
            a.Vitality = Math.Min(100f, a.Vitality + Cfg.VitalityRegenPer600s * dt / 600f);
        }

        // --- 温暖的持续强化 ---
        if (a.LocalTemp >= Cfg.ComfortLow && a.LocalTemp <= 30f)
        {
            a.HasEverFeltWarm = true;
            if (Tick - a.LastWarmTick >= Cfg.SecondsToTicks(60f))
            {
                a.LastWarmTick = Tick;
                a.AddBelief(KnowledgeId.HeaterWarm, -1, Cfg.ConfWarmReinforce, BeliefSource.PersonalExperience, 0, Tick);

                // H4：站在一台开着的电暖气旁边，人会低头看这台机器——
                // 它跟别的不一样，侧面那个东西是转过的。这一步是**机器自己在教人**，
                // 没有它，发现链就只活在第一发现者一个人的脑子里，全屋永远不会暖起来。
                int h = NearestRunningHeater(a.Pos, Cfg.HeaterLocalRadius);
                if (h >= 0)
                {
                    var heater = EntityById[h];
                    RevealNextStep(a, heater, KnowledgeId.HeaterKnobExists, ActionType.TurnKnob, 0.30f);
                    RevealNextStep(a, heater, KnowledgeId.HeaterPlugFits, ActionType.PlugIn, 0.12f);
                }

                // 火堆同理：蹲在火边的人会记住"火是暖和的"
                if (FireBurning && FireEntityId >= 0
                    && Vec2.Dist(a.Pos, EntityById[FireEntityId].Pos) < Cfg.FireLocalRadius)
                {
                    a.AddBelief(KnowledgeId.FireWarm, -1, Cfg.ConfWarmReinforce * 2f,
                                BeliefSource.PersonalExperience, 0, Tick);
                }
            }
        }

        if (a.Vitality <= 0f) Kill(a, DeathCause.Injury, -1);
    }

    public static float DexFromCoreTemp(float ct)
    {
        if (ct >= 36.5f) return 1.00f;
        if (ct >= 36.0f) return 0.95f;
        if (ct >= 35.5f) return 0.85f;
        if (ct >= 35.0f) return 0.70f;
        if (ct >= 34.5f) return 0.50f;
        return 0.30f;
    }

    public static float InsertErrorFromCoreTemp(float ct)
    {
        if (ct >= 36.5f) return 0.05f;
        if (ct >= 36.0f) return 0.08f;
        if (ct >= 35.5f) return 0.15f;
        if (ct >= 35.0f) return 0.25f;
        if (ct >= 34.5f) return 0.40f;
        return 0.55f;
    }

    public static float NoiseMultFromCoreTemp(float ct)
    {
        if (ct >= 36.0f) return 1.0f;
        if (ct >= 35.5f) return 1.2f;
        if (ct >= 35.0f) return 1.5f;
        if (ct >= 34.5f) return 2.0f;
        return 3.0f;
    }

    public static float SpeedMultFromCoreTemp(float ct)
    {
        if (ct >= 36.0f) return 1.00f;
        if (ct >= 35.5f) return 0.90f;
        if (ct >= 35.0f) return 0.80f;
        if (ct >= 34.5f) return 0.65f;
        return 0.50f;
    }

    public static string HypothermiaLabel(float ct)
    {
        if (ct >= 36.5f) return "正常";
        if (ct >= 36.0f) return "略冷";
        if (ct >= 35.5f) return "轻度失温";
        if (ct >= 35.0f) return "中度失温";
        if (ct >= 34.5f) return "重度失温";
        return "濒临失温";
    }

    public void ApplyInjury(Agent a, InjuryType type, float dmg, float pain, int sourceEntityId, string _)
    {
        a.Vitality -= dmg;
        a.Pain = Math.Min(1f, a.Pain + pain);
        int healTicks = Cfg.SecondsToTicks(type == InjuryType.Burn ? 2400f : 1200f);
        a.Injuries.Add(new Injury
        {
            Type = type,
            Severity = Math.Min(1f, dmg / 20f),
            Tick = Tick,
            SourceEntityId = sourceEntityId,
            HealTick = Tick + healTicks,
        });
        if (a.Injuries.Count > 16) a.Injuries.RemoveAt(0);
        Emit((ushort)a.Id, EventType.Injury, a.Pos, sourceEntityId, (int)type, dmg);
        if (a.Vitality <= 0f) Kill(a, DeathCause.Injury, sourceEntityId);
    }

    public void Kill(Agent a, DeathCause cause, int sourceEntityId)
    {
        if (!a.Alive) return;
        a.Alive = false;
        a.Death = cause;
        a.DeathTick = Tick;
        AliveCount--;

        var et = cause switch
        {
            DeathCause.Drown => EventType.DeathDrown,
            DeathCause.Shock => EventType.DeathShock,
            _ => EventType.DeathInjury,
        };
        Emit((ushort)a.Id, et, a.Pos, sourceEntityId, (int)cause, GameSeconds);

        if (T_FirstDeath < 0) { T_FirstDeath = Tick; }
        if (cause == DeathCause.Drown && T_FirstDrown < 0) T_FirstDrown = Tick;
        if (cause == DeathCause.Shock && T_FirstShock < 0) T_FirstShock = Tick;
        if (cause == DeathCause.Drown) DeathsDrown++;
        else if (cause == DeathCause.Shock) DeathsShock++;
        else DeathsInjury++;
        // 死亡的目击传播由调用方负责（避免同一次死亡广播两次）：
        //   Act / Resolve* 里的死亡带具体动作；CheckPool 的溺亡见下。
    }

    // =====================================================================
    //  运动与碰撞
    // =====================================================================
    /// <summary>水池边沿的"止步线"。3 岁的孩子看得见地上有个坑，不会好好走着就掉下去——
    /// 但**惊慌逃跑**和**被人推**的时候会。这一条把 244 例无谓溺亡压回合理量级，
    /// 同时保留了"慌不择路掉进水里"这一设计要的戏。</summary>
    private bool WouldStepInPool(Agent a, Vec2 next)
    {
        // 惊慌 / 疼痛 / 逃跑 —— 看不见路
        bool blind = a.Emo == Emotion.Panic || a.Emo == Emotion.Fear || a.Emo == Emotion.Hurt || a.Fleeing;
        if (blind) return false;   // 不拦，让他们跑进去

        float m = Cfg.PoolHalf + 0.9f;   // 加上身体半径与一点安全边距
        return MathF.Abs(next.X) < m && MathF.Abs(next.Z) < m;
    }

    private void Motor(Agent a)
    {
        if (!a.HasMoveTarget) return;
        Vec2 to = a.MoveTarget - a.Pos;
        float d = to.Length;
        if (d < 0.05f) { a.HasMoveTarget = false; return; }
        float speed = (a.Emo == Emotion.Fear || a.Emo == Emotion.Panic ? Cfg.RunSpeed : Cfg.WalkSpeed)
                      * SpeedMultFromCoreTemp(a.CoreTemp);
        float step = speed * Cfg.TickSeconds;
        if (step > d) step = d;
        var dir = to / d;
        var next = a.Pos + dir * step;

        if (WouldStepInPool(a, next))
        {
            DiagPoolBlockNow++;
            if (DiagPoolBlockNow > DiagPoolBlockMax) DiagPoolBlockMax = DiagPoolBlockNow;
            // 绕着水池走，而不是一头撞上去就愣在原地。
            // 原先的做法是"停住 + 丢掉目标"，可只要目标在池子对面，下个大脑 tick 就会
            // 原样重发一次同样的指令，于是人被永久钉在池沿——一个 3 岁的孩子绕着水坑走
            // 是常识，直挺挺站着不动不是。转向从 ±30° 试到 ±90°，取第一个不撞水的方向。
            // 左右偏好按 id 奇偶分，免得全屋人绕着池子同向转圈。
            bool slid = false;
            int first = (a.Id & 1) == 0 ? -1 : 1;
            for (int s = 1; s <= 3 && !slid; s++)
            {
                float ang = s * (MathF.PI / 6f);
                for (int k = 0; k < 2; k++)
                {
                    float signed = ang * (k == 0 ? first : -first);
                    float cs = MathF.Cos(signed), sn = MathF.Sin(signed);
                    var d2 = new Vec2(dir.X * cs - dir.Z * sn, dir.X * sn + dir.Z * cs);
                    var n2 = a.Pos + d2 * step;
                    if (WouldStepInPool(a, n2)) continue;
                    next = n2; dir = d2; slid = true; break;
                }
            }
            if (!slid)
            {
                // 三面都是水（角落），这次真的走不了
                a.HasMoveTarget = false;
                a.MoveTarget = a.Pos;
                return;
            }
        }

        a.Pos = next;
        a.Facing = Vec2.Lerp(a.Facing, dir, 0.25f).Normalized;

        // 墙体
        float lim = Cfg.RoomHalf - 0.4f;
        a.Pos = new Vec2(Math.Clamp(a.Pos.X, -lim, lim), Math.Clamp(a.Pos.Z, -lim, lim));
    }

    private void ResolveCollisions()
    {
        _grid.Clear();
        for (int i = 0; i < Agents.Count; i++)
            if (Agents[i].Alive) _grid.Insert(i, Agents[i].Pos);

        const float r = 0.32f;
        for (int i = 0; i < Agents.Count; i++)
        {
            var a = Agents[i];
            if (!a.Alive) continue;
            var cand = _grid.Query(a.Pos, 1.0f);
            for (int k = 0; k < cand.Count; k++)
            {
                int j = cand[k];
                if (j <= i) continue;
                var b = Agents[j];
                if (!b.Alive) continue;
                Vec2 d = a.Pos - b.Pos;
                float dist = d.Length;
                if (dist < r * 2f && dist > 1e-4f)
                {
                    Vec2 push = d / dist * (r * 2f - dist) * 0.5f;
                    a.Pos += push;
                    b.Pos -= push;
                }
            }
        }
    }

    /// <summary>
    /// 站在池边的人会本能地往后缩。人群挤压、贴边推搡本来会把成百上千人蹭下水，
    /// 那样这个仿真就只剩溺亡一个剧情了。清醒的人被挤到池边时站住；
    /// 惊慌逃跑、疼痛挣扎、刚被人推了一把（PushedUntilTick）的人——照样掉下去。
    /// </summary>
    private void PoolEdgeBrace()
    {
        float ph = Cfg.PoolHalf;
        float m = ph + 0.35f;          // 身体半径：中心越过这条线才算踩空
        for (int i = 0; i < Agents.Count; i++)
        {
            var a = Agents[i];
            if (!a.Alive) continue;
            if (Tick < a.PushedUntilTick) continue;
            if (a.Emo == Emotion.Panic || a.Emo == Emotion.Fear || a.Emo == Emotion.Hurt || a.Fleeing) continue;

            float ax = a.Pos.X, az = a.Pos.Z;
            if (MathF.Abs(ax) >= m || MathF.Abs(az) >= m) continue;

            // 推到最近的池沿外
            float dx = m - MathF.Abs(ax);
            float dz = m - MathF.Abs(az);
            if (dx < dz) a.Pos = new Vec2(MathF.Sign(ax) * m, az);
            else a.Pos = new Vec2(ax, MathF.Sign(az) * m);
            a.HasMoveTarget = false;
        }
    }

    private void CheckPool()
    {
        float ph = Cfg.PoolHalf;
        for (int i = 0; i < Agents.Count; i++)
        {
            var a = Agents[i];
            if (!a.Alive) continue;
            if (MathF.Abs(a.Pos.X) < ph && MathF.Abs(a.Pos.Z) < ph)
            {
                Emit((ushort)a.Id, EventType.EnterPool, a.Pos, 0, 0, GameSeconds);
                Kill(a, DeathCause.Drown, -1);
                // 溺亡是最强的"水很危险"教学事件（§4.4）
                BroadcastDeed(a, ActionType.Idle, -1, OutcomeType.Death, 1.0f, -100f, 0f, 0f, 30f);
            }
        }
    }

    // =====================================================================
    //  记录
    // =====================================================================
    public void Emit(ushort agentId, EventType type, Vec2 pos, int targetA, int targetB, float value, float value2 = 0f)
    {
        Events.Add(new EventRecord
        {
            Tick = Tick,
            AgentId = agentId,
            Type = type,
            Pos = pos,
            TargetA = targetA,
            TargetB = targetB,
            Value = value,
            Value2 = value2,
        });
    }

    private static byte StateByte(Agent a)
    {
        if (!a.Alive) return 6;
        return a.Emo switch
        {
            Emotion.Panic => 5,
            Emotion.Fear => 4,
            Emotion.Hurt => 3,
            _ => a.HasMoveTarget ? (byte)1 : (byte)0,
        };
    }

    private void RecordTraj(Agent a)
    {
        float ang = MathF.Atan2(a.Facing.X, a.Facing.Z);
        int h = (int)((ang + MathF.PI) / MathF.Tau * 255f);
        Samples.AddTraj(a.Id, new TrajSample
        {
            Tick = Tick,
            X = a.Pos.X,
            Z = a.Pos.Z,
            Heading = (byte)Math.Clamp(h, 0, 255),
            State = StateByte(a),
            Flags = (byte)(a.HeldId >= 0 ? 1 : 0),
        });
    }

    private void RecordBody(Agent a)
    {
        Samples.AddBody(a.Id, new BodySample
        {
            Tick = Tick,
            AgentId = (byte)Math.Min(255, a.Id),
            Vitality = (byte)Math.Clamp(a.Vitality, 0, 100),
            CoreTempQ = (byte)Math.Clamp((int)(a.CoreTemp * 5f), 0, 255),
            Pain = (byte)Math.Clamp((int)(a.Pain * 255f), 0, 255),
            InjuryCount = (byte)Math.Min(255, a.Injuries.Count),
            LastInjuryType = (byte)(a.Injuries.Count > 0 ? (int)a.Injuries[^1].Type : 0),
        });
    }

    private void SampleKnowledgeCurve()
    {
        int warm = 0, hot = 0, deadly = 0, water = 0, fire = 0;
        for (int i = 0; i < Agents.Count; i++)
        {
            var a = Agents[i];
            if (a.ConfidenceOf(KnowledgeId.HeaterWarm) > 0.5f) warm++;
            if (a.ConfidenceOf(KnowledgeId.HeaterHot) > 0.5f) hot++;
            if (a.ConfidenceOf(KnowledgeId.HoleRightDeadly) > 0.5f) deadly++;
            if (a.ConfidenceOf(KnowledgeId.WaterDanger) > 0.5f) water++;
            if (a.ConfidenceOf(KnowledgeId.FireWarm) > 0.5f) fire++;
        }
        KnowledgeCurve.Add((Tick, warm, hot, deadly, water, fire));

        // T_fear_cross：K_warm 反超 K_hot（design.md §4.6 / A12）
        if (T_FearCross < 0 && KnowledgeCurve.Count > 4 && warm > hot && hot > 0)
        {
            T_FearCross = Tick;
            Emit(0, EventType.Milestone, Vec2.Zero, (int)MilestoneId.FearCross, 0, GameSeconds);
        }
    }

    public int CountKnowing(KnowledgeId id, float thr = 0.5f)
    {
        int n = 0;
        for (int i = 0; i < Agents.Count; i++)
            if (Agents[i].ConfidenceOf(id) > thr) n++;
        return n;
    }

    /// <summary>会做某个动作的白痴数量 —— 发现链传播的真正的度量。</summary>
    public int CountKnowingAction(ActionType act)
    {
        int n = 0;
        for (int i = 0; i < Agents.Count; i++)
            if (Agents[i].Alive && Agents[i].KnowsAction(act)) n++;
        return n;
    }

    /// <summary>电暖气状态盘点。</summary>
    public (int pushed, int plugged, int running, int broken) HeaterState()
    {
        int pushed = 0, plugged = 0, running = 0, broken = 0;
        for (int i = 0; i < HeaterIds.Count; i++)
        {
            var h = EntityById[HeaterIds[i]];
            // 四项是**累计**的，不是互斥状态——原先写成 else-if，结果 8 台在运转
            // 的报告里"已插电 0"，读起来像自相矛盾。
            if (h.Broken) { broken++; continue; }
            if (h.Running) running++;
            if (h.Plugged) plugged++;
            if (NearestEmptySocketTo(h.Pos, Cfg.HeaterPowerCordLength) >= 0) pushed++;
        }
        return (pushed, plugged, running, broken);
    }
}

public enum MilestoneId
{
    HeaterPush = 1,
    HeaterPlug = 2,
    HeaterKnob = 3,
    HeaterOn = 4,
    FirstBurn = 5,
    InsertNail = 6,
    Fire = 7,
    Comfort = 8,
    FearCross = 9,
}
