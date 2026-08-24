namespace CentralPtBr;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 5 && args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            return UpdateInstaller.ApplyAndRestart(args[1], args[2], int.Parse(args[3]), args[4]);
        }
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase)) return SelfTest.Run();
        if (args.Length == 2 && args[0].Equals("--render-test", StringComparison.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            return SelfTest.Render(args[1]);
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
