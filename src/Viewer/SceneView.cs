using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using IdiotSim;
using IdiotSim.Agents;
using IdiotSim.Core;
using IdiotSim.Knowledge;
using IdiotSim.Recording;
using IdiotSim.World;

namespace IdiotSim.Viewer;

/// <summary>视图模式（design.md §7.1）。</summary>
public enum ViewMode { Roam, TopDown, Follow }

/// <summary>热力图叠加层。</summary>
public enum Overlay { None, Temperature, Emotion, Density, Deaths, Knowledge }

public struct Vec3
{
    public float X, Y, Z;
    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}

/// <summary>
/// 软件渲染的场景视图（design.md §7.5 / §8.3）。
/// 没有 Unity，用 GDI+ 手写针孔投影 + 近平面裁剪 + 画家算法。
/// 房间是凸的，所以"先画地面与四面墙、再按深度从远到近画内容"永远正确。
/// </summary>
public sealed class SceneView : Control
{
    private readonly SimRunner _r;
    public ViewMode Mode = ViewMode.Roam;
    public Overlay Over = Overlay.None;
    public bool HeatZones = true;

    /// <summary>幽灵观察者相机（§7.5.1：眼高 1.7m，无碰撞，不扰动仿真）。</summary>
    public Vec3 Eye = new(0f, 1.7f, 30f);
    public float Yaw, Pitch;
    public float WalkSpeed = 2.5f;

    public int SelectedId = -1;
    public int HoverId = -1;

    private readonly HashSet<Keys> _keys = new();
    private bool _looking;
    private Point _lastMouse;
    private int _lastFrameTick = -1;

    /// <summary>准星指向的白痴（≤15m 才给名牌，§7.5.2）。</summary>
    public float CrosshairRange = 15f;

    public SceneView(SimRunner r)
    {
        _r = r;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        BackColor = Color.Black;
    }

    // =====================================================================
    //  输入
    // =====================================================================

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Right) { _looking = true; _lastMouse = e.Location; Cursor = Cursors.SizeAll; }
        else if (e.Button == MouseButtons.Left)
        {
            if (HoverId > 0) SelectedId = SelectedId == HoverId ? -1 : HoverId;
            else SelectedId = -1;
            OnSelectionChanged?.Invoke();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { _looking = false; Cursor = Cursors.Default; }
        base.OnMouseUp(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_looking)
        {
            float dx = e.X - _lastMouse.X, dy = e.Y - _lastMouse.Y;
            _lastMouse = e.Location;
            Yaw += dx * 0.004f;
            Pitch = Math.Clamp(Pitch - dy * 0.004f, -1.45f, 1.45f);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Mode != ViewMode.Roam) { }
        base.OnMouseWheel(e);
    }

    protected override bool IsInputKey(Keys k) => true;

    protected override void OnKeyDown(KeyEventArgs e) { _keys.Add(e.KeyCode); base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { _keys.Remove(e.KeyCode); base.OnKeyUp(e); }
    protected override void OnLostFocus(EventArgs e) { _keys.Clear(); base.OnLostFocus(e); }

    public event Action? OnSelectionChanged;

    /// <summary>每帧推进幽灵相机（§7.5.1）。Δt 为真实秒。</summary>
    public void UpdateCamera(float dt)
    {
        if (Mode == ViewMode.Follow && SelectedId > 0)
        {
            if (_r.TryGetPose(SelectedId, _r.ViewTick, out var p, out _))
                Eye = new Vec3(p.X - MathF.Sin(Yaw) * 6f, 3.2f, p.Z - MathF.Cos(Yaw) * 6f);
            return;
        }
        if (Mode != ViewMode.Roam || !Focused) return;

        float sp = WalkSpeed * (_keys.Contains(Keys.ShiftKey) ? 2f : 1f) * dt;
        var fwd = new Vec3(MathF.Sin(Yaw), 0, MathF.Cos(Yaw));
        var right = new Vec3(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));
        Vec3 d = default;
        if (_keys.Contains(Keys.W)) d = new Vec3(d.X + fwd.X, d.Y, d.Z + fwd.Z);
        if (_keys.Contains(Keys.S)) d = new Vec3(d.X - fwd.X, d.Y, d.Z - fwd.Z);
        if (_keys.Contains(Keys.D)) d = new Vec3(d.X + right.X, d.Y, d.Z + right.Z);
        if (_keys.Contains(Keys.A)) d = new Vec3(d.X - right.X, d.Y, d.Z - right.Z);
        if (_keys.Contains(Keys.Space)) d.Y += 1f;
        if (_keys.Contains(Keys.ControlKey)) d.Y -= 1f;
        if (_keys.Contains(Keys.Left)) Yaw -= 1.6f * dt;
        if (_keys.Contains(Keys.Right)) Yaw += 1.6f * dt;
        if (_keys.Contains(Keys.Up)) Pitch = Math.Min(1.45f, Pitch + 1.2f * dt);
        if (_keys.Contains(Keys.Down)) Pitch = Math.Max(-1.45f, Pitch - 1.2f * dt);

        float len = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        if (len > 1e-4f) { d = new Vec3(d.X / len * sp, d.Y * sp, d.Z / len * sp); }
        else d = new Vec3(0, d.Y * sp, 0);

        Eye = new Vec3(
            Math.Clamp(Eye.X + d.X, -49f, 49f),
            Math.Clamp(Eye.Y + d.Y, 0.4f, 40f),
            Math.Clamp(Eye.Z + d.Z, -49f, 49f));
    }

    // =====================================================================
    //  渲染
    // =====================================================================

    protected override void OnPaint(PaintEventArgs pe)
    {
        var g = pe.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(8, 10, 14));

        int tick = _r.ViewTick;
        if (tick != _lastFrameTick) { RecomputeHover(tick); _lastFrameTick = tick; }

        if (Mode == ViewMode.TopDown) DrawTopDown(g, tick);
        else DrawPerspective(g, tick);

        DrawHud(g, tick);
    }

    private const float Near = 0.08f;

    /// <summary>世界点 → 相机空间。</summary>
    private Vec3 ToCam(Vec3 p)
    {
        float dx = p.X - Eye.X, dy = p.Y - Eye.Y, dz = p.Z - Eye.Z;
        float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);
        float xc = dx * cy - dz * sy;
        float zc = dx * sy + dz * cy;
        float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
        float yc2 = dy * cp - zc * sp;
        float zc2 = dy * sp + zc * cp;
        return new Vec3(xc, yc2, zc2);
    }

    private float Focal => Width * 0.5f / MathF.Tan(1.0f);   // 90° 水平 FOV

    private PointF Project(in Vec3 c)
        => new(Width * 0.5f + Focal * c.X / c.Z, Height * 0.5f - Focal * c.Y / c.Z);

    /// <summary>近平面裁剪（Sutherland–Hodgman，只对一个平面）。</summary>
    private static List<Vec3> ClipNear(List<Vec3> poly)
    {
        var outp = new List<Vec3>(poly.Count + 2);
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            bool ain = a.Z >= Near, bin = b.Z >= Near;
            if (ain) outp.Add(a);
            if (ain != bin)
            {
                float t = (Near - a.Z) / (b.Z - a.Z);
                outp.Add(new Vec3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, Near));
            }
        }
        return outp;
    }

    private void FillPoly(Graphics g, List<Vec3> world, Brush fill, Pen? stroke = null)
    {
        var cam = new List<Vec3>(world.Count);
        for (int i = 0; i < world.Count; i++) cam.Add(ToCam(world[i]));
        var clipped = ClipNear(cam);
        if (clipped.Count < 3) return;
        var pts = new PointF[clipped.Count];
        for (int i = 0; i < clipped.Count; i++)
        {
            var p = Project(clipped[i]);
            // 限制在合理范围内，避免 GDI+ 因超大坐标抛异常
            pts[i] = new PointF(Math.Clamp(p.X, -20000f, 20000f), Math.Clamp(p.Y, -20000f, 20000f));
        }
        g.FillPolygon(fill, pts);
        if (stroke != null) g.DrawPolygon(stroke, pts);
    }

    private static float Dist2D(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    // ---------- 透视（漫游 / 跟随）----------

    private void DrawPerspective(Graphics g, int tick)
    {
        float H = _r.Cfg.RoomHalf, RH = _r.Cfg.RoomHeight;
        float ph = _r.Cfg.PoolHalf;

        // --- 地面 ---
        FillPoly(g, new List<Vec3> {
            new(-H,0,-H), new(H,0,-H), new(H,0,H), new(-H,0,H)
        }, new SolidBrush(Color.FromArgb(38, 40, 46)));

        // --- 地面网格（10m 一格，提供尺度感）---
        using var gridPen = new Pen(Color.FromArgb(24, 60, 66, 78), 1f);
        for (float x = -H; x <= H; x += 10f)
            FillPolyLine(g, new Vec3(x, 0.01f, -H), new Vec3(x, 0.01f, H), gridPen);
        for (float z = -H; z <= H; z += 10f)
            FillPolyLine(g, new Vec3(-H, 0.01f, z), new Vec3(H, 0.01f, z), gridPen);

        // --- 局部热区（站在电暖气/火边上能看见地面泛红）---
        if (HeatZones) DrawHeatZones(g, tick);

        // --- 水池 ---
        FillPoly(g, new List<Vec3> {
            new(-ph,0.02f,-ph), new(ph,0.02f,-ph), new(ph,0.02f,ph), new(-ph,0.02f,ph)
        }, new SolidBrush(Color.FromArgb(255, 14, 34, 56)),
           new Pen(Color.FromArgb(150, 60, 150, 210), 1.5f));

        // --- 四面墙 ---
        DrawWall(g, new Vec3(-H, 0, H), new Vec3(H, 0, H), RH, Color.FromArgb(58, 60, 70));
        DrawWall(g, new Vec3(H, 0, -H), new Vec3(H, 0, H), RH, Color.FromArgb(48, 50, 60));
        DrawWall(g, new Vec3(-H, 0, -H), new Vec3(H, 0, -H), RH, Color.FromArgb(52, 54, 64));
        DrawWall(g, new Vec3(-H, 0, -H), new Vec3(-H, 0, H), RH, Color.FromArgb(44, 46, 56));

        // --- 插座（400 个，按距离剔除）---
        DrawSockets(g);

        // --- 火 ---
        if (_r.Sim.FireBurning && _r.Sim.FireEntityId > 0)
        {
            var f = _r.Sim.EntityById[_r.Sim.FireEntityId];
            DrawFire(g, new Vec3(f.Pos.X, 0, f.Pos.Z));
        }

        // --- 实体 + 白痴：统一按深度从远到近 ---
        var drawables = new List<(float depth, Action<Graphics> fn)>(_r.Cfg.AgentCount);

        foreach (var id in _r.Sim.HeaterIds)
        {
            var e = _r.Sim.EntityById[id];
            if (e == null) continue;
            var c = ToCam(new Vec3(e.Pos.X, 0, e.Pos.Z));
            if (c.Z < Near) continue;
            var ent = e;
            drawables.Add((c.Z, gg => DrawHeater(gg, ent, tick)));
        }
        foreach (var list in new[] { _r.Sim.NailIds, _r.Sim.PlankIds, _r.Sim.StickIds, _r.Sim.WeedIds })
            foreach (var id in list)
            {
                var e = _r.Sim.EntityById[id];
                if (e == null || e.Held) continue;
                var c = ToCam(new Vec3(e.Pos.X, 0, e.Pos.Z));
                if (c.Z < Near) continue;
                var ent = e;
                drawables.Add((c.Z, gg => DrawProp(gg, ent)));
            }

        for (int i = 0; i < _r.Sim.Agents.Count; i++)
        {
            var a = _r.Sim.Agents[i];
            if (!_r.TryGetPose(a.Id, tick, out var p, out var st)) continue;
            var c = ToCam(new Vec3(p.X, 0, p.Z));
            if (c.Z < Near || c.Z > 90f) continue;
            byte state = st;
            drawables.Add((c.Z, gg => DrawAgent(gg, a, p, state, tick)));
        }

        drawables.Sort((x, y) => y.depth.CompareTo(x.depth));
        foreach (var d in drawables) d.fn(g);

        DrawCrosshair(g);
    }

    private void FillPolyLine(Graphics g, Vec3 a, Vec3 b, Pen pen)
    {
        var ca = ToCam(a); var cb = ToCam(b);
        if (ca.Z < Near && cb.Z < Near) return;
        if (ca.Z < Near) { float t = (Near - ca.Z) / (cb.Z - ca.Z); ca = new Vec3(ca.X + (cb.X - ca.X) * t, ca.Y + (cb.Y - ca.Y) * t, Near); }
        if (cb.Z < Near) { float t = (Near - cb.Z) / (ca.Z - cb.Z); cb = new Vec3(cb.X + (ca.X - cb.X) * t, cb.Y + (ca.Y - cb.Y) * t, Near); }
        var pa = Project(ca); var pb = Project(cb);
        if (MathF.Abs(pa.X) > 20000 || MathF.Abs(pa.Y) > 20000) return;
        if (MathF.Abs(pb.X) > 20000 || MathF.Abs(pb.Y) > 20000) return;
        g.DrawLine(pen, pa, pb);
    }

    private void DrawWall(Graphics g, Vec3 a, Vec3 b, float h, Color col)
    {
        // 墙面分上下两段做一点明暗，避免大片死板
        FillPoly(g, new List<Vec3> { a, b, new(b.X, h, b.Z), new(a.X, h, a.Z) },
                 new SolidBrush(col));
    }

    private void DrawHeatZones(Graphics g, int tick)
    {
        // 只在相机附近画，远处看不见也不值得画
        float step = 8f;
        float h = _r.Cfg.RoomHalf;
        int cx = (int)(Eye.X / step), cz = (int)(Eye.Z / step);
        for (int i = cx - 3; i <= cx + 3; i++)
            for (int j = cz - 3; j <= cz + 3; j++)
            {
                float x = i * step, z = j * step;
                if (MathF.Abs(x) > h - 1 || MathF.Abs(z) > h - 1) continue;
                float t = _r.Sim.LocalTemperatureAt(new Vec2(x + step / 2, z + step / 2));
                float f = Math.Clamp((t - _r.Cfg.T_Out) / 14f, 0f, 1f);
                if (f <= 0.02f) continue;
                int alpha = (int)(f * 90);
                FillPoly(g, new List<Vec3> {
                    new(x, 0.015f, z), new(x+step, 0.015f, z),
                    new(x+step, 0.015f, z+step), new(x, 0.015f, z+step)
                }, new SolidBrush(Color.FromArgb(alpha, 255, 120, 40)));
            }
    }

    private void DrawSockets(Graphics g)
    {
        var r = _r;
        using var livePen = new Pen(Color.FromArgb(70, 255, 80, 80), 1f);
        using var nailPen = new Pen(Color.FromArgb(230, 255, 200, 60), 1.5f);
        foreach (var id in r.Sim.SocketIds)
        {
            var s = r.Sim.EntityById[id];
            if (s == null) continue;
            float d = Dist2D(new Vec3(s.Pos.X, 0, s.Pos.Z), Eye);
            if (d > 55f) continue;
            var cam = ToCam(new Vec3(s.Pos.X, s.Height, s.Pos.Z));
            if (cam.Z < Near) continue;

            // 插座本体：贴在墙上的小方块
            var inward = new Vec2(s.WallInward.X, s.WallInward.Z);
            var along = new Vec2(-inward.Z, inward.X);
            float hw = 0.22f, hh = 0.28f;
            Vec3 Corner(float u, float v) => new(
                s.Pos.X + along.X * u + inward.X * 0.02f, s.Height + v,
                s.Pos.Z + along.Z * u + inward.Z * 0.02f);

            Color c = s.SState switch
            {
                SocketState.NailInLive => Color.FromArgb(255, 220, 70, 70),
                SocketState.NailInNeutral => Color.FromArgb(255, 90, 220, 120),
                SocketState.PluggedIn => Color.FromArgb(255, 90, 170, 255),
                _ => Color.FromArgb(255, 120, 122, 132),
            };
            FillPoly(g, new List<Vec3> { Corner(-hw, -hh), Corner(hw, -hh), Corner(hw, hh), Corner(-hw, hh) },
                     new SolidBrush(c));

            // 火线孔（右）常亮红点——这是"看得见但看不懂"的关键信息
            var rp = Corner(hw * 0.45f, 0f);
            var lp = Corner(-hw * 0.45f, 0f);
            DrawDot(g, rp, 2.2f, Color.FromArgb(220, 255, 60, 60));
            DrawDot(g, lp, 2.2f, Color.FromArgb(200, 70, 140, 255));
        }
    }

    private void DrawDot(Graphics g, Vec3 world, float px, Color col)
    {
        var cam = ToCam(world);
        if (cam.Z < Near) return;
        var p = Project(cam);
        if (MathF.Abs(p.X) > 5000 || MathF.Abs(p.Y) > 5000) return;
        using var b = new SolidBrush(col);
        g.FillEllipse(b, p.X - px, p.Y - px, px * 2, px * 2);
    }

    private void DrawFire(Graphics g, Vec3 at)
    {
        var cam = ToCam(new Vec3(at.X, 0.6f, at.Z));
        if (cam.Z < Near) return;
        var p = Project(cam);
        float sc = Math.Clamp(Focal / cam.Z, 1f, 400f);
        float flick = 1f + 0.12f * MathF.Sin(_r.ViewTick * 0.7f);
        using var glow = new SolidBrush(Color.FromArgb(70, 255, 140, 40));
        g.FillEllipse(glow, p.X - sc * 3.2f, p.Y - sc * 3.0f, sc * 6.4f, sc * 6f);
        using var core = new SolidBrush(Color.FromArgb(235, 255, 170, 60));
        g.FillEllipse(core, p.X - sc * 0.7f * flick, p.Y - sc * 1.7f * flick, sc * 1.4f * flick, sc * 1.9f * flick);
        using var hot = new SolidBrush(Color.FromArgb(245, 255, 240, 190));
        g.FillEllipse(hot, p.X - sc * 0.34f, p.Y - sc * 0.85f, sc * 0.68f, sc * 0.95f);
    }

    private void DrawHeater(Graphics g, Entity e, int tick)
    {
        var baseC = new Vec3(e.Pos.X, 0, e.Pos.Z);
        var cam = ToCam(baseC);
        if (cam.Z < Near) return;
        var pb = Project(cam);
        var pt = Project(ToCam(new Vec3(e.Pos.X, 0.62f, e.Pos.Z)));
        float sc = Math.Clamp(Focal / cam.Z, 0.05f, 400f);
        float w = sc * 0.55f, h = MathF.Abs(pb.Y - pt.Y);

        Color body = e.Broken ? Color.FromArgb(255, 70, 60, 60)
                   : e.Running ? Color.FromArgb(255, 200, 90, 55)
                   : Color.FromArgb(255, 105, 108, 118);
        using var bb = new SolidBrush(body);
        g.FillRectangle(bb, pb.X - w, pt.Y, w * 2, h);

        if (e.Running)
        {
            // 发红光——这是设计里那个"停不下来的广告"（§2.5.2）
            using var glow = new SolidBrush(Color.FromArgb(60, 255, 80, 40));
            g.FillEllipse(glow, pb.X - w * 2.4f, pt.Y - w * 1.6f, w * 4.8f, h + w * 3.2f);
            using var strip = new SolidBrush(Color.FromArgb(255, 255, 130, 60));
            g.FillRectangle(strip, pb.X - w * 0.8f, pt.Y + h * 0.25f, w * 1.6f, Math.Max(1f, h * 0.5f));
        }
        if (e.Plugged)
        {
            using var pen = new Pen(Color.FromArgb(180, 110, 180, 255), 1.4f);
            g.DrawRectangle(pen, pb.X - w, pt.Y, w * 2, h);
        }
        if (sc > 5f)
        {
            using var f = new Font("Consolas", Math.Clamp(sc * 0.16f, 6f, 13f));
            using var tb = new SolidBrush(Color.FromArgb(220, 235, 235, 245));
            string lbl = $"暖气#{e.Index}" + (e.Running ? $" 开{e.Knob}档 {e.SurfaceTemp:0}°" : e.Plugged ? " 已插电" : "");
            g.DrawString(lbl, f, tb, pb.X - w, pt.Y - f.Height - 2);
        }
    }

    private void DrawProp(Graphics g, Entity e)
    {
        var cam = ToCam(new Vec3(e.Pos.X, 0, e.Pos.Z));
        if (cam.Z < Near) return;
        var p = Project(cam);
        float sc = Math.Clamp(Focal / cam.Z, 0.05f, 400f);
        Color c = e.Type switch
        {
            EntityType.Nail => Color.FromArgb(255, 215, 215, 225),
            EntityType.Plank => Color.FromArgb(255, 150, 110, 65),
            EntityType.Stick => Color.FromArgb(255, 170, 130, 80),
            EntityType.Weed => Color.FromArgb(255, 95, 155, 75),
            _ => Color.Gray,
        };
        using var b = new SolidBrush(c);
        float s = Math.Clamp(sc * 0.13f, 1.5f, 14f);
        g.FillEllipse(b, p.X - s, p.Y - s * 0.6f, s * 2, s * 1.2f);
    }

    private void DrawAgent(Graphics g, Agent a, Vec2 p, byte state, int tick)
    {
        var cam = ToCam(new Vec3(p.X, 0, p.Z));
        if (cam.Z < Near) return;
        var pb = Project(cam);
        var ptop = Project(ToCam(new Vec3(p.X, 1.75f, p.Z)));
        float sc = Math.Clamp(Focal / cam.Z, 0.02f, 400f);
        float h = MathF.Abs(pb.Y - ptop.Y);
        if (h < 0.7f) h = 0.7f;
        float w = h * 0.22f;

        bool dead = state == 6 || !a.Alive;
        Color body = dead ? Color.FromArgb(255, 58, 52, 58) : ColorFor(a, tick);
        if (dead) { w *= 2.0f; h *= 0.22f; }

        using var brush = new SolidBrush(body);
        if (dead)
            g.FillEllipse(brush, pb.X - w, pb.Y - h, w * 2, h * 2);
        else
        {
            // 躯干 + 头，够了。多了在 500 人下也看不清。
            g.FillRectangle(brush, pb.X - w, ptop.Y + h * 0.24f, w * 2, h * 0.76f);
            using var hb = new SolidBrush(Lighten(body, 0.15f));
            float hr = w * 0.72f;
            g.FillEllipse(hb, pb.X - hr, ptop.Y, hr * 2, hr * 2);
        }

        bool sel = a.Id == SelectedId, hov = a.Id == HoverId;
        if (sel || hov)
        {
            using var pen = new Pen(sel ? Color.FromArgb(255, 90, 230, 255) : Color.FromArgb(200, 255, 255, 255), sel ? 2f : 1f);
            g.DrawRectangle(pen, pb.X - w - 2, ptop.Y - 2, w * 2 + 4, h + 4);
        }

        // 名牌：15m 内给完整信息，更远只给编号（§7.5.2）
        float dist = cam.Z;
        if (dist < 60f && sc > 1.2f)
        {
            using var f = new Font("Consolas", Math.Clamp(sc * 0.22f, 6.5f, 12f));
            string txt = dist <= CrosshairRange
                ? $"#{a.Id} · {EmoName(a.Emo)} · {a.CoreTemp:0.0}°" + (a.Vitality < 60 ? " ⚠" : "")
                : $"#{a.Id}";
            var sz = g.MeasureString(txt, f);
            using var bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            g.FillRectangle(bg, pb.X - sz.Width / 2, ptop.Y - sz.Height - 3, sz.Width, sz.Height);
            using var tb = new SolidBrush(sel ? Color.FromArgb(255, 140, 240, 255) : Color.FromArgb(225, 235, 235, 240));
            g.DrawString(txt, f, tb, pb.X - sz.Width / 2, ptop.Y - sz.Height - 3);
        }
    }

    private static Color Lighten(Color c, float f)
        => Color.FromArgb(c.A, (int)Math.Min(255, c.R + 255 * f), (int)Math.Min(255, c.G + 255 * f), (int)Math.Min(255, c.B + 255 * f));

    private Color ColorFor(Agent a, int tick)
    {
        if (Over == Overlay.Emotion)
            return a.Emo switch
            {
                Emotion.Panic => Color.FromArgb(255, 220, 40, 40),
                Emotion.Fear => Color.FromArgb(255, 220, 130, 40),
                Emotion.Hurt => Color.FromArgb(255, 200, 40, 160),
                Emotion.Seeking => Color.FromArgb(255, 90, 130, 220),
                Emotion.Comfort => Color.FromArgb(255, 90, 200, 120),
                Emotion.Curious => Color.FromArgb(255, 220, 210, 90),
                _ => Color.FromArgb(255, 170, 172, 180),
            };
        if (Over == Overlay.Temperature)
        {
            float t = a.LocalTemp;
            float f = Math.Clamp((t - 12f) / 18f, 0f, 1f);
            return Blend(Color.FromArgb(255, 60, 110, 220), Color.FromArgb(255, 235, 70, 50), f);
        }
        if (Over == Overlay.Knowledge)
        {
            float k = a.ConfidenceOf(KnowledgeId.HeaterWarm);
            float h = a.ConfidenceOf(KnowledgeId.HeaterHot);
            if (h > k) return Color.FromArgb(255, 230, 90, 60);
            if (k > 0.3f) return Color.FromArgb(255, 90, 220, 240);
            return Color.FromArgb(255, 150, 152, 160);
        }
        // 默认：按情绪做底色，但把"冷得发抖"和"烫伤过"这两个故事点显出来
        if (a.CoreTemp < 35f) return Color.FromArgb(255, 90, 140, 230);
        if (a.BurnsSuffered > 0 && a.ConfidenceOf(KnowledgeId.HeaterHot) > 0.5f)
            return Color.FromArgb(255, 225, 130, 70);
        return a.Emo switch
        {
            Emotion.Comfort => Color.FromArgb(255, 110, 205, 130),
            Emotion.Seeking => Color.FromArgb(255, 200, 200, 120),
            _ => Color.FromArgb(255, 185, 188, 196),
        };
    }

    private static Color Blend(Color a, Color b, float t)
        => Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static string EmoName(Emotion e) => e switch
    {
        Emotion.Calm => "Calm", Emotion.Seeking => "Seeking", Emotion.Curious => "Curious",
        Emotion.Hurt => "Hurt", Emotion.Fear => "Fear", Emotion.Panic => "Panic",
        Emotion.Comfort => "Comfort", _ => "?",
    };

    // ---------- 俯视全景 ----------

    private void DrawTopDown(Graphics g, int tick)
    {
        float H = _r.Cfg.RoomHalf;
        float pad = 24f;   // 留白：插座就贴在 ±50 的墙上，padding 太小会被画到画面外面
        float scale = Math.Min((Width - pad * 2) / (H * 2), (Height - pad * 2) / (H * 2));
        float ox = Width / 2f, oy = Height / 2f;
        PointF M(float x, float z) => new(ox + x * scale, oy + z * scale);

        // 房间
        g.FillRectangle(new SolidBrush(Color.FromArgb(255, 30, 32, 38)), M(-H, -H).X, M(-H, -H).Y, H * 2 * scale, H * 2 * scale);

        if (Over == Overlay.Temperature || Over == Overlay.Density)
        {
            float step = 5f;
            for (float x = -H; x < H; x += step)
                for (float z = -H; z < H; z += step)
                {
                    float v;
                    if (Over == Overlay.Temperature)
                        v = Math.Clamp((_r.Sim.LocalTemperatureAt(new Vec2(x + step / 2, z + step / 2)) - 14f) / 12f, 0f, 1f);
                    else
                    {
                        int cnt = 0;
                        for (int i = 0; i < _r.Sim.Agents.Count; i++)
                        {
                            if (!_r.TryGetPose(_r.Sim.Agents[i].Id, tick, out var pp, out var st) || st == 6) continue;
                            if (MathF.Abs(pp.X - (x + step / 2)) < step / 2 && MathF.Abs(pp.Z - (z + step / 2)) < step / 2) cnt++;
                        }
                        v = Math.Clamp(cnt / 14f, 0f, 1f);
                    }
                    if (v <= 0.02f) continue;
                    var c = Blend(Color.FromArgb(255, 20, 40, 90), Color.FromArgb(255, 255, 90, 40), v);
                    var p0 = M(x, z);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(160, c)), p0.X, p0.Y, step * scale + 1, step * scale + 1);
                }
        }

        // 水池
        float ph = _r.Cfg.PoolHalf;
        var p0p = M(-ph, -ph);
        g.FillRectangle(new SolidBrush(Color.FromArgb(255, 16, 44, 76)), p0p.X, p0p.Y, ph * 2 * scale, ph * 2 * scale);
        g.DrawRectangle(new Pen(Color.FromArgb(160, 70, 160, 220), 1.5f), p0p.X, p0p.Y, ph * 2 * scale, ph * 2 * scale);

        // 插座（沿墙）
        foreach (var id in _r.Sim.SocketIds)
        {
            var s = _r.Sim.EntityById[id];
            if (s == null) continue;
            if (s.SState == SocketState.Empty) continue;
            var q = M(s.Pos.X, s.Pos.Z);
            var c = s.SState switch
            {
                SocketState.NailInLive => Color.FromArgb(255, 255, 60, 60),
                SocketState.NailInNeutral => Color.FromArgb(255, 80, 230, 120),
                _ => Color.FromArgb(255, 90, 170, 255),
            };
            g.FillRectangle(new SolidBrush(c), q.X - 2.5f, q.Y - 2.5f, 5, 5);
        }

        // 电暖气与火
        foreach (var id in _r.Sim.HeaterIds)
        {
            var e = _r.Sim.EntityById[id];
            if (e == null) continue;
            var q = M(e.Pos.X, e.Pos.Z);
            var c = e.Running ? Color.FromArgb(255, 255, 130, 60) : Color.FromArgb(255, 130, 132, 142);
            g.FillRectangle(new SolidBrush(c), q.X - 4 * scale / 6, q.Y - 4 * scale / 6, 8 * scale / 6, 8 * scale / 6);
            if (e.Running) g.DrawEllipse(new Pen(Color.FromArgb(120, 255, 120, 40), 1.5f), q.X - 4 * scale, q.Y - 4 * scale, 8 * scale, 8 * scale);
        }
        if (_r.Sim.FireBurning && _r.Sim.FireEntityId > 0)
        {
            var f = _r.Sim.EntityById[_r.Sim.FireEntityId];
            var q = M(f.Pos.X, f.Pos.Z);
            g.FillEllipse(new SolidBrush(Color.FromArgb(255, 255, 170, 50)), q.X - 6, q.Y - 6, 12, 12);
        }

        // 白痴
        for (int i = 0; i < _r.Sim.Agents.Count; i++)
        {
            var a = _r.Sim.Agents[i];
            if (!_r.TryGetPose(a.Id, tick, out var p, out var st)) continue;
            var q = M(p.X, p.Z);
            bool dead = st == 6;
            if (Over == Overlay.Deaths && !dead) continue;
            var c = dead ? Color.FromArgb(255, 90, 20, 20) : ColorFor(a, tick);
            float rr = dead ? 2.2f : 2.8f;
            g.FillEllipse(new SolidBrush(c), q.X - rr, q.Y - rr, rr * 2, rr * 2);
            if (a.Id == SelectedId)
                g.DrawEllipse(new Pen(Color.FromArgb(255, 90, 230, 255), 2f), q.X - 6, q.Y - 6, 12, 12);
        }

        g.DrawRectangle(new Pen(Color.FromArgb(200, 90, 95, 110), 2f), M(-H, -H).X, M(-H, -H).Y, H * 2 * scale, H * 2 * scale);
    }

    // =====================================================================
    //  准星拾取与 HUD
    // =====================================================================

    private void RecomputeHover(int tick)
    {
        if (Mode == ViewMode.TopDown) { HoverId = -1; return; }
        int best = -1; float bestScore = 0.06f;
        for (int i = 0; i < _r.Sim.Agents.Count; i++)
        {
            var a = _r.Sim.Agents[i];
            if (!_r.TryGetPose(a.Id, tick, out var p, out var st) || st == 6) continue;
            var c = ToCam(new Vec3(p.X, 1.0f, p.Z));
            if (c.Z < Near || c.Z > CrosshairRange * 1.6f) continue;
            var s = Project(c);
            float dx = (s.X - Width * 0.5f) / Width, dy = (s.Y - Height * 0.5f) / Height;
            float d2 = dx * dx + dy * dy;
            if (d2 < bestScore) { bestScore = d2; best = a.Id; }
        }
        HoverId = best;
    }

    private void DrawCrosshair(Graphics g)
    {
        float cx = Width / 2f, cy = Height / 2f;
        using var pen = new Pen(Color.FromArgb(170, 220, 230, 240), 1.4f);
        g.DrawLine(pen, cx - 8, cy, cx - 3, cy);
        g.DrawLine(pen, cx + 3, cy, cx + 8, cy);
        g.DrawLine(pen, cx, cy - 8, cx, cy - 3);
        g.DrawLine(pen, cx, cy + 3, cx, cy + 8);

        if (HoverId > 0)
        {
            var a = _r.Sim.Agents[HoverId - 1];
            string t = $"#{a.Id} · {EmoName(a.Emo)} · {a.CoreTemp:0.0}°C";
            using var f = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            var sz = g.MeasureString(t, f);
            g.FillRectangle(new SolidBrush(Color.FromArgb(190, 10, 14, 20)), cx - sz.Width / 2 - 6, cy + 14, sz.Width + 12, sz.Height + 4);
            g.DrawString(t, f, new SolidBrush(Color.FromArgb(255, 235, 245, 255)), cx - sz.Width / 2, cy + 16);
        }
    }

    private void DrawHud(Graphics g, int tick)
    {
        using var f = new Font("Consolas", 9.5f);
        using var b = new SolidBrush(Color.FromArgb(210, 200, 210, 225));
        string mode = Mode switch { ViewMode.Roam => "漫游观测", ViewMode.TopDown => "俯视全景", _ => "跟随个体" };
        string l1 = $"{mode}   WASD移动 Shift疾走 Space/Ctrl升降 右键拖动转视角";
        string l2 = _r.IsLive
            ? $"实时  {Simulation.FormatGameTime(tick, _r.Cfg)}  {_r.TicksPerRealSecond / 60f:0.0}×  ({_r.TicksPerRealSecond:0} tick/s)"
            : $"回看  {Simulation.FormatGameTime(tick, _r.Cfg)}  （已暂停）";
        string l3 = $"存活 {_r.AliveAt(tick)}/{_r.Cfg.AgentCount}   室温 {_r.Sim.GlobalTemp:0.00}°C   事件 {_r.Sim.Events.Count}";
        g.DrawString(l1, f, b, 10, 8);
        g.DrawString(l2, f, b, 10, 24);
        g.DrawString(l3, f, b, 10, 40);
    }
}
