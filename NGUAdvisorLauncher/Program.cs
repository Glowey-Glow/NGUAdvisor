using System;
using System.Diagnostics;
using System.IO;

namespace NGUAdvisorLauncher
{
    // Public launcher: direct-injects NGUAdvisor.dll (no bootstrap / no hot-reload). Carries the advisor
    // icon and runs from its own folder. On failure it pauses so the error stays readable. The injected
    // advisor auto-launches the companion (injector\companion\NGUAdvisorCompanion.exe).
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                string injector = Path.Combine(dir, "injector");
                string smi = Path.Combine(injector, "smi.exe");
                if (!File.Exists(smi))
                {
                    Console.Error.WriteLine("Could not find injector\\smi.exe next to this launcher.");
                    Console.Error.WriteLine("Run it from the extracted NGU Advisor folder.");
                    return Fail();
                }

                // The advisor is byte-loaded by smi, so Assembly.Location is empty in-game — write it our
                // injector path so it can find injector\companion\NGUAdvisorCompanion.exe (auto-launch + F1).
                // "Run NGU Advisor.bat" writes the same file; keep the two in sync.
                try
                {
                    string low = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "AppData", "LocalLow", "NGUAdvisor");
                    Directory.CreateDirectory(low);
                    File.WriteAllText(Path.Combine(low, "injector-path.txt"), injector);
                }
                catch (Exception e)
                {
                    // Non-fatal: the advisor still injects, only the companion auto-launch/F1 is lost.
                    Console.Error.WriteLine("Warning: could not write injector-path.txt (" + e.Message +
                                            ") - the companion window may not open.");
                }

                var psi = new ProcessStartInfo(smi,
                    "inject -p NGUIdle -a .\\injector\\NGUAdvisor.dll -n NGUAdvisor -c Loader -m Init")
                {
                    WorkingDirectory = dir,
                    UseShellExecute = false
                };

                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("Injection failed — is NGU Idle running?");
                        return Fail(p.ExitCode);
                    }
                    return 0;   // success: close promptly
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Launcher error: " + e.Message);
                return Fail();
            }
        }

        private static int Fail(int code = 1)
        {
            try { Console.Error.WriteLine("Press any key to close..."); Console.ReadKey(true); } catch { }
            return code;
        }
    }
}
