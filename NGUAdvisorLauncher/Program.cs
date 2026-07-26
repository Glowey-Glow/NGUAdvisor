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
                string smi = Path.Combine(dir, "injector", "smi.exe");
                if (!File.Exists(smi))
                {
                    Console.Error.WriteLine("Could not find injector\\smi.exe next to this launcher.");
                    Console.Error.WriteLine("Run it from the extracted NGU Advisor folder.");
                    return Fail();
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
