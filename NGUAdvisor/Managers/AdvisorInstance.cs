using System;
using System.Text;

namespace NGUAdvisor.Managers
{
    // WHICH ADVISOR THE PIPES AND THE COMPANION MUTEX BELONG TO.
    // Unity-free, linked into the test project.
    //
    // ── WHY THIS EXISTS ───────────────────────────────────────────────────────────────────────────
    // Three names in this product were bare string constants, and all three are MACHINE-WIDE:
    //
    //   UiBridge          "NGUAdvisorUI"                  snapshot pipe   (advisor -> UI)
    //   UiBridge          "NGUAdvisorUICmd"               command pipe    (UI -> advisor)
    //   the companion     "NGUAdvisorCompanionSingleton"  single-instance mutex
    //
    // One game, one advisor, one UI — so a constant was right, and the mutex is what makes the
    // injector's auto-launch idempotent. It stops being right the moment a SECOND NGU Idle runs on the
    // machine, which is what NGU-Bench\ exists to do: load a user's save into an isolated copy of the
    // game and run the advisor against it while the live session keeps going.
    //
    // With bare constants that second advisor does not merely lack a UI. Its companion exits on the
    // mutex, and if one is started anyway it CONNECTS TO THE LIVE ADVISOR'S PIPE — a window that says
    // "bench" on the taskbar and shows the live character. Reading the wrong game's numbers is a
    // failure that looks exactly like a correct answer, which is the only kind worth a code change.
    //
    // ── THE CONTRACT ──────────────────────────────────────────────────────────────────────────────
    // An instance id comes from the NGUADVISOR_INSTANCE environment variable of the GAME process, and
    // suffixes all three names. The companion inherits it as a child process and is also handed it as
    // argv[1] (see Main.LaunchCompanionNow) so a hand-started companion can be pointed at a bench too.
    //
    //   unset / empty / all-punctuation  ->  "NGUAdvisorUI", "NGUAdvisorUICmd", "NGUAdvisorCompanionSingleton"
    //   "bench"                          ->  "NGUAdvisorUI-bench", "NGUAdvisorUICmd-bench", "...Singleton-bench"
    //
    // THE EMPTY CASE MUST STAY BYTE-IDENTICAL TO THE OLD CONSTANTS. Every existing install — the live
    // session, every public 2.4.0 user, the deploy script's companion restart — sets no such variable,
    // and a new advisor DLL will meet an OLD companion.exe (and vice versa) across any partial deploy.
    // Those two only find each other if the default name is unchanged, so the tests assert the literal
    // strings rather than re-deriving them.
    internal static class AdvisorInstance
    {
        public const string EnvVar = "NGUADVISOR_INSTANCE";

        public const string SnapshotPipeBase = "NGUAdvisorUI";
        public const string CommandPipeBase = "NGUAdvisorUICmd";
        public const string CompanionMutexBase = "NGUAdvisorCompanionSingleton";

        // Long enough to be legible ("bench", "melody-repro"), short enough that the result cannot
        // approach the pipe-name and mutex-name limits no matter what is in the variable.
        public const int MaxIdChars = 24;

        private static readonly string _id = Sanitize(ReadEnv());

        /// <summary>The sanitised instance id; "" for the default (live) instance.</summary>
        public static string Id { get { return _id; } }

        // The three names as pure functions of an id, so the tests can exercise the RULES for ids this
        // process does not have. The properties below are these applied to the ambient id.
        /// <remarks>
        /// Each base is decorated INDEPENDENTLY, rather than deriving the command pipe as the decorated
        /// snapshot name + "Cmd" the way PipeClient's one-argument constructor does. That convention
        /// aliases once ids exist: "NGUAdvisorUI-x" + "Cmd" is also the snapshot pipe of an instance
        /// called "xCmd", and the loser of that race fails to bind and shows an empty UI. Decorating the
        /// bases separately cannot collide for any pair of ids, since "NGUAdvisorUI-" and
        /// "NGUAdvisorUICmd-" differ before the id begins. The companion is handed both names explicitly
        /// (MainForm -> PipeClient's two-argument constructor) so the two sides agree by construction.
        /// </remarks>
        public static string SnapshotPipeFor(string id) { return Decorate(SnapshotPipeBase, id); }
        public static string CommandPipeFor(string id) { return Decorate(CommandPipeBase, id); }
        public static string CompanionMutexFor(string id) { return Decorate(CompanionMutexBase, id); }

        public static string SnapshotPipe { get { return SnapshotPipeFor(_id); } }

        public static string CommandPipe { get { return CommandPipeFor(_id); } }

        public static string CompanionMutex { get { return CompanionMutexFor(_id); } }

        private static string ReadEnv()
        {
            // A denied environment read must not take the bridge down with it — an advisor with no UI
            // is a bad day, and this runs during UiBridge's static init.
            try { return Environment.GetEnvironmentVariable(EnvVar); }
            catch { return null; }
        }

        /// <summary>
        /// Reduce an arbitrary string to something safe to paste into a pipe name and a mutex name.
        /// Keeps [A-Za-z0-9_-] and drops the rest; truncates to <see cref="MaxIdChars"/>.
        /// </summary>
        /// <remarks>
        /// Dropping rather than escaping, and dropping SILENTLY, is deliberate. A backslash is the
        /// namespace separator in both a mutex name ("Global\...") and a pipe path, so passing one
        /// through would not merely rename this advisor's objects — it could aim them at another
        /// namespace entirely. There is no id worth honouring that badly: an id is a label a human
        /// typed into a launcher, and the worst case of mangling one is two benches sharing a name,
        /// which is visible immediately. The worst case of honouring one is not.
        /// </remarks>
        public static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder(MaxIdChars);
            for (var i = 0; i < raw.Length && sb.Length < MaxIdChars; i++)
            {
                var c = raw[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Append an instance id to a base name. An empty id returns the base name unchanged.</summary>
        public static string Decorate(string baseName, string id)
        {
            return string.IsNullOrEmpty(id) ? baseName : baseName + "-" + id;
        }
    }
}
