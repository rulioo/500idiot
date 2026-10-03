using System.Drawing.Drawing2D;
using IdiotSim;
using IdiotSim.Agents;
using IdiotSim.Knowledge;
using IdiotSim.Recording;

namespace IdiotSim.Viewer;

/// <summary>
/// 个体检视面板（design.md §7.3）：状态 / 身体状况 / 道具尝试记录。
/// 面板显示的是**当前时间轴时刻的切片**——拖时间轴时，这个白痴的履历会一行行长出来。
/// </summary>
public sealed class InspectorPanel : UserControl
{
    private readonly SimRunner _r;
    private int _id = -1;
    private readonly ListView _ledger;
    private readonly BodyBox _body;
    private readonly TabControl _tabs;
    private readonly Label _header;

    public event Action<int>? OnSeekToTick;

    public int AgentId
    {
        get => _id;
        set { _id = value; Refresh2(); }
    }

    public InspectorPanel(SimRunner r)
    {
        _r = r;
        BackColor = Color.FromArgb(24, 26, 32);
        ForeColor = Color.FromArgb(225, 230, 240);
        Width = 420;
        Dock = DockStyle.Right;

        _header = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            ForeColor = Color.FromArgb(140, 235, 255),
            BackColor = Color.FromArgb(18, 20, 26),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            Text = "个体检视　—　准星对准某人后按 E 或左键锁定",
        };

        _body = new BodyBox(r) { Dock = DockStyle.Fill };

        _ledger = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BackColor = Color.FromArgb(20, 22, 28),
            ForeColor = Color.FromArgb(220, 226, 236),
            Font = new Font("Consolas", 8.6f),
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.None,
            OwnerDraw = false,
        };
        _ledger.Columns.Add("时间", 62);
        _ledger.Columns.Add("道具", 74);
        _ledger.Columns.Add("动作", 78);
        _ledger.Columns.Add("结果", 66);
        _ledger.Columns.Add("身体/认知", 130);
        _ledger.DoubleClick += (_, _) =>
        {
            if (_ledger.SelectedItems.Count == 0) return;
            if (_ledger.SelectedItems[0].Tag is int tk) OnSeekToTick?.Invoke(tk);
        };

        var bodyPage = new Panel { BackColor = Color.FromArgb(24, 26, 32) };
        bodyPage.Controls.Add(_body);

        var ledgerPage = new Panel { BackColor = Color.FromArgb(24, 26, 32) };
        ledgerPage.Controls.Add(_ledger);

        _tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9f) };
        _tabs.TabPages.Add(new TabPage("身体") { BackColor = Color.FromArgb(24, 26, 32) });
        _tabs.TabPages.Add(new TabPage("尝试记录") { BackColor = Color.FromArgb(24, 26, 32) });
        _tabs.TabPages[0].Controls.Add(bodyPage);
        _tabs.TabPages[1].Controls.Add(ledgerPage);

        Controls.Add(_tabs);
        Controls.Add(_header);
    }

    /// <summary>时间轴变化 / 锁定对象变化时调用。</summary>
    public void Refresh2()
    {
        if (InvokeRequired) { BeginInvoke(Refresh2); return; }
        int tick = _r.ViewTick;

        if (_id <= 0 || _id > _r.Cfg.AgentCount)
        {
            _header.Text = "个体检视　—　准星对准某人后按 E 或左键锁定";
            _body.SetAgent(-1, tick);
            _ledger.Items.Clear();
            return;
        }

        var a = _r.Sim.Agents[_id - 1];
        bool dead = _r.IsDeadAt(_id, tick, out var cause);
        string st = dead ? $"☠ 已死亡（{DeathName(cause)}）" : SceneView.EmoName(a.Emo);
        _header.Text = $"白痴 #{_id}　{st}";

        _body.SetAgent(_id, tick);
        RebuildLedger(tick);
    }

    private static string DeathName(DeathCause c) => c switch
    {
        DeathCause.Drown => "溺水", DeathCause.Shock => "触电", DeathCause.Injury => "伤重", _ => "—",
    };

    private void RebuildLedger(int tick)
    {
        _ledger.BeginUpdate();
        _ledger.Items.Clear();
        var rows = _r.AttemptsUpTo(_id, tick, 300);
        foreach (var e in rows)
        {
            var it = new ListViewItem(Simulation.FormatGameTime(e.Tick, _r.Cfg));
            it.SubItems.Add(PropLabel(e));
            it.SubItems.Add(ActionName((ActionType)e.TargetB));
            it.SubItems.Add(OutcomeName(e));
            it.SubItems.Add(Detail(e));
            it.Tag = e.Tick;
            if (e.Type is EventType.DeathDrown or EventType.DeathShock or EventType.DeathInjury)
            { it.ForeColor = Color.FromArgb(255, 120, 120); it.BackColor = Color.FromArgb(48, 20, 22); }
            else if (e.Type is EventType.Burn or EventType.ContactHeater)
                it.ForeColor = Color.FromArgb(255, 190, 120);
            else if (e.Type is EventType.HeaterStart or EventType.FireStart or EventType.DrillSuccess)
            { it.ForeColor = Color.FromArgb(150, 255, 200); it.BackColor = Color.FromArgb(18, 44, 30); }
            else if (e.Type is EventType.InsertNailSafe)
                it.ForeColor = Color.FromArgb(150, 230, 160);
            _ledger.Items.Add(it);
        }
        _ledger.EndUpdate();
    }

    private string PropLabel(in EventRecord e)
    {
        if (e.TargetA <= 0 || e.TargetA >= _r.Sim.EntityById.Length) return "—";
        var ent = _r.Sim.EntityById[e.TargetA];
        if (ent == null) return "—";
        string n = ent.Type switch
        {
            EntityType.Heater => "电暖气", EntityType.Socket => "插座", EntityType.Nail => "铁钉",
            EntityType.Plank => "木板", EntityType.Stick => "木棍", EntityType.Weed => "杂草",
            EntityType.Fire => "火", _ => "?",
        };
        return $"{n}#{ent.Index}";
    }

    public static string ActionName(ActionType a) => a switch
    {
        ActionType.Idle => "发呆", ActionType.Wander => "乱走", ActionType.Approach => "走近",
        ActionType.Grab => "拿起", ActionType.DropHeld => "丢下", ActionType.Throw => "扔出",
        ActionType.TouchProp => "试探", ActionType.InsertNail => "插铁钉",
        ActionType.ContactHeater => "摸外壳", ActionType.PlugIn => "插插头",
        ActionType.TurnKnob => "转旋钮", ActionType.PushObject => "推动",
        ActionType.Drill => "钻木", ActionType.PushAgent => "推人",
        ActionType.Cry => "哭", ActionType.Flee => "逃跑", ActionType.Rest => "歇着",
        _ => a.ToString(),
    };

    private static string OutcomeName(in EventRecord e) => e.Type switch
    {
        EventType.Grab => "拿到",
        EventType.Drop => "丢下",
        EventType.Throw => "扔出",
        EventType.TouchProp => "没反应",
        EventType.InsertNailSafe => "存活",
        EventType.InsertNailLethal => "☠ 触电",
        EventType.ContactHeater => "烫伤",
        EventType.Burn => "烫伤",
        EventType.HeaterPush => "推动",
        EventType.HeaterPlug => "插上",
        EventType.HeaterKnob => "转动",
        EventType.HeaterStart => "★ 启动",
        EventType.DrillTick => "搓…",
        EventType.DrillSuccess => "★ 起火",
        EventType.FireStart => "★ 起火",
        EventType.EnterPool => "落水",
        EventType.DeathDrown => "☠ 溺亡",
        EventType.DeathShock => "☠ 触电",
        EventType.DeathInjury => "☠ 伤重",
        EventType.PushAgent => "推人",
        _ => "—",
    };

    private static string Detail(in EventRecord e) => e.Type switch
    {
        EventType.Burn => $"生命 {e.Value:0.#}",
        EventType.ContactHeater => $"外壳 {e.Value:0}°C",
        EventType.DrillTick => $"进度 {e.Value:0}s",
        EventType.InsertNailSafe => e.TargetB < 0 ? "左孔" : "右孔",
        EventType.InsertNailLethal => e.TargetB < 0 ? "左孔" : "右孔",
        EventType.HeaterKnob => $"档位 → {e.TargetB}",
        EventType.HeaterStart => "开始发热",
        EventType.PushAgent => "推搡",
        _ => "",
    };

    // =====================================================================
    //  身体状况（§3.7 / §7.3）
    // =====================================================================

    private sealed class BodyBox : Control
    {
        private readonly SimRunner _r;
        private int _id = -1, _tick;

        public BodyBox(SimRunner r)
        {
            _r = r;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(24, 26, 32);
            Font = new Font("Consolas", 9f);
        }

        public void SetAgent(int id, int tick) { _id = id; _tick = tick; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            if (_id <= 0 || _id > _r.Cfg.AgentCount)
            {
                g.DrawString("未锁定任何白痴", Font, Brushes.Gray, 12, 12);
                return;
            }

            var a = _r.Sim.Agents[_id - 1];
            var bs = _r.GetBody(_id, _tick);
            bool dead = _r.IsDeadAt(_id, _tick, out var cause);

            float y = 10;
            float W = Width - 24;

            // 个体参数（静态，不随时间变化）
            Line(g, ref y, $"状态: {SceneView.EmoName(a.Emo)}    情绪: {a.Emo}    舒适度: {a.Comfort:0.00}");
            Line(g, ref y, $"个体参数: 好奇{a.PCuriosity:0.00} 胆大{a.PBoldness:0.00} 模仿{a.PImitation:0.00} " +
                           $"执念{a.PPerseveration:0.00} 耐痛{a.PPainTolerance:0.00}");

            // 位置与温度：优先用采样值（回看时也准确）
            string posTxt;
            if (_r.TryGetPose(_id, _tick, out var p, out _))
                posTxt = $"位置: ({p.X:0.0}, {p.Z:0.0})";
            else posTxt = "位置: —";
            float localT = _r.Sim.LocalTemperatureAt(p);
            Line(g, ref y, $"{posTxt}    局部温度 {localT:0.0}°C    全局 {_r.Sim.GlobalTemp:0.00}°C");

            if (dead)
            {
                y += 6;
                Line(g, ref y, $"☠ {DeathName(cause)}　死于 {Simulation.FormatGameTime(DeathTickOf(_id), _r.Cfg)}");
                return;
            }

            y += 8;
            Header(g, ref y, "身体状况");
            y += 2;

            float vit = bs?.Vitality ?? a.Vitality;
            float ct = (bs?.CoreTempQ ?? (byte)(a.CoreTemp * 5)) / 5f;
            float pain = (bs?.Pain ?? (byte)(a.Pain * 255)) / 255f;

            // 生命
            Bar(g, ref y, W, "生命", vit / 100f, $"{vit:0} / 100",
                vit > 60 ? Color.FromArgb(90, 200, 120) : vit > 30 ? Color.FromArgb(230, 190, 70) : Color.FromArgb(230, 80, 70));
            // 体温（35.5–38.0 映射到 0..1）
            float tf = Math.Clamp((ct - 34f) / 4f, 0f, 1f);
            Bar(g, ref y, W, "体温", tf, $"{ct:0.0} °C" + (ct < 36f ? "  ⚠ 失温" : ""),
                ct < 35f ? Color.FromArgb(90, 150, 235) : ct < 36.3f ? Color.FromArgb(140, 190, 230) : Color.FromArgb(110, 205, 140));
            // 疼痛
            Bar(g, ref y, W, "疼痛", pain, $"{pain * 100:0}%",
                pain > 0.5f ? Color.FromArgb(230, 90, 110) : Color.FromArgb(200, 160, 90));
            // 敏捷
            Bar(g, ref y, W, "敏捷", a.Dexterity, $"{a.Dexterity:0.00}  → 插孔失误率 {Simulation.InsertErrorFromCoreTemp(ct) * 100:0}%",
                Color.FromArgb(160, 170, 200));

            y += 8;
            Header(g, ref y, "伤势");
            y += 2;
            var inj = _r.InjuriesUpTo(_id, _tick, 6);
            if (inj.Count == 0) Line(g, ref y, "（无）", Color.FromArgb(140, 145, 155));
            foreach (var ie in inj)
            {
                string what = ie.Type == EventType.Burn ? "烫伤" : "受伤";
                string src = ie.TargetA > 0 && ie.TargetA < _r.Sim.EntityById.Length && _r.Sim.EntityById[ie.TargetA] != null
                    ? $"来自 {_r.Sim.EntityById[ie.TargetA].Type}#{_r.Sim.EntityById[ie.TargetA].Index}" : "";
                Line(g, ref y, $"· {what}　{Simulation.FormatGameTime(ie.Tick, _r.Cfg)}　{src}", Color.FromArgb(230, 160, 120));
            }

            y += 8;
            Header(g, ref y, "此刻相信什么（§4.2）");
            y += 2;
            foreach (KnowledgeId k in Enum.GetValues<KnowledgeId>())
            {
                if (k == KnowledgeId.None) continue;
                float c = a.ConfidenceOf(k);
                if (c < 0.06f) continue;
                var col = c > 0.6f ? Color.FromArgb(140, 220, 160) : c > 0.3f ? Color.FromArgb(210, 200, 120) : Color.FromArgb(150, 150, 160);
                Line(g, ref y, $"· {KnowledgeName(k),-14} {c:0.00}", col);
            }
        }

        private int DeathTickOf(int id)
        {
            var span = _r.Sim.Events.Span;
            for (int i = 0; i < span.Length; i++)
            {
                ref readonly var e = ref span[i];
                if (e.AgentId != id) continue;
                if (e.Type is EventType.DeathDrown or EventType.DeathShock or EventType.DeathInjury) return e.Tick;
            }
            return 0;
        }

        private void Header(Graphics g, ref float y, string t)
        {
            using var b = new SolidBrush(Color.FromArgb(120, 200, 235));
            g.DrawString(t, new Font(Font, FontStyle.Bold), b, 12, y);
            using var pen = new Pen(Color.FromArgb(50, 70, 90), 1f);
            g.DrawLine(pen, 12, y + 16, Width - 12, y + 16);
            y += 20;
        }

        private void Line(Graphics g, ref float y, string t, Color? c = null)
        {
            using var b = new SolidBrush(c ?? Color.FromArgb(215, 222, 234));
            g.DrawString(t, Font, b, 12, y);
            y += 15;
        }

        private void Bar(Graphics g, ref float y, float W, string label, float f, string val, Color col)
        {
            f = Math.Clamp(f, 0f, 1f);
            using var b = new SolidBrush(Color.FromArgb(215, 222, 234));
            g.DrawString(label, Font, b, 12, y);
            float bx = 62, bw = W - 90;
            g.FillRectangle(new SolidBrush(Color.FromArgb(38, 42, 50)), bx, y + 2, bw, 11);
            g.FillRectangle(new SolidBrush(col), bx, y + 2, bw * f, 11);
            g.DrawRectangle(new Pen(Color.FromArgb(60, 66, 78)), bx, y + 2, bw, 11);
            g.DrawString(val, Font, b, bx + bw + 6, y);
            y += 18;
        }

        public static string KnowledgeName(KnowledgeId k) => k switch
        {
            KnowledgeId.HeaterPush => "推得动",
            KnowledgeId.HeaterPlugFits => "插头能插",
            KnowledgeId.HeaterKnobExists => "有旋钮",
            KnowledgeId.HeaterWarm => "开着会暖",
            KnowledgeId.HeaterHot => "开着会烫",
            KnowledgeId.StickPlankTogether => "棍+板",
            KnowledgeId.DrillingMotion => "搓",
            KnowledgeId.FireWarm => "火很暖和",
            KnowledgeId.HoleLeftSafe => "左孔安全",
            KnowledgeId.HoleRightDeadly => "右孔致命",
            KnowledgeId.NailDangerous => "铁钉危险",
            KnowledgeId.WaterDanger => "水会淹死",
            KnowledgeId.WaterBody => "水里有同类",
            _ => k.ToString(),
        };
    }
}
