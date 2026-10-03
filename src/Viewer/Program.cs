namespace IdiotSim.Viewer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 无头模式：IdiotSim.exe --headless --minutes 1440 [--seed 20261003] [--quiet]
        if (args.Length > 0 && (args[0] == "--headless" || args[0] == "-h"))
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            return Headless.Run(args);
        }

        // 离屏截图（开发用）：IdiotSim.exe --shot --minutes 60 --out shots
        if (args.Length > 0 && args[0] == "--shot")
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            return Shot.Run(args);
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
