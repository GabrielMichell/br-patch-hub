namespace BrPatchHub;

static class Program
{
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    static int Main(string[] args)
    {
        SetCurrentProcessExplicitAppUserModelID("GabrielMichell.BRPatchHub");
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
        if (args.Length == 4 && args[0].Equals("--render-test", StringComparison.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            return SelfTest.Render(args[1], int.Parse(args[2]), int.Parse(args[3]));
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
