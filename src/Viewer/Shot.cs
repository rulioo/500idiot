using IdiotSim;
using IdiotSim.Recording;

namespace IdiotSim.Viewer;

/// <summary>
/// 离屏截图（开发用）：把三种视图各渲一张 PNG，用来肉眼确认软件渲染器没画歪。
/// 不参与正常运行，只在 --shot 时走这条路。
/// </summary>
public static class Shot
{
    public static int Run(string[] args)
    {
        float minutes = 60f;
        int seed = 20261003;
        string outDir = "shots";
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--minutes" && i + 1 < args.Length) float.TryParse(args[++i], out minutes);
            else if (args[i] == "--seed" && i + 1 < args.Length) int.TryParse(args[++i], out seed);
            else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
        }
        Directory.CreateDirectory(outDir);

        var cfg = SimConfig.Default;
        cfg.WorldSeed = seed;
        var runner = new SimRunner(cfg, minutes * 60f);

        Console.WriteLine($"仿真到 {minutes} 游戏分钟…");
        runner.SkipTo(cfg.SecondsToTicks(minutes * 60f));
        runner.Paused = true;

        var sim = runner.Sim;
        Console.WriteLine($"  tick={sim.Tick}  存活={sim.AliveCount}  室温={sim.GlobalTemp:0.00}°C  " +
                          $"G1={sim.T_FirstHeaterOn} G2={sim.T_FirstFire}");

        int W = 1280, H = 760;
        void Shoot(string name, Action<SceneView> setup)
        {
            using var view = new SceneView(runner) { Width = W, Height = H };
            view.CreateControl();
            setup(view);
            using var bmp = new Bitmap(W, H);
            view.DrawToBitmap(bmp, new Rectangle(0, 0, W, H));
            string path = Path.Combine(outDir, name + ".png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"  → {path}");
        }

        // 站在房间南侧朝北看，能同时看到水池、电暖气和水池边的白痴
        Shoot("roam-heater", v =>
        {
            v.Mode = ViewMode.Roam;
            v.Eye = new Vec3(6f, 1.7f, 26f);
            v.Yaw = MathF.PI; v.Pitch = -0.10f;
            v.HeatZones = true;
            v.SelectedId = sim.T_FirstHeaterOn > 0 ? 1 : -1;
        });

        Shoot("roam-pool", v =>
        {
            v.Mode = ViewMode.Roam;
            v.Eye = new Vec3(0f, 2.2f, 16f);
            v.Yaw = MathF.PI; v.Pitch = -0.25f;
        });

        // 贴近墙看插座
        Shoot("roam-sockets", v =>
        {
            v.Mode = ViewMode.Roam;
            float H2 = cfg.RoomHalf;
            v.Eye = new Vec3(-40f, 1.7f, H2 - 6f);
            v.Yaw = MathF.PI * 0.5f; v.Pitch = 0.02f;
        });

        Shoot("topdown", v =>
        {
            v.Mode = ViewMode.TopDown;
            v.Over = Overlay.Temperature;
        });

        Shoot("topdown-knowledge", v =>
        {
            v.Mode = ViewMode.TopDown;
            v.Over = Overlay.Knowledge;
        });

        runner.Dispose();
        Console.WriteLine("完成。");
        return 0;
    }
}
