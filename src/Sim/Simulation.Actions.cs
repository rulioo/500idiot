using IdiotSim.Agents;
using IdiotSim.Core;
using IdiotSim.Knowledge;
using IdiotSim.Recording;
using IdiotSim.World;

namespace IdiotSim;

public sealed partial class Simulation
{
    private const float InnovationBase = 0.020f;   // 每次试探的"发明"概率

    public int DiagActDrill, DiagActDrillFar, DiagActDrillTick, DiagActDrillFire;

    private float ReachOf(ActionType t) => t switch
    {
        ActionType.InsertNail => 0.9f,
        ActionType.ContactHeater => 0.7f,
        ActionType.PlugIn => 1.3f,
        ActionType.TurnKnob => 0.9f,
        ActionType.PushObject => 1.0f,
        ActionType.Drill => 1.2f,
        ActionType.Grab => Cfg.ReachRange,
        ActionType.TouchProp => Cfg.ReachRange,
        _ => Cfg.ReachRange,
    };

    private Vec2 TargetPos(Agent a)
    {
        if (a.TargetIsAgent)
        {
            if (a.TargetId > 0 && a.TargetId <= Agents.Count) return Agents[a.TargetId - 1].Pos;
            return a.Pos;
        }
        if (a.TargetId >= 0 && a.TargetId < EntityById.Length && EntityById[a.TargetId] != null)
            return EntityById[a.TargetId].Pos;
        return a.Pos;
    }

    // =====================================================================
    //  动作执行
    // =====================================================================
    private void Act(Agent a)
    {
        float dt = Cfg.BrainEveryTicks * Cfg.TickSeconds;

        switch (a.Action)
        {
            case ActionType.Wander:
                if (!a.HasMoveTarget) { PickWanderTarget(a); a.ActionTimer = 8f; }
                if (Vec2.Dist(a.Pos, a.MoveTarget) < 1.2f) { a.HasMoveTarget = false; a.ActionTimer = 0f; }
                a.ActionTimer -= dt;
                if (a.ActionTimer <= 0f) { a.HasMoveTarget = false; a.Action = ActionType.Idle; }
                return;

            case ActionType.Rest:
                a.HasMoveTarget = false;
                a.ActionTimer -= dt;
                if (a.Comfort < 0.6f) a.Action = ActionType.Idle;
                return;

            case ActionType.Flee:
                a.ActionTimer -= dt;
                if (a.ActionTimer <= 0f || !a.HasMoveTarget) { a.Action = ActionType.Idle; a.HasMoveTarget = false; a.Fleeing = false; }
                return;

            case ActionType.Cry:
                DoCry(a);
                a.Action = ActionType.Idle;
                return;

            case ActionType.DropHeld:
                ResolveDrop(a);
                a.Action = ActionType.Idle;
                return;

            case ActionType.Throw:
                ResolveThrow(a);
                a.Action = ActionType.Idle;
                return;
        }

        // --- 需要接近目标的动作 ---
        if (a.TargetId < 0) { a.Action = ActionType.Idle; return; }
        if (a.TargetIsAgent)
        {
            if (a.TargetId <= 0 || a.TargetId > Agents.Count || !Agents[a.TargetId - 1].Alive)
            { a.Action = ActionType.Idle; return; }
        }
        else if (a.TargetId >= EntityById.Length || EntityById[a.TargetId] == null
                 || EntityById[a.TargetId].InPool)
        { a.Action = ActionType.Idle; return; }

        Vec2 tp = TargetPos(a);
        float reach = ReachOf(a.Action);
        float d = Vec2.Dist(a.Pos, tp);
        bool dr = a.Action == ActionType.Drill;
        if (dr) DiagActDrill++;

        if (d > reach)
        {
            if (dr) DiagActDrillFar++;
            a.MoveTarget = tp;
            a.HasMoveTarget = true;
            return;
        }

        a.HasMoveTarget = false;
        var f = (tp - a.Pos);
        if (f.LengthSq > 1e-5f) a.Facing = f.Normalized;

        a.ActionTimer -= dt;
        if (dr) DiagActDrillTick++;
        if (a.ActionTimer > 0f) return;

        if (dr) DiagActDrillFire++;
        Resolve(a);
    }

    // =====================================================================
    //  动作结算
    // =====================================================================
    private void Resolve(Agent a)
    {
        switch (a.Action)
        {
            case ActionType.Grab: ResolveGrab(a); break;
            case ActionType.TouchProp: ResolveTouch(a); break;
            case ActionType.InsertNail: ResolveInsertNail(a); break;
            case ActionType.ContactHeater: ResolveContactHeater(a); break;
            case ActionType.PlugIn: ResolvePlugIn(a); break;
            case ActionType.TurnKnob: ResolveTurnKnob(a); break;
            case ActionType.PushObject: ResolvePushObject(a); break;
            case ActionType.Drill: ResolveDrill(a); break;
            case ActionType.PushAgent: ResolvePushAgent(a); break;
            case ActionType.Approach: break;   // 纯移动，无结算
        }
        a.Action = ActionType.Idle;
        a.TimerAct = ActionType.Idle;          // 归还计时器，下一次从头计时
        a.TimerTarget = -1;
    }

    /// <summary>
    /// 铁钉被人拔走了：把插座的状态一起复位。
    ///
    /// 原先只把钉子拿走，`SState` 留在 `NailInLive`——墙上于是留下一个**看不见的**
    /// 致死插座：钉子早在别人手里攥着了，插座还照样见谁电谁，而且没有任何外观线索
    /// （钉子本身已经不在那儿了）。全屋最频繁的动作就是挨个摸插座，所以这是个永不停机
    /// 的绞肉机：3 小时里 7 个这样的空陷阱电死 183 人，最狠的一个杀了 47 个。
    /// 这不是剧情，是状态没同步。
    /// </summary>
    private void DetachNailFromSocket(Entity nail)
    {
        for (int i = 0; i < Entities.Count; i++)
        {
            var s = Entities[i];
            if (s.Type != EntityType.Socket || s.NailEntityId != nail.Id) continue;
            s.SState = SocketState.Empty;
            s.NailEntityId = -1;
            Emit(0, EventType.NailPulledOut, s.Pos, s.Id, nail.Id, GameSeconds);
            return;
        }
    }

    private void ResolveGrab(Agent a)
    {
        if (a.HeldId >= 0) return;
        var e = EntityById[a.TargetId];
        if (e == null || e.Held || !e.IsProp) return;
        var slot = GrabSlot(a);   // 一人一次只能拿一件（简化）
        e.Held = true;
        e.HolderId = a.Id;
        a.HeldId = e.Id;
        if (e.Type == EntityType.Nail) DetachNailFromSocket(e);
        Emit((ushort)a.Id, EventType.Grab, a.Pos, e.Id, 0, GameSeconds);

        // 手里攥着木棍，眼前有块木板——这两样东西放一起，是火链的第一环
        if (e.Type == EntityType.Stick)
        {
            a.AddBelief(KnowledgeId.StickPlankTogether, -1, 0.10f, BeliefSource.PersonalExperience, 0, Tick);
            if (NearestOfType(a, EntityType.Plank, 20f) >= 0)
                RevealNextStep(a, e, KnowledgeId.DrillingMotion, ActionType.Drill, 0.22f);
        }
        PushLedger(a, e, ActionType.Grab, e.Id, 0, OutcomeType.Success, 0, 0, 0, KnowledgeId.None, 0, 0);
    }

    private static int GrabSlot(Agent a) => 0;

    /// <summary>
    /// 掉进水池的东西就捞不回来了——没人会游泳，也没人敢下去捡。
    ///
    /// 关键在于**它必须立刻不再是目标**。原先只挪了位置、没改状态，于是水池里
    /// 躺着一块谁都看得见、谁都够不着的木板：全屋子的人一个接一个走过去，
    /// 在止步线上撞住、原地重发一次"走过去"的指令、再撞住……45 分钟时
    /// 336 个活人里有 278 个被这样钉死在池沿 1.6 米环带里，而且他们一边钉着
    /// 一边把手里的东西也丢进水里，又造出新的诱饵。这是个正反馈，会把整屋人吃光。
    /// </summary>
    private void SinkIfInPool(Entity e)
    {
        if (MathF.Abs(e.Pos.X) >= Cfg.PoolHalf || MathF.Abs(e.Pos.Z) >= Cfg.PoolHalf) return;
        e.InPool = true;
        Emit(0, EventType.PropInPool, e.Pos, e.Id, (int)e.Type, 0);
    }

    private void ResolveDrop(Agent a)
    {
        if (a.HeldId < 0) return;
        var e = EntityById[a.HeldId];
        e.Held = false;
        e.HolderId = -1;
        e.Pos = a.Pos + a.Facing * 0.6f;
        a.HeldId = -1;
        MarkEntityMoved();
        SinkIfInPool(e);
        Emit((ushort)a.Id, EventType.Drop, a.Pos, e.Id, 0, GameSeconds);
    }

    private void ResolveThrow(Agent a)
    {
        if (a.HeldId < 0) return;
        var e = EntityById[a.HeldId];
        e.Held = false;
        e.HolderId = -1;
        var tgt = a.Pos + a.Facing * a.Rng.Range(3f, 9f);
        ClampToRoom(ref tgt);
        e.Pos = tgt;
        a.HeldId = -1;
        MarkEntityMoved();
        SinkIfInPool(e);
        Emit((ushort)a.Id, EventType.Throw, a.Pos, e.Id, 0, GameSeconds);
        PushLedger(a, e, ActionType.Throw, e.Id, 0, OutcomeType.Success, 0, 0, 0, KnowledgeId.None, 0, 0);
    }

    /// <summary>
    /// 通用试探。**这是"发明"的引擎**（design.md §3.4）：白痴不会凭空想到新动作，
    /// 但好奇的试探偶尔会撞上一个新玩法——这就是"摸索"的起源。
    /// </summary>
    private void ResolveTouch(Agent a)
    {
        var e = EntityById[a.TargetId];
        if (e == null) return;

        var outcome = OutcomeType.NoEffect;
        float dv = 0, dp = 0, dc = 0;

        // 触碰插着火线铁钉的插座 = 触电身亡（design.md §2.4，Q2 默认开启）
        if (e.Type == EntityType.Socket && e.SState == SocketState.NailInLive)
        {
            Emit((ushort)a.Id, EventType.ContactHeater, a.Pos, e.Id, 0, GameSeconds);
            Kill(a, DeathCause.Shock, e.Id);
            BroadcastDeed(a, ActionType.TouchProp, e.Id, OutcomeType.Death, 1.0f, -100f, 1f, 0f, 30f);
            return;
        }

        // 触碰运转中的电暖气 = 烫伤
        if (e.Type == EntityType.Heater && e.Running && e.SurfaceTemp > 60f)
        {
            ApplyInjury(a, InjuryType.Burn, Cfg.BurnDamage, Cfg.BurnPain, e.Id, "touch");
            a.PainSourceId = e.Id;
            outcome = OutcomeType.Injury;
            dv = -Cfg.BurnDamage; dp = Cfg.BurnPain;
            a.BurnsSuffered++;
            BurnEvents++;
            if (T_FirstBurn < 0) { T_FirstBurn = Tick; FirstBurnAgent = a.Id; }
            a.AddBelief(KnowledgeId.HeaterHot, -1, Cfg.ConfPersonalBurn, BeliefSource.PersonalExperience, 0, Tick);
            Emit((ushort)a.Id, EventType.ContactHeater, a.Pos, e.Id, 0, GameSeconds);
            Emit((ushort)a.Id, EventType.Burn, a.Pos, e.Id, 0, Cfg.BurnDamage);
            DoCry(a);
            int w = BroadcastDeed(a, ActionType.ContactHeater, e.Id, OutcomeType.Injury, 0.8f, dv, dp, dc, 20f);
            PushLedger(a, e, ActionType.TouchProp, e.Id, 0, outcome, dv, dp, dc, KnowledgeId.HeaterHot, Cfg.ConfPersonalBurn, w);
            return;
        }

        Emit((ushort)a.Id, EventType.TouchProp, a.Pos, e.Id, 0, GameSeconds);

        // ---- 发明判定 ----
        TryInnovate(a, e);

        int wit = BroadcastDeed(a, ActionType.TouchProp, e.Id, OutcomeType.NoEffect, 0.15f, 0, 0, 0, 5f);
        PushLedger(a, e, ActionType.TouchProp, e.Id, 0, outcome, 0, 0, 0, KnowledgeId.None, 0, wit);
    }

    /// <summary>试探时的"发明"判定：学会一个此前不知道、且对该实体有效的动作。</summary>
    private void TryInnovate(Agent a, Entity e)
    {
        float p = InnovationBase * (0.5f + a.PCuriosity) * (0.5f + a.PBoldness);
        if (!a.Rng.Chance(p)) return;

        Span<ActionType> pool = stackalloc ActionType[5];
        int n = 0;
        switch (e.Type)
        {
            case EntityType.Socket:
                if (a.HeldId >= 0 && EntityById[a.HeldId].Type == EntityType.Nail) pool[n++] = ActionType.InsertNail;
                break;
            case EntityType.Heater:
                pool[n++] = ActionType.ContactHeater;
                pool[n++] = ActionType.PushObject;
                if (e.Plugged) pool[n++] = ActionType.TurnKnob;
                else pool[n++] = ActionType.PlugIn;
                break;
            case EntityType.Plank:
            case EntityType.Stick:
                if (a.HeldId >= 0 && EntityById[a.HeldId].Type == EntityType.Stick) pool[n++] = ActionType.Drill;
                break;
            case EntityType.Nail:
                if (a.HeldId >= 0) pool[n++] = ActionType.Throw;
                break;
        }
        if (n == 0) return;

        // 只在"还不会的"里面挑——否则一次成功的发明判定会被浪费在已知动作上
        Span<ActionType> unknown = stackalloc ActionType[5];
        int m = 0;
        for (int i = 0; i < n; i++)
            if (!a.KnowsAction(pool[i])) unknown[m++] = pool[i];
        if (m == 0) return;

        var act = unknown[a.Rng.RangeInt(0, m)];
        a.LearnAction(act);
        Emit((ushort)a.Id, EventType.BeliefGain, a.Pos, (int)act, e.Id, 1f, 0f);
    }

    /// <summary>
    /// 四步发现链的下一步"顺手就看见了"（design.md §2.5.2 H1→H4）。
    /// 推动电暖气的人会看见电源线；插上插头的人会看见侧面的旋钮。
    /// 没有这一步，链条就断了——500 个人也凑不齐四次独立的小概率发明。
    /// </summary>
    private void RevealNextStep(Agent a, Entity e, KnowledgeId reveal, ActionType action, float chance)
    {
        if (a.KnowsAction(action)) return;
        if (!a.Rng.Chance(chance * (0.6f + a.PCuriosity * 0.8f))) return;
        a.LearnAction(action);
        a.AddBelief(reveal, -1, 0.25f, BeliefSource.PersonalExperience, 0, Tick);
        Emit((ushort)a.Id, EventType.BeliefGain, a.Pos, (int)action, e.Id, 0.25f);
    }

    /// <summary>
    /// 铁钉插插座（design.md §2.4 / §3.7.2）。
    /// 孔位判定是纯几何的：白痴"想插自己右手边的孔"，
    /// 而插座自身也有一个"右手边"——两者在正常站位下一致，失误时则由犯错的概率决定。
    /// </summary>
    private void ResolveInsertNail(Agent a)
    {
        if (a.HeldId < 0 || EntityById[a.HeldId].Type != EntityType.Nail) return;
        var sock = EntityById[a.TargetId];
        if (sock == null || sock.Type != EntityType.Socket) return;

        if (sock.SState != SocketState.Empty)
        {
            var e0 = EntityById[a.HeldId];
            PushLedger(a, e0, ActionType.InsertNail, sock.Id, 0, OutcomeType.NoEffect, 0, 0, 0, KnowledgeId.None, 0, 0);
            return;
        }

        if (T_FirstInsertNail < 0) { T_FirstInsertNail = Tick; FirstInsertAgent = a.Id; }
        var nail = EntityById[a.HeldId];

        // 意图：相信哪一侧安全？没有知识则随机
        float leftSafe = a.ConfidenceOf(KnowledgeId.HoleLeftSafe);
        float rightDead = a.ConfidenceOf(KnowledgeId.HoleRightDeadly);
        int intended;
        if (leftSafe > rightDead + 0.1f) intended = -1;         // 自认左手边安全
        else if (rightDead > leftSafe + 0.1f) intended = +1;    // 自认右手边安全
        else intended = a.Rng.Chance(0.5f) ? 1 : -1;

        // 失误：越冷越抖，越疼越抖（§3.7.2 的核心耦合）
        float err = Cfg.BaseInsertError + (1f - a.Dexterity) * 0.66f;
        int actual = a.Rng.Chance(err) ? -intended : intended;

        // 白痴的"右手边"落到世界坐标
        var reachDir = a.Facing.Right * actual;
        bool hitsLiveHole = Vec2.Dot(reachDir, sock.SocketRight) > 0f;

        nail.Held = false; nail.HolderId = -1;
        a.HeldId = -1;
        nail.Pos = sock.Pos;
        sock.NailEntityId = nail.Id;

        if (hitsLiveHole)
        {
            sock.SState = SocketState.NailInLive;
            Emit((ushort)a.Id, EventType.InsertNailLethal, sock.Pos, sock.Id, actual, GameSeconds, err);
            Kill(a, DeathCause.Shock, sock.Id);
            BroadcastDeed(a, ActionType.InsertNail, sock.Id, OutcomeType.Death, 1.0f, -100f, 1f, 0f, 30f);
        }
        else
        {
            sock.SState = SocketState.NailInNeutral;
            Emit((ushort)a.Id, EventType.InsertNailSafe, sock.Pos, sock.Id, actual, GameSeconds, err);
            // 亲身体验成功：绑定到具体实例（泛化是慢的，§4.2）
            a.AddBelief(KnowledgeId.HoleLeftSafe, sock.Id, Cfg.ConfPersonalSuccess, BeliefSource.PersonalExperience, 0, Tick);
            int w = BroadcastDeed(a, ActionType.InsertNail, sock.Id, OutcomeType.Success, 0.4f, 0, 0, 0, 12f);
            PushLedger(a, nail, ActionType.InsertNail, sock.Id, actual, OutcomeType.Success, 0, 0, 0,
                       KnowledgeId.HoleLeftSafe, Cfg.ConfPersonalSuccess, w);
        }
    }

    private void ResolveContactHeater(Agent a)
    {
        var h = EntityById[a.TargetId];
        if (h == null || h.Type != EntityType.Heater) return;

        if (h.Running && h.SurfaceTemp > 60f)
        {
            ApplyInjury(a, InjuryType.Burn, Cfg.BurnDamage, Cfg.BurnPain, h.Id, "contact");
            a.PainSourceId = h.Id;
            a.BurnsSuffered++;
            BurnEvents++;
            if (T_FirstBurn < 0) { T_FirstBurn = Tick; FirstBurnAgent = a.Id; }
            a.AddBelief(KnowledgeId.HeaterHot, -1, Cfg.ConfPersonalBurn, BeliefSource.PersonalExperience, 0, Tick);
            Emit((ushort)a.Id, EventType.ContactHeater, a.Pos, h.Id, 0, GameSeconds);
            Emit((ushort)a.Id, EventType.Burn, a.Pos, h.Id, 0, Cfg.BurnDamage);
            DoCry(a);
            int w = BroadcastDeed(a, ActionType.ContactHeater, h.Id, OutcomeType.Injury, 0.8f,
                                  -Cfg.BurnDamage, Cfg.BurnPain, 0f, 20f);
            PushLedger(a, h, ActionType.ContactHeater, h.Id, 0, OutcomeType.Injury,
                       -Cfg.BurnDamage, Cfg.BurnPain, 0, KnowledgeId.HeaterHot, Cfg.ConfPersonalBurn, w);
        }
        else
        {
            Emit((ushort)a.Id, EventType.ContactHeater, a.Pos, h.Id, 0, GameSeconds);
            int w = BroadcastDeed(a, ActionType.ContactHeater, h.Id, OutcomeType.NoEffect, 0.2f, 0, 0, 0, 10f);
            PushLedger(a, h, ActionType.ContactHeater, h.Id, 0, OutcomeType.NoEffect, 0, 0, 0, KnowledgeId.None, 0, w);
            // 机器是凉的 —— 也是一种信息（但它很弱，且需要站在旁边才感觉得到温暖）
            a.AddBelief(KnowledgeId.HeaterWarm, h.Id, 0.05f, BeliefSource.PersonalExperience, 0, Tick);
        }
    }

    private void ResolvePlugIn(Agent a)
    {
        var h = EntityById[a.TargetId];
        if (h == null || h.Type != EntityType.Heater || h.Plugged) return;
        int sid = NearestEmptySocketTo(h.Pos, Cfg.HeaterPowerCordLength);
        if (sid < 0) return;

        var s = EntityById[sid];
        h.Plugged = true;
        h.PluggedSocket = sid;
        s.SState = SocketState.PluggedIn;
        s.PluggedHeater = h.Id;

        a.AddBelief(KnowledgeId.HeaterPlugFits, -1, Cfg.ConfPersonalSuccess * 0.7f, BeliefSource.PersonalExperience, 0, Tick);
        Emit((ushort)a.Id, EventType.HeaterPlug, a.Pos, h.Id, sid, GameSeconds);
        if (T_FirstHeaterPlug < 0) T_FirstHeaterPlug = Tick;

        // H3：插好插头，一抬头看见侧面有个旋钮
        RevealNextStep(a, h, KnowledgeId.HeaterKnobExists, ActionType.TurnKnob, 0.40f);
        int w = BroadcastDeed(a, ActionType.PlugIn, h.Id, OutcomeType.Success, 0.3f, 0, 0, 0, 10f);
        PushLedger(a, h, ActionType.PlugIn, h.Id, sid, OutcomeType.Success, 0, 0, 0,
                   KnowledgeId.HeaterPlugFits, Cfg.ConfPersonalSuccess * 0.7f, w);
    }

    private void ResolveTurnKnob(Agent a)
    {
        var h = EntityById[a.TargetId];
        if (h == null || h.Type != EntityType.Heater) return;
        if (!h.Plugged || h.Broken) return;

        h.Knob = (h.Knob + 1) % 3;
        Emit((ushort)a.Id, EventType.HeaterKnob, a.Pos, h.Id, h.Knob, GameSeconds);
        if (T_FirstHeaterKnob < 0) T_FirstHeaterKnob = Tick;

        var outcome = OutcomeType.NoEffect;
        float dc = 0f;
        if (h.Knob >= 1)
        {
            outcome = OutcomeType.Warm;
            dc = Cfg.HeaterLocalDeltaT / 20f;
            if (T_FirstHeaterOn < 0) FirstHeaterAgent = a.Id;
            Emit((ushort)a.Id, EventType.HeaterStart, a.Pos, h.Id, h.Knob, GameSeconds);
            Emit(0, EventType.Milestone, h.Pos, (int)MilestoneId.HeaterOn, a.Id, GameSeconds);
        }
        a.AddBelief(KnowledgeId.HeaterKnobExists, -1, Cfg.ConfPersonalSuccess * 0.6f, BeliefSource.PersonalExperience, 0, Tick);
        if (h.Knob >= 1)
            a.AddBelief(KnowledgeId.HeaterWarm, -1, Cfg.ConfPersonalWarm, BeliefSource.PersonalExperience, 0, Tick);

        int w = BroadcastDeed(a, ActionType.TurnKnob, h.Id,
                              h.Knob >= 1 ? OutcomeType.Warm : OutcomeType.NoEffect,
                              h.Knob >= 1 ? 0.85f : 0.2f, 0, 0, dc, h.Knob >= 1 ? 30f : 8f);
        PushLedger(a, h, ActionType.TurnKnob, h.Id, h.Knob, outcome, 0, 0, dc,
                   KnowledgeId.HeaterKnobExists, Cfg.ConfPersonalSuccess * 0.6f, w);
    }

    private void ResolvePushObject(Agent a)
    {
        var h = EntityById[a.TargetId];
        if (h == null || h.Type != EntityType.Heater || h.Plugged || h.Broken) return;

        int sid = a.TargetArg;
        if (sid < 0 || sid >= EntityById.Length || EntityById[sid] == null)
            sid = NearestSocketAnywhere(h.Pos, 12f);
        if (sid < 0) return;

        var sp = EntityById[sid].Pos;
        var dir = (sp - h.Pos);
        float d = dir.Length;
        if (d < 0.05f) return;
        float step = Math.Min(Cfg.HeaterPushPerAction, d);
        h.Pos += dir / d * step;
        h.Yaw = MathF.Atan2(dir.X, dir.Z);
        MarkEntityMoved();

        a.AddBelief(KnowledgeId.HeaterPush, -1, Cfg.ConfPersonalSuccess * 0.5f, BeliefSource.PersonalExperience, 0, Tick);
        Emit((ushort)a.Id, EventType.HeaterPush, h.Pos, h.Id, sid, step, Vec2.Dist(h.Pos, sp));
        if (T_FirstHeaterPush < 0) T_FirstHeaterPush = Tick;

        // 推进水池 = 永久损坏（§12.2 Q6）
        if (MathF.Abs(h.Pos.X) < Cfg.PoolHalf && MathF.Abs(h.Pos.Z) < Cfg.PoolHalf)
        {
            h.Broken = true; h.Running = false; h.Plugged = false;
            if (h.PluggedSocket >= 0 && EntityById[h.PluggedSocket] != null)
            {
                EntityById[h.PluggedSocket].SState = SocketState.Empty;
                EntityById[h.PluggedSocket].PluggedHeater = -1;
                h.PluggedSocket = -1;
            }
            Emit(0, EventType.HeaterFallIntoPool, h.Pos, h.Id, 0, GameSeconds);
        }

        // H2：推着推着，看见机器拖着一根线
        RevealNextStep(a, h, KnowledgeId.HeaterPlugFits, ActionType.PlugIn, 0.30f);

        int w = BroadcastDeed(a, ActionType.PushObject, h.Id, OutcomeType.Success, 0.25f, 0, 0, 0, 8f);
        PushLedger(a, h, ActionType.PushObject, h.Id, sid, OutcomeType.Success, 0, 0, 0,
                   KnowledgeId.HeaterPush, Cfg.ConfPersonalSuccess * 0.5f, w);
    }

    public int DiagResolveDrill, DiagDrillNoStick, DiagDrillNoPlank;

    private void ResolveDrill(Agent a)
    {
        DiagResolveDrill++;
        if (a.HeldId < 0 || EntityById[a.HeldId].Type != EntityType.Stick) { DiagDrillNoStick++; return; }
        var plank = EntityById[a.TargetId];
        if (plank == null || plank.Type != EntityType.Plank) { DiagDrillNoPlank++; return; }

        // 进度按**动作时长**累加（一次搓 5 游戏秒）。早先写成 BrainEveryTicks*TickSeconds
        // 只加 1 秒，等于要求连续搓 45 次——没有哪个 3 岁小孩会这么干，G2 因此永远到不了。
        a.DrillProgress += Cfg.DrillSecondsPerAction;
        if (a.DrillProgress > MaxDrillProgress) MaxDrillProgress = a.DrillProgress;
        a.AddBelief(KnowledgeId.StickPlankTogether, -1, 0.08f, BeliefSource.PersonalExperience, 0, Tick);
        a.AddBelief(KnowledgeId.DrillingMotion, -1, Cfg.ConfImitate * 2f, BeliefSource.PersonalExperience, 0, Tick);
        Emit((ushort)a.Id, EventType.DrillTick, a.Pos, plank.Id, 0, a.DrillProgress);

        // "搓"这个动作是可以看会的——旁观者看到的是一个人拿着棍子在木板上使劲蹭。
        // 但**不能每 tick 都广播**：动作被改成 1 tick 一次之后，这一行把事件日志
        // 从 3 万条炸到 12 万条。改成每累计 DrillBroadcastEvery 游戏秒广播一次。
        int bandBefore = (int)((a.DrillProgress - Cfg.DrillSecondsPerAction) / Cfg.DrillBroadcastEvery);
        int bandNow = (int)(a.DrillProgress / Cfg.DrillBroadcastEvery);
        int wit = 0;
        if (bandNow > bandBefore)
        {
            wit = BroadcastDeed(a, ActionType.Drill, plank.Id, OutcomeType.NoEffect,
                                0.25f + 0.4f * Math.Min(1f, a.DrillProgress / 30f), 0, 0, 0, 6f);
            PushLedger(a, plank, ActionType.Drill, plank.Id, 0, OutcomeType.NoEffect,
                       0, 0, 0, KnowledgeId.DrillingMotion, Cfg.ConfImitate * 2f, wit);
        }

        if (a.DrillProgress < Cfg.DrillRequiredTicks * Cfg.TickSeconds) return;

        // 屋里已经有火了。这里**必须把进度清掉**——早先 StartFire 直接 return，
        // 进度就一直挂在那儿，搓成过的人从此蹲在墙角搓到 11150 秒，G2 之后
        // 再也没有第二个结局，只有一条无限长的钻木流水线。
        if (FireBurning) { a.DrillProgress = 0f; return; }

        // 成功！需要附近有杂草作引火物
        int weed = NearestOfType(a, EntityType.Weed, Cfg.FireFuelRadius);
        if (weed < 0) { a.DrillProgress = 0f; DrillNoFuel++; return; }

        StartFire(a, plank.Pos, weed);
    }

    private void StartFire(Agent a, Vec2 pos, int weedId)
    {
        if (FireBurning) return;
        var weed = EntityById[weedId];
        weed.Held = false; weed.HolderId = -1;

        var fire = new Entity
        {
            Id = Entities.Count + 1,
            Type = EntityType.Fire,
            Index = 1,
            Pos = pos,
            Height = 0.4f,
        };
        Entities.Add(fire);
        var newById = new Entity[Entities.Count + 1];
        Array.Copy(EntityById, newById, EntityById.Length);
        newById[fire.Id] = fire;
        EntityById = newById;

        FireEntityId = fire.Id;
        FireBurning = true;
        FireFuel = Cfg.FireBurnSeconds;

        a.AddBelief(KnowledgeId.FireWarm, -1, Cfg.ConfWarmReinforce * 6f, BeliefSource.PersonalExperience, 0, Tick);
        Emit((ushort)a.Id, EventType.DrillSuccess, pos, a.Id, 0, GameSeconds);
        Emit((ushort)a.Id, EventType.FireStart, pos, fire.Id, 0, GameSeconds);
        Emit(0, EventType.Milestone, pos, (int)MilestoneId.Fire, a.Id, GameSeconds);
        if (T_FirstFire < 0) { T_FirstFire = Tick; FirstFireAgent = a.Id; }

        BroadcastDeed(a, ActionType.Drill, fire.Id, OutcomeType.Warm, 0.95f, 0, 0, 0.8f, 45f);
        a.DrillProgress = 0f;
    }

    private void ResolvePushAgent(Agent a)
    {
        if (a.TargetId <= 0 || a.TargetId > Agents.Count) return;
        var b = Agents[a.TargetId - 1];
        if (!b.Alive) return;

        var dir = (b.Pos - a.Pos);
        if (dir.LengthSq < 1e-5f) dir = a.Facing;
        b.Pos += dir.Normalized * 0.9f;
        b.Pain = Math.Min(1f, b.Pain + Cfg.PushPain);
        b.PushedUntilTick = Tick + Cfg.SecondsToTicks(2.5f);   // 踉跄：池边站不住
        float lim = Cfg.RoomHalf - 0.4f;
        b.Pos = new Vec2(Math.Clamp(b.Pos.X, -lim, lim), Math.Clamp(b.Pos.Z, -lim, lim));

        Emit((ushort)a.Id, EventType.PushAgent, a.Pos, b.Id, 0, GameSeconds);
        PushLedger(a, null, ActionType.PushAgent, b.Id, 0, OutcomeType.Success, 0, 0, 0, KnowledgeId.None, 0, 0);
        // 被推者记录这次经历
        b.AddBelief(KnowledgeId.None, -1, 0f, BeliefSource.PersonalExperience, 0, Tick);
    }

    private void DoCry(Agent a)
    {
        Emit((ushort)a.Id, EventType.AttentionGrab, a.Pos, 0, 0, 0.8f);
        BroadcastDeed(a, ActionType.Cry, -1, OutcomeType.Startled, 0.6f, 0, 0, 0, 15f);
    }

    // =====================================================================
    //  尝试记录写入（design.md §6.5）
    // =====================================================================
    private void PushLedger(Agent a, Entity prop, ActionType act, int targetId, int arg,
                            OutcomeType oc, float dv, float dp, float dc,
                            KnowledgeId kid, float dconf, int witnesses)
    {
        a.PushAttempt(new AttemptEntry
        {
            Tick = Tick,
            Prop = prop?.Type ?? EntityType.Socket,
            InstanceId = prop?.Id ?? targetId,
            InstanceIndex = prop?.Index ?? 0,
            Action = act,
            TargetId = targetId,
            TargetArg = arg,
            Outcome = oc,
            DVitality = dv,
            DPain = dp,
            DComfort = dc,
            LearnedId = kid,
            DConfidence = dconf,
            WitnessCount = witnesses,
        });
    }
}
