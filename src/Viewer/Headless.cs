using System.Diagnostics;
using System.Text;
using IdiotSim;
using IdiotSim.Core;
using IdiotSim.Knowledge;
using IdiotSim.Recording;

namespace IdiotSim.Viewer;

/// <summary>
/// 无头批量验证（design.md §10.2）。不依赖任何 UI，用于快速调参与验收。
/// </summary>
public static class Headless
{
    public static int Run(string[] args)
    {
        float minutes = 24f * 60f;
        int seed = 20261003;
        bool quiet = false;
        float poolHalf = -1f;      // 调参用：--pool 0.01 等于把水池拿掉，看分布会不会散开
        bool noProps = false;      // 调参用：--noprops 把木板木棍铁钉杂草全部失效

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--minutes" && i + 1 < args.Length) float.TryParse(args[++i], out minutes);
            else if (args[i] == "--seed" && i + 1 < args.Length) int.TryParse(args[++i], out seed);
            else if (args[i] == "--quiet") quiet = true;
            else if (args[i] == "--pool" && i + 1 < args.Length) float.TryParse(args[++i], out poolHalf);
            else if (args[i] == "--noprops") noProps = true;
        }

        var cfg = SimConfig.Default;
        cfg.WorldSeed = seed;
        if (poolHalf >= 0f) cfg.PoolHalf = poolHalf;

        var sw = Stopwatch.StartNew();
        var sim = new Simulation(cfg);
        if (noProps)
            foreach (var e in sim.Entities)
                if (e.IsProp) e.InPool = true;      // 借 InPool 把道具从感知里摘掉
        double buildMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        float gameSeconds = minutes * 60f;
        int ticks = cfg.SecondsToTicks(gameSeconds);
        for (int i = 0; i < ticks; i++) sim.Step();
        double simMs = sw.Elapsed.TotalMilliseconds;

        var sb = new StringBuilder();
        sb.AppendLine("==============================================================");
        sb.AppendLine($" 500 个白痴 —— 无头仿真报告   seed={seed}   时长={minutes:0} 游戏分钟");
        sb.AppendLine("==============================================================");
        sb.AppendLine($"构建 {buildMs:0} ms    仿真 {simMs:0} ms    tick={sim.Tick}    {simMs / Math.Max(1, sim.Tick) * 1000:0.0} µs/tick");

        sb.AppendLine();
        sb.AppendLine("---- 目标 KPI（design.md §10.1）----");
        K(sb, cfg, "G1  T_firstHeaterOn   首台电暖气启动", sim.T_FirstHeaterOn);
        K(sb, cfg, "    · 首次推动          ", sim.T_FirstHeaterPush);
        K(sb, cfg, "    · 首次插头入座      ", sim.T_FirstHeaterPlug);
        K(sb, cfg, "    · 首次拧旋钮        ", sim.T_FirstHeaterKnob);
        K(sb, cfg, "G2  T_firstFire       首次钻木取火", sim.T_FirstFire);
        K(sb, cfg, "G3  T_comfort         全屋舒适", sim.T_Comfort);
        sb.AppendLine();
        K(sb, cfg, "    T_firstBurn       首次烫伤  ", sim.T_FirstBurn);
        K(sb, cfg, "    T_firstInsertNail 首次插铁钉", sim.T_FirstInsertNail);
        K(sb, cfg, "    T_firstDeath      首次死亡  ", sim.T_FirstDeath);
        K(sb, cfg, "    T_fearCross       K_warm 反超 K_hot", sim.T_FearCross);

        sb.AppendLine();
        sb.AppendLine("---- 结局统计 ----");
        sb.AppendLine($"存活 {sim.AliveCount} / {cfg.AgentCount}     死亡 {cfg.AgentCount - sim.AliveCount}");
        sb.AppendLine($"  溺亡 {sim.DeathsDrown}   触电 {sim.DeathsShock}   伤重 {sim.DeathsInjury}");
        sb.AppendLine($"烫伤事件 {sim.BurnEvents}     舒适度震荡 {sim.ComfortOscillations}");
        sb.AppendLine($"全局温度 {sim.GlobalTemp:0.00} °C     事件 {sim.Events.Count} 条");

        sb.AppendLine();
        sb.AppendLine($"钻木取火：全屋最大搓动进度 {sim.MaxDrillProgress:0} / 需要 {cfg.DrillRequiredTicks * cfg.TickSeconds:0} 游戏秒");
        sb.AppendLine($"          搓够了但附近没有引火物而白搓 {sim.DrillNoFuel} 次");
        sb.AppendLine($"          诊断：手持木棍 {sim.DiagHeldStick} 次 / 其中会搓 {sim.DiagHeldStickKnowsDrill} 次" +
                      $" / 生成 Drill 候选 {sim.DiagDrillCand} 次 / 被选中 {sim.DiagDrillChosen} 次");
        sb.AppendLine($"          结算 Drill {sim.DiagResolveDrill} 次：没拿棍 {sim.DiagDrillNoStick} / 目标不是木板 {sim.DiagDrillNoPlank}");
        sb.AppendLine($"          Act: 进入Drill {sim.DiagActDrill} / 还太远 {sim.DiagActDrillFar} / 计时 {sim.DiagActDrillTick} / 触发结算 {sim.DiagActDrillFire}");
        sb.AppendLine($"          赶路承诺：保持 {sim.DiagDrillCommitKept} / 超时放弃 {sim.DiagDrillCommitTimeout} / 目标失效 {sim.DiagDrillCommitInvalid}");
        sb.AppendLine($"          撞上水池止步线被挡住：最后 1 tick {sim.DiagPoolBlockNow} 人 / 峰值 {sim.DiagPoolBlockMax} 人");
        sb.AppendLine("---- 事件直方图（前 18 类）----");
        var hist = new Dictionary<EventType, int>();
        for (int i = 0; i < sim.Events.Count; i++)
        {
            var t = sim.Events.At(i).Type;
            hist[t] = hist.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        foreach (var kv in hist.OrderByDescending(k => k.Value).Take(18))
            sb.AppendLine($"  {kv.Key,-22} {kv.Value,8}");

        sb.AppendLine();
        sb.AppendLine("---- 发现链传播：会做这个动作的人（存活）----");
        sb.AppendLine($"  推电暖气 PushObject   {sim.CountKnowingAction(ActionType.PushObject),4}");
        sb.AppendLine($"  插插头   PlugIn       {sim.CountKnowingAction(ActionType.PlugIn),4}");
        sb.AppendLine($"  拧旋钮   TurnKnob     {sim.CountKnowingAction(ActionType.TurnKnob),4}");
        sb.AppendLine($"  摸电暖气 ContactHeater{sim.CountKnowingAction(ActionType.ContactHeater),4}");
        sb.AppendLine($"  插铁钉   InsertNail   {sim.CountKnowingAction(ActionType.InsertNail),4}");
        sb.AppendLine($"  钻木取火 Drill        {sim.CountKnowingAction(ActionType.Drill),4}");
        var (hp, hpl, hr, hb) = sim.HeaterState();
        sb.AppendLine($"  电暖气（共 {sim.HeaterIds.Count} 台）：够得着插座 {hp} / 已插电 {hpl} / 运转中 {hr} / 报废 {hb}");

        sb.AppendLine();
        sb.AppendLine("---- 此刻在干什么（存活者）----");
        var act = new Dictionary<ActionType, int>();
        int still = 0, ring = 0;
        float m2 = cfg.PoolHalf + 1.6f;
        for (int i = 0; i < sim.Agents.Count; i++)
        {
            var a = sim.Agents[i];
            if (!a.Alive) continue;
            act[a.Action] = act.TryGetValue(a.Action, out var c0) ? c0 + 1 : 1;
            if (!a.HasMoveTarget) still++;
            if (MathF.Abs(a.Pos.X) < m2 && MathF.Abs(a.Pos.Z) < m2) ring++;
        }
        foreach (var kv in act.OrderByDescending(k => k.Value).Take(10))
            sb.AppendLine($"  {kv.Key,-16} {kv.Value,4}");
        sb.AppendLine($"  站住不动 {still} 人     池沿 1.6m 环带内 {ring} 人");

        sb.AppendLine();
        sb.AppendLine("---- 空间分布（10x10 网格，每格 10m）----");
        {
            var occ = new int[10, 10];
            for (int i = 0; i < sim.Agents.Count; i++)
            {
                var a = sim.Agents[i];
                if (!a.Alive) continue;
                int gx = Math.Clamp((int)((a.Pos.X + 50f) / 10f), 0, 9);
                int gz = Math.Clamp((int)((a.Pos.Z + 50f) / 10f), 0, 9);
                occ[gz, gx]++;
            }
            for (int z = 9; z >= 0; z--)
            {
                sb.Append("  ");
                for (int x = 0; x < 10; x++) sb.Append($"{occ[z, x],4}");
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("---- 抽样个体（每 25 人取 1）----");
        for (int i = 0; i < sim.Agents.Count; i += 25)
        {
            var a = sim.Agents[i];
            if (!a.Alive) { sb.AppendLine($"  #{a.Id,-4} 已死"); continue; }
            string tn = "—";
            float td = -1f;
            if (a.TargetIsAgent && a.TargetId > 0 && a.TargetId <= sim.Agents.Count)
            { tn = "白痴"; td = Vec2.Dist(a.Pos, sim.Agents[a.TargetId - 1].Pos); }
            else if (!a.TargetIsAgent && a.TargetId > 0 && a.TargetId < sim.EntityById.Length && sim.EntityById[a.TargetId] != null)
            { tn = sim.EntityById[a.TargetId].Type.ToString(); td = Vec2.Dist(a.Pos, sim.EntityById[a.TargetId].Pos); }
            sb.AppendLine($"  #{a.Id,-4} ({a.Pos.X,6:0.0},{a.Pos.Z,6:0.0}) {a.Action,-14} 目标={tn,-8} 距离={td,6:0.0} " +
                          $"移动中={(a.HasMoveTarget ? "是" : "否")} 舒适={a.Comfort:0.00} 体温={a.CoreTemp:0.0}");
        }

        sb.AppendLine();
        sb.AppendLine("---- 触电死亡按插座分布（一个插座杀太多人 = 死亡陷阱，不是剧情）----");
        {
            var bySock = new Dictionary<int, int>();
            for (int i = 0; i < sim.Events.Count; i++)
            {
                var e = sim.Events.At(i);
                if (e.Type != EventType.DeathShock) continue;
                bySock[e.TargetA] = bySock.TryGetValue(e.TargetA, out var c) ? c + 1 : 1;
            }
            foreach (var kv in bySock.OrderByDescending(k => k.Value).Take(8))
            {
                var s = kv.Key > 0 && kv.Key < sim.EntityById.Length ? sim.EntityById[kv.Key] : null;
                string where = s == null ? "?" : $"({s.Pos.X,6:0.0},{s.Pos.Z,6:0.0})";
                // 现在的状态：钉子还在不在。钉子还在 = 看得见的陷阱（剧情）；
                // 钉子没了却仍是 NailInLive = 隐形绞肉机（状态没同步的 bug）。
                string now = s == null ? "" :
                    s.SState == SocketState.NailInLive
                        ? (s.NailEntityId >= 0 && sim.EntityById[s.NailEntityId] != null
                            ? "  现仍带电：钉子还在（看得见）"
                            : "  现仍带电：**钉子不见了** ← bug")
                        : $"  现已安全（{s.SState}）";
                sb.AppendLine($"  插座#{kv.Key,-5} 杀了 {kv.Value,4} 人   {where}{now}");
            }
            sb.AppendLine($"  合计：{bySock.Count} 个插座杀过人，共 {sim.DeathsShock} 起触电死亡");
        }

        sb.AppendLine();
        sb.AppendLine("---- 知识持有者数（阈值 0.5）----");
        sb.AppendLine($"  HeaterPush      推得动       {sim.CountKnowing(KnowledgeId.HeaterPush)}");
        sb.AppendLine($"  HeaterPlugFits  插头能插     {sim.CountKnowing(KnowledgeId.HeaterPlugFits)}");
        sb.AppendLine($"  HeaterKnobExists 旋钮存在    {sim.CountKnowing(KnowledgeId.HeaterKnobExists)}");
        sb.AppendLine($"  HeaterWarm      开着会暖     {sim.CountKnowing(KnowledgeId.HeaterWarm)}");
        sb.AppendLine($"  HeaterHot       开着会烫     {sim.CountKnowing(KnowledgeId.HeaterHot)}");
        sb.AppendLine($"  HoleLeftSafe    左孔安全     {sim.CountKnowing(KnowledgeId.HoleLeftSafe)}");
        sb.AppendLine($"  HoleRightDeadly 右孔致命     {sim.CountKnowing(KnowledgeId.HoleRightDeadly)}");
        sb.AppendLine($"  NailDangerous   铁钉危险     {sim.CountKnowing(KnowledgeId.NailDangerous)}");
        sb.AppendLine($"  WaterDanger     水会淹死人   {sim.CountKnowing(KnowledgeId.WaterDanger)}");
        sb.AppendLine($"  DrillingMotion  搓           {sim.CountKnowing(KnowledgeId.DrillingMotion)}");
        sb.AppendLine($"  FireWarm        火很暖和     {sim.CountKnowing(KnowledgeId.FireWarm)}");

        if (!quiet)
        {
            sb.AppendLine();
            sb.AppendLine("---- 温度曲线（每 10 游戏分钟采样）----");
            int step = Math.Max(1, sim.TempCurve.Count / 60);
            for (int i = 0; i < sim.TempCurve.Count; i += step)
            {
                var (tick, temp) = sim.TempCurve[i];
                sb.AppendLine($"  {Simulation.FormatGameTime(tick, cfg)}  {temp,6:0.00} °C  {Bar(temp, 15f, 30f)}");
            }

            sb.AppendLine();
            sb.AppendLine("---- 知识扩散（每 30 游戏分钟采样）----");
            sb.AppendLine("   时间      warm  hot  deadly water fire");
            int kstep = Math.Max(1, sim.KnowledgeCurve.Count / 48);
            for (int i = 0; i < sim.KnowledgeCurve.Count; i += kstep)
            {
                var (tick, warm, hot, deadly, water, fire) = sim.KnowledgeCurve[i];
                sb.AppendLine($"  {Simulation.FormatGameTime(tick, cfg)}  {warm,4} {hot,4} {deadly,6} {water,5} {fire,5}");
            }

            sb.AppendLine();
            sb.AppendLine("---- 里程碑事件流（前 40 条）----");
            int shown = 0;
            for (int i = 0; i < sim.Events.Count && shown < 40; i++)
            {
                var e = sim.Events.At(i);
                if (e.Type != EventType.Milestone && e.Type != EventType.DeathShock
                    && e.Type != EventType.DeathDrown && e.Type != EventType.FireStart
                    && e.Type != EventType.HeaterStart && e.Type != EventType.ComfortReached) continue;
                sb.AppendLine($"  {Simulation.FormatGameTime(e.Tick, cfg)}  {e.Type,-16} agent={e.AgentId,-4} @({e.Pos.X,6:0.0},{e.Pos.Z,6:0.0})");
                shown++;
            }
        }

        Console.Write(sb.ToString());
        try { File.WriteAllText("headless-report.txt", sb.ToString(), Encoding.UTF8); } catch { }
        return 0;
    }

    private static void K(StringBuilder sb, SimConfig cfg, string label, int tick)
        => sb.AppendLine($"  {label}  {(tick < 0 ? "（未发生）" : Simulation.FormatGameTime(tick, cfg))}");

    private static string Bar(float v, float lo, float hi)
    {
        int n = (int)Math.Clamp((v - lo) / (hi - lo) * 40f, 0f, 40f);
        return new string('#', n);
    }
}
