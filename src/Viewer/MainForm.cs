using System.Diagnostics;
using IdiotSim;
using IdiotSim.Agents;
using IdiotSim.Recording;

namespace IdiotSim.Viewer;

/// <summary>时间轴控件（design.md §7.2）：事件标记 + 温度缩略图 + 拖动回看。</summary>
public sealed class Timeline : Control
{
    private readonly SimRunner _r;
    public event Action<int>? OnSeek;
    public int HoverTick = -1;
    private bool _drag;
    public bool ShowDetail = true;

    public Timeline(SimRunner r)
    {
        _r = r;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(18, 20, 26);
        Height = 58;
        Dock = DockStyle.Bottom;
        Cursor = Cursors.Hand;
    }

    private float TickToX(int tick)
        => 8f + (Width - 16f) * Math.Clamp(tick / (float)Math.Max(1, _r.TargetTick), 0f, 1f);

    private int XToTick(float x)
        => (int)Math.Clamp((x - 8f) / Math.Max(1f, Width - 16f) * _r.TargetTick, 0, _r.TargetTick);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _drag = true;
        Seek(e.X); base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        HoverTick = XToTick(e.X);
        if (_drag) Seek(e.X);
        Invalidate();
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; base.OnMouseUp(e); }
    protected override void OnMouseLeave(EventArgs e) { HoverTick = -1; Invalidate(); base.OnMouseLeave(e); }

    private void Seek(float x) => OnSeek?.Invoke(XToTick(x));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using var f = new Font("Consolas", 8f);
        using var fb = new Font("Microsoft YaHei UI", 8.5f);

        float trackY = ShowDetail ? 34 : 18;
        float trackH = 8;

        // --- 温度缩略图（画在时间轴上，一眼看出升温过程与震荡）---
        var tc = _r.Sim.TempCurve;
        if (tc.Count > 1 && ShowDetail)
        {
            var pts = new List<PointF>(tc.Count);
            foreach (var (tick, temp) in tc)
            {
                float px = TickToX(tick);
                float v = Math.Clamp((temp - 10f) / 22f, 0f, 1f);
                pts.Add(new PointF(px, trackY - 2 - v * 22f));
            }
            g.DrawLines(new Pen(Color.FromArgb(180, 255, 140, 60), 1.5f), pts.ToArray());
            g.DrawString("室温曲线", f, new SolidBrush(Color.FromArgb(150, 200, 160, 100)), 10, 2);
        }

        // --- 轨道 ---
        g.FillRectangle(new SolidBrush(Color.FromArgb(38, 42, 52)), 8, trackY, Width - 16, trackH);

        // --- 事件标记 ---
        var span = _r.Sim.Events.Span;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var ev = ref span[i];
            Color? c = ev.Type switch
            {
                EventType.DeathDrown or EventType.DeathShock or EventType.DeathInjury => Color.FromArgb(220, 230, 60, 60),
                EventType.FireStart or EventType.HeaterStart => Color.FromArgb(255, 90, 230, 255),
                EventType.Burn or EventType.ContactHeater => Color.FromArgb(200, 255, 210, 80),
                EventType.HeaterKnob or EventType.HeaterPlug => Color.FromArgb(180, 120, 255, 160),
                EventType.Milestone => Color.FromArgb(255, 255, 170, 60),
                _ => null,
            };
            if (c == null) continue;
            float x = TickToX(ev.Tick);
            using var b = new SolidBrush(c.Value);
            bool big = ev.Type is EventType.Milestone or EventType.FireStart or EventType.HeaterStart;
            g.FillRectangle(b, x, trackY - (big ? 6 : 3), big ? 2.5f : 1.5f, trackH + (big ? 12 : 6));
        }

        // --- 当前位置 ---
        float hx = TickToX(_r.ViewTick);
        g.DrawLine(new Pen(Color.FromArgb(255, 240, 245, 255), 2f), hx, trackY - 8, hx, trackY + trackH + 8);
        g.FillEllipse(new SolidBrush(Color.FromArgb(255, 240, 245, 255)), hx - 4, trackY + trackH / 2 - 4, 8, 8);

        // --- 文字 ---
        string cur = Simulation.FormatGameTime(_r.ViewTick, _r.Cfg);
        string total = Simulation.FormatGameTime(_r.TargetTick, _r.Cfg);
        g.DrawString(cur, fb, new SolidBrush(Color.FromArgb(235, 240, 250)), 8, trackY + trackH + 4);
        var sz = g.MeasureString(total, fb);
        g.DrawString(total, fb, new SolidBrush(Color.FromArgb(140, 148, 160)), Width - sz.Width - 8, trackY + trackH + 4);

        if (HoverTick >= 0)
        {
            string ht = Simulation.FormatGameTime(HoverTick, _r.Cfg);
            var hs = g.MeasureString(ht, f);
            float x = Math.Clamp(TickToX(HoverTick) - hs.Width / 2, 2, Width - hs.Width - 2);
            g.FillRectangle(new SolidBrush(Color.FromArgb(200, 0, 0, 0)), x - 3, 1, hs.Width + 6, hs.Height);
            g.DrawString(ht, f, Brushes.White, x, 2);
        }
    }
}

/// <summary>
/// 主窗口（design.md §7）。左侧全局统计，中间 3D 场景，右侧个体检视，底部时间轴。
/// </summary>
public sealed class MainForm : Form
{
    private readonly SimRunner _runner;
    private readonly SceneView _scene;
    private readonly InspectorPanel _inspector;
    private readonly StatsPanel _stats;
    private readonly Timeline _timeline;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly Stopwatch _frameClock = new();
    private readonly ToolStripStatusLabel _status;
    private readonly ToolStripComboBox _speedBox, _modeBox, _overlayBox;
    private readonly ToolStripButton _pauseBtn;

    public MainForm()
    {
        Text = "500 个白痴 —— 观测台";
        Width = 1680; Height = 980;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(14, 16, 20);
        KeyPreview = true;

        var cfg = SimConfig.Default;
        _runner = new SimRunner(cfg, 4f * 3600f);   // 一场 4 游戏小时

        _scene = new SceneView(_runner) { Dock = DockStyle.Fill };
        _inspector = new InspectorPanel(_runner);
        _stats = new StatsPanel(_runner);
        _timeline = new Timeline(_runner);

        // ---- 顶部工具条 ----
        var bar = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = Color.FromArgb(26, 29, 36),
            ForeColor = Color.FromArgb(220, 226, 236),
            Renderer = new ToolStripProfessionalRenderer(new DarkColors()),
            Padding = new Padding(6, 2, 6, 2),
        };

        _pauseBtn = new ToolStripButton("⏸ 暂停") { ForeColor = Color.FromArgb(230, 236, 246) };
        _pauseBtn.Click += (_, _) => { _runner.Paused = !_runner.Paused; SyncButtons(); };
        bar.Items.Add(_pauseBtn);

        _speedBox = new ToolStripComboBox { Items = { "0.25×", "1×", "4×", "16×", "64×" }, SelectedIndex = 1, Width = 74 };
        _speedBox.SelectedIndexChanged += (_, _) =>
        {
            _runner.Speed = _speedBox.SelectedIndex switch { 0 => 0.25f, 1 => 1f, 2 => 4f, 3 => 16f, _ => 64f };
        };
        bar.Items.Add(new ToolStripLabel("倍速") { ForeColor = Color.FromArgb(180, 188, 200) });
        bar.Items.Add(_speedBox);

        bar.Items.Add(new ToolStripSeparator());

        _modeBox = new ToolStripComboBox { Items = { "漫游观测", "俯视全景", "跟随个体" }, SelectedIndex = 0, Width = 96 };
        _modeBox.SelectedIndexChanged += (_, _) =>
        {
            _scene.Mode = (ViewMode)_modeBox.SelectedIndex;
            _scene.Invalidate();
        };
        bar.Items.Add(new ToolStripLabel("视图") { ForeColor = Color.FromArgb(180, 188, 200) });
        bar.Items.Add(_modeBox);

        _overlayBox = new ToolStripComboBox { Items = { "无叠加", "温度", "情绪", "密度", "死亡地点", "知识" }, SelectedIndex = 0, Width = 96 };
        _overlayBox.SelectedIndexChanged += (_, _) =>
        {
            _scene.Over = (Overlay)_overlayBox.SelectedIndex;
            _scene.Invalidate();
        };
        bar.Items.Add(new ToolStripLabel("叠加") { ForeColor = Color.FromArgb(180, 188, 200) });
        bar.Items.Add(_overlayBox);

        bar.Items.Add(new ToolStripSeparator());
        var liveBtn = new ToolStripButton("⏭ 回到实时") { ForeColor = Color.FromArgb(230, 236, 246) };
        liveBtn.Click += (_, _) => { _runner.GoLive(); _runner.Paused = false; SyncButtons(); };
        bar.Items.Add(liveBtn);

        var jumpBtn = new ToolStripDropDownButton("跳到里程碑") { ForeColor = Color.FromArgb(230, 236, 246) };
        jumpBtn.DropDownItems.Add("G1 首台电暖气启动", null, (_, _) => Jump(_runner.Sim.T_FirstHeaterOn));
        jumpBtn.DropDownItems.Add("G2 首次钻木取火", null, (_, _) => Jump(_runner.Sim.T_FirstFire));
        jumpBtn.DropDownItems.Add("G3 全屋舒适", null, (_, _) => Jump(_runner.Sim.T_Comfort));
        jumpBtn.DropDownItems.Add("K_warm 反超 K_hot", null, (_, _) => Jump(_runner.Sim.T_FearCross));
        jumpBtn.DropDownItems.Add("首次死亡", null, (_, _) => Jump(_runner.Sim.T_FirstDeath));
        jumpBtn.DropDownItems.Add("首次烫伤", null, (_, _) => Jump(_runner.Sim.T_FirstBurn));
        bar.Items.Add(jumpBtn);

        bar.Items.Add(new ToolStripSeparator());
        var heatBtn = new ToolStripButton("地面热区") { Checked = true, CheckOnClick = true, ForeColor = Color.FromArgb(230, 236, 246) };
        heatBtn.CheckedChanged += (_, _) => { _scene.HeatZones = heatBtn.Checked; _scene.Invalidate(); };
        bar.Items.Add(heatBtn);

        _status = new ToolStripStatusLabel("初始化…") { ForeColor = Color.FromArgb(200, 208, 220), Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        bar.Items.Add(_status);

        // ---- 布局 ----
        var center = new Panel { Dock = DockStyle.Fill };
        center.Controls.Add(_scene);

        Controls.Add(center);
        Controls.Add(_stats);
        Controls.Add(_inspector);
        Controls.Add(_timeline);
        Controls.Add(bar);

        // ---- 事件接线 ----
        _scene.OnSelectionChanged += () =>
        {
            _inspector.AgentId = _scene.SelectedId;
            _scene.Invalidate();
        };
        _inspector.OnSeekToTick += t => Jump(t);
        _stats.OnSeekToTick += t => Jump(t);
        _timeline.OnSeek += t =>
        {
            _runner.JumpTo(t);          // 拖动即暂停并回看
            SyncButtons();
            RefreshPanels();
        };

        KeyDown += OnKey;

        _uiTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _uiTimer.Tick += (_, _) => TickUi();
        _uiTimer.Start();
        _frameClock.Start();

        SyncButtons();
    }

    private void Jump(int tick)
    {
        if (tick < 0) { _status.Text = "该里程碑尚未发生"; return; }
        _runner.JumpTo(tick);
        SyncButtons();
        RefreshPanels();
        _scene.Invalidate();
    }

    private void SyncButtons()
    {
        _pauseBtn.Text = _runner.Paused ? "▶ 继续" : "⏸ 暂停";
        _pauseBtn.Checked = _runner.Paused;
        _modeBox.SelectedIndex = (int)_scene.Mode;
    }

    private void OnKey(object? s, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Space when !_scene.Focused:
                _runner.Paused = !_runner.Paused; SyncButtons(); e.Handled = true; break;
            case Keys.E:
                if (_scene.HoverId > 0)
                {
                    _scene.SelectedId = _scene.SelectedId == _scene.HoverId ? -1 : _scene.HoverId;
                    _inspector.AgentId = _scene.SelectedId;
                    _scene.Invalidate();
                }
                else if (_scene.SelectedId > 0) { _scene.SelectedId = -1; _inspector.AgentId = -1; _scene.Invalidate(); }
                e.Handled = true; break;
            case Keys.Q: Cycle(-1); e.Handled = true; break;
            case Keys.R: Cycle(+1); e.Handled = true; break;
            case Keys.F:
                _scene.Mode = ViewMode.Follow; _modeBox.SelectedIndex = 2; _scene.Invalidate(); e.Handled = true; break;
            case Keys.D1: Jump(_runner.Sim.T_FirstDeath); e.Handled = true; break;
            case Keys.D2: Jump(_runner.Sim.T_FirstBurn); e.Handled = true; break;
            case Keys.D3: Jump(_runner.Sim.T_FirstHeaterOn); e.Handled = true; break;
        }
    }

    private void Cycle(int d)
    {
        int id = _scene.SelectedId;
        id = id <= 0 ? (d > 0 ? 1 : _runner.Cfg.AgentCount) : ((id - 1 + d + _runner.Cfg.AgentCount) % _runner.Cfg.AgentCount) + 1;
        _scene.SelectedId = id;
        _inspector.AgentId = id;
        _scene.Invalidate();
    }

    private void RefreshPanels()
    {
        _inspector.Refresh2();
        _stats.Invalidate();
        _timeline.Invalidate();
    }

    private void TickUi()
    {
        float dt = (float)_frameClock.Elapsed.TotalSeconds;
        _frameClock.Restart();
        if (dt > 0.2f) dt = 0.2f;

        _scene.UpdateCamera(dt);
        _scene.Invalidate();
        _timeline.Invalidate();
        _stats.Invalidate();
        if (_inspector.Visible) _inspector.Refresh2();

        string state = _runner.IsLive ? (_runner.Paused ? "已暂停" : "运行中") : "回看";
        _status.Text = $"{state}   {Simulation.FormatGameTime(_runner.ViewTick, _runner.Cfg)} / " +
                       $"{Simulation.FormatGameTime(_runner.TargetTick, _runner.Cfg)}   " +
                       $"存活 {_runner.AliveAt(_runner.ViewTick)}/{_runner.Cfg.AgentCount}   " +
                       $"室温 {_runner.Sim.GlobalTemp:0.00}°C   " +
                       $"{_runner.TicksPerRealSecond / 60f:0.0}× 实时   " +
                       $"{_runner.Sim.Events.Count} 事件";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _uiTimer.Stop();
        _runner.Dispose();
        base.OnFormClosing(e);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Color.FromArgb(26, 29, 36);
        public override Color ToolStripGradientMiddle => Color.FromArgb(26, 29, 36);
        public override Color ToolStripGradientEnd => Color.FromArgb(26, 29, 36);
        public override Color ToolStripDropDownBackground => Color.FromArgb(32, 36, 44);
        public override Color MenuItemSelected => Color.FromArgb(52, 60, 74);
        public override Color MenuBorder => Color.FromArgb(60, 66, 80);
        public override Color ButtonSelectedHighlight => Color.FromArgb(52, 60, 74);
    }
}
