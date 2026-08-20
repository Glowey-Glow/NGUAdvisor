using System.Diagnostics;

namespace NGUAdvisorCompanion;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Single instance PER ADVISOR INSTANCE: the injector auto-launches this on load, and the user may
        // also start it — the second start for the SAME instance exits immediately. The mutex is what makes
        // injector auto-launch idempotent. It is suffixed (AdvisorInstance) so that a bench game's companion
        // is not mistaken for the live one and silently killed at startup; unsuffixed, "one companion per
        // machine" would make an isolated bench UI impossible.
        using var mutex = new System.Threading.Mutex(true, AdvisorInstance.CompanionMutex, out bool created);
        if (!created) return;

        // Lifecycle: the injector passes the GAME's process id as arg[0]. Exit when the game exits, so the
        // companion doesn't linger after NGU closes — a lingering instance also holds the singleton mutex,
        // which blocks the next session's fresh auto-launch and leaves stale data on screen (user-reported).
        WatchGame(args);

        // Source-generated (UseWindowsForms) — applies the PerMonitorV2 high-DPI mode from the csproj.
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void WatchGame(string[] args)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out int pid)) return;
        try
        {
            var game = Process.GetProcessById(pid);
            var t = new Thread(() =>
            {
                try { game.WaitForExit(); } catch { }
                Environment.Exit(0);   // game gone -> close the companion (background threads need no teardown)
            })
            { IsBackground = true, Name = "GameWatch" };
            t.Start();
        }
        catch { /* pid already gone or invalid: just run normally (FormClosed still disposes the pipe) */ }
    }
}
