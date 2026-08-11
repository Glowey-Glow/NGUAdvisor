using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace NGUAdvisor.Tests
{
    // DEV INSTRUMENTS MUST NOT SHIP ARMED.
    //
    // ConstantCapture is the case this was written for. It is a one-shot read of the Unity-scene-serialized
    // game constants into inject.log — the values that exist only in the scene, so the decompile shows the
    // declaration and never the number. It produced audit 08, 11, 16 and 19, and its own header said "remove
    // once audit/08-captured-constants.md is written". 08 was written on 2026-08-02 and the call was still
    // there at e12de0d, five days and ~90 commits later, running unguarded on every launch.
    //
    // MEASURED on the operator's own install, 2026-08-07: 358 lines and ~60 KB per launch; 3,222 lines across
    // 9 launches; 9.2% of a 5.9 MB inject.log. Not a correctness risk — a dev instrument billed to players.
    //
    // THE DECISION THIS PINS IS TWO-SIDED, and both halves matter:
    //   * the CALL is gone, so it stops shipping armed;
    //   * the CLASS is KEPT, because it is the only way the constants get re-measured after a game patch
    //     moves them. CompatibilityGate DETECTS a changed game build; ConstantCapture is what RE-MEASURES
    //     after one. Re-arming is restoring one line at the marker in Main.Start.
    //
    // So a failure here is not automatically a bug — if someone re-armed it deliberately for a capture
    // session, this test is the reminder to disarm before the build ships.
    public class DevInstrumentTests
    {
        private static string RepoRoot([CallerFilePath] string here = null)
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !Directory.Exists(Path.Combine(dir, "NGUAdvisor", "Presets")))
                dir = Path.GetDirectoryName(dir);
            return dir;
        }

        // Line comments only. The call site is surrounded by a comment block that NAMES the call so it can be
        // restored, so a raw text search would match its own instructions — stripping `//` is what separates
        // the program from the note about the program. A `//` inside a string literal would truncate that
        // line early and could only ever UNDER-report, never invent a call that is not there.
        private static string CodeOnly(string source) =>
            string.Join("\n", source.Split('\n').Select(line =>
            {
                var i = line.IndexOf("//", StringComparison.Ordinal);
                return i >= 0 ? line.Substring(0, i) : line;
            }));

        [Fact]
        public void Main_does_not_ship_with_the_constant_capture_instrument_armed()
        {
            var main = Path.Combine(RepoRoot(), "NGUAdvisor", "Main.cs");
            Assert.True(File.Exists(main), "Main.cs moved — this guard has nothing to check");

            var code = CodeOnly(File.ReadAllText(main));
            Assert.False(code.Contains("ConstantCapture.Run("),
                "Main.cs calls ConstantCapture.Run() — that is a dev instrument and it writes ~358 lines " +
                "(~60 KB) into every player's inject.log on every launch. If this was re-armed on purpose " +
                "for a capture session, disarm it before the build ships.");
        }

        // The other half. Removing the call is not removing the class, and a later "tidy up the dead code"
        // pass would delete the only instrument that can re-measure scene-serialized constants.
        [Fact]
        public void The_constant_capture_instrument_itself_is_kept_for_re_measurement()
        {
            var instrument = Path.Combine(RepoRoot(), "NGUAdvisor", "Managers", "ConstantCapture.cs");
            Assert.True(File.Exists(instrument),
                "ConstantCapture.cs is gone. The CALL was retired deliberately; the CLASS is retained because " +
                "every value it reads exists only in the Unity scene — the decompile shows the declaration and " +
                "never the number, so if a game patch moves one, this file is how it gets re-measured.");
        }
    }
}
