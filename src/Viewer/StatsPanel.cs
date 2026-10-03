using System.Drawing.Drawing2D;
using IdiotSim;
using IdiotSim.Knowledge;
using IdiotSim.Recording;

namespace IdiotSim.Viewer;

/// <summary>
/// 全局统计面板（design.md §7.4）：KPI 大字 / 存活曲线 / 死因 / 知识扩散 / 温度双轴。
/// </summary>
public sealed class StatsPanel : Control
{
    private readonly SimRunner _r;
    public event Action<int>? OnSeekToTick;

    private readonly Rectangle[] _kpiRects = new Rectangle[4];

    public StatsPanel(SimRunner r)
    {
        _r = r;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(20, 22, 28);
        Width = 330;
        Dock = DockStyle.Left;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int tick = _r.ViewTick;
        for (int i = 0; i < _kpiRects.Length; i++)
        {
            if (!_kpiRects[i].Contains(e.Location)) continue;
            int t = i switch
            {
                0 => _r.Sim.T_FirstHeaterOn,
                1 => _r.Sim.T_FirstFire,
                2 => _r.Sim.T_Comfort,
                _ => _r.Sim.T_FearCross,
            };
            if (t >= 0) OnSeekToTick?.Invoke(t);
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        using var f = new Font("Consolas", 9f);
        using var fb = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        using var fbig = new Font("Consolas", 13f, FontStyle.Bold);
        float y = 8;

        g.DrawString("目标 KPI（点击跳转）", fb, new SolidBrush(Color.FromArgb(120, 200, 235)), 10, y);
        y += 22;

        float half = (Width - 26) / 2f;
        Kpi(g, fbig, f, 10, y, half, 0, "G1 首台电暖气", _r.Sim.T_FirstHeaterOn, Color.FromArgb(120, 220, 255));
        Kpi(g, fbig, f, 12 + half, y, half, 1, "G2 首次取火", _r.Sim.T_FirstFire, Color.FromArgb(255, 170, 70));
        y += 46;
        Kpi(g, fbig, f, 10, y, half, 2, "G3 全屋舒适", _r.Sim.T_Comfort, Color.FromArgb(130, 240, 150));
        Kpi(g, fbig, f, 12 + half, y, half, 3, "K_warm>K_hot", _r.Sim.T_FearCross, Color.FromArgb(230, 130, 220));
        y += 50;

        using var pen = new Pen(Color.FromArgb(45, 52, 64));
        g.DrawLine(pen, 8, y, Width - 8, y);
        y += 8;

        int tick = _r.ViewTick;
        int alive = _r.AliveAt(tick);
        g.DrawString($"当前时刻  {Simulation.FormatGameTime(tick, _r.Cfg)}", fb, new SolidBrush(Color.FromArgb(215, 222, 234)), 10, y);
        y += 20;
        g.DrawString($"存活 {alive} / {_r.Cfg.AgentCount}    死亡 {_r.Cfg.AgentCount - alive}", f, new SolidBrush(Color.FromArgb(200, 208, 220)), 10, y);
        y += 16;
        g.DrawString($"室温 {_r.Sim.GlobalTemp:0.00}°C   局部最高 {MaxLocalTemp():0.0}°C", f, new SolidBrush(Color.FromArgb(200, 208, 220)), 10, y);
        y += 16;
        g.DrawString($"溺亡 {_r.Sim.DeathsDrown}   触电 {_r.Sim.DeathsShock}   伤重 {_r.Sim.DeathsInjury}", f, new SolidBrush(Color.FromArgb(200, 208, 220)), 10, y);
        y += 16;
        g.DrawString($"烫伤事件 {_r.Sim.BurnEvents}", f, new SolidBrush(Color.FromArgb(230, 180, 120)), 10, y);
        y += 22;

        // --- 知识扩散曲线（§4.6，K_warm 与 K_hot 必须同图）---
        var kc = _r.Sim.KnowledgeCurve;
        y = Chart(g, fb, f, y, "知识扩散（warm/hot/deadly/water/fire）", 84,
            new (string, Color, float[])[]
            {
                ("warm", Color.FromArgb(120, 220, 255), kc.Select(v => (float)v.warm).ToArray()),
                ("hot",  Color.FromArgb(255, 120, 90),  kc.Select(v => (float)v.hot).ToArray()),
                ("dead", Color.FromArgb(230, 200, 90),  kc.Select(v => (float)v.deadly).ToArray()),
                ("water",Color.FromArgb(90, 160, 235),  kc.Select(v => (float)v.water).ToArray()),
                ("fire", Color.FromArgb(255, 170, 70),  kc.Select(v => (float)v.fire).ToArray()),
            },
            _r.Cfg.AgentCount);

        // --- 温度曲线 ---
        y = Chart(g, fb, f, y, "室温 (°C)", 76,
            new (string, Color, float[])[]
            {
                ("temp", Color.FromArgb(255, 140, 60), _r.Sim.TempCurve.Select(v => v.temp).ToArray()),
            },
            36f);

        // --- 死因占比 ---
        y += 4;
        g.DrawString("死因分布", fb, new SolidBrush(Color.FromArgb(120, 200, 235)), 10, y);
        y += 20;
        int dd = _r.Sim.DeathsDrown, ds = _r.Sim.DeathsShock, di = _r.Sim.DeathsInjury;
        Pie(g, 10, y, 76, dd, ds, di);
        float lx = 96;
        Legend(g, f, ref lx, y + 6, Color.FromArgb(90, 160, 235), $"溺亡 {dd}");
        Legend(g, f, ref lx, y + 22, Color.FromArgb(255, 120, 90), $"触电 {ds}");
        Legend(g, f, ref lx, y + 38, Color.FromArgb(230, 200, 90), $"伤重 {di}");
    }

    private float MaxLocalTemp()
    {
        float m = _r.Sim.GlobalTemp;
        foreach (var id in _r.Sim.HeaterIds)
        {
            var h = _r.Sim.EntityById[id];
            if (h != null && h.Running) m = Math.Max(m, h.SurfaceTemp);
        }
        return m;
    }

    private void Kpi(Graphics g, Font fbig, Font f, float x, float y, float w, int idx, string label, int tick, Color col)
    {
        var rect = new Rectangle((int)x, (int)y, (int)w, 42);
        _kpiRects[idx] = rect;
        using var bg = new SolidBrush(Color.FromArgb(28, 32, 40));
        g.FillRectangle(bg, rect);
        using var pen = new Pen(Color.FromArgb(col.R, col.G, col.B, 120), 1f);
        g.DrawRectangle(pen, rect);
        g.DrawString(label, f, new SolidBrush(Color.FromArgb(170, 180, 195)), x + 6, y + 4);
        string v = tick < 0 ? "（未发生）" : Simulation.FormatGameTime(tick, _r.Cfg);
        g.DrawString(v, fbig, new SolidBrush(tick < 0 ? Color.FromArgb(120, 125, 135) : col), x + 6, y + 19);
    }

    private float Chart(Graphics g, Font fb, Font f, float y, string title, float h,
        (string name, Color col, float[] vals)[] series, float max)
    {
        g.DrawString(title, fb, new SolidBrush(Color.FromArgb(120, 200, 235)), 10, y);
        y += 18;
        var box = new Rectangle(10, (int)y, Width - 20, (int)h);
        g.FillRectangle(new SolidBrush(Color.FromArgb(16, 18, 24)), box);
        g.DrawRectangle(new Pen(Color.FromArgb(45, 52, 64)), box);
        if (series.Length > 0 && series[0].vals.Length > 1)
        {
            // 把当前时间轴位置画成竖线
            int headTick = _r.ViewTick;
            float hx = box.Left + box.Width * Math.Clamp(headTick / (float)_r.TargetTick, 0f, 1f);
            g.DrawLine(new Pen(Color.FromArgb(120, 255, 255, 255), 1f), hx, box.Top, hx, box.Bottom);

            float lx = box.Left + 4;
            foreach (var s in series)
            {
                // 采样点本身是等间隔的，横坐标直接按索引均匀铺开
                int n = s.vals.Length;
                var pts = new PointF[n];
                for (int i = 0; i < n; i++)
                {
                    float px = box.Left + box.Width * i / (float)Math.Max(1, n - 1);
                    float v = Math.Clamp(s.vals[i] / max, 0f, 1f);
                    pts[i] = new PointF(px, box.Bottom - 3 - (box.Height - 6) * v);
                }
                if (n >= 2) g.DrawLines(new Pen(s.col, 1.6f), pts);
                using var b = new SolidBrush(s.col);
                g.DrawString(s.name, f, b, lx, box.Top + 2);
                lx += g.MeasureString(s.name, f).Width + 8;
            }
        }
        return y + h + 10;
    }

    private void Pie(Graphics g, float x, float y, float d, int a, int b, int c)
    {
        int tot = Math.Max(1, a + b + c);
        float start = -90f;
        Color[] cols = { Color.FromArgb(90, 160, 235), Color.FromArgb(255, 120, 90), Color.FromArgb(230, 200, 90) };
        int[] vals = { a, b, c };
        for (int i = 0; i < 3; i++)
        {
            float sweep = 360f * vals[i] / tot;
            if (sweep <= 0f) continue;
            using var br = new SolidBrush(cols[i]);
            g.FillPie(br, x, y, d, d, start, sweep);
            start += sweep;
        }
        g.DrawEllipse(new Pen(Color.FromArgb(60, 68, 82)), x, y, d, d);
    }

    private void Legend(Graphics g, Font f, ref float x, float y, Color c, string t)
    {
        using var b = new SolidBrush(c);
        g.FillRectangle(b, x, y + 3, 9, 9);
        using var tb = new SolidBrush(Color.FromArgb(205, 212, 224));
        g.DrawString(t, f, tb, x + 13, y);
        x += 13 + g.MeasureString(t, f).Width + 8;
    }
}
