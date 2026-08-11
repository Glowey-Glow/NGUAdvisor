using System;
using System.Globalization;
using System.IO;

namespace NGUAdvisor.Managers
{
    // WHICH BUILD IS ACTUALLY RUNNING, on each side of the pipe — and whether they agree.
    // Unity-free, linked into the test project.
    //
    // ── WHY THIS EXISTS ───────────────────────────────────────────────────────────────────────────
    // The advisor DLL and the companion UI are TWO projects with TWO Release-only deploy targets, and
    // only one of them is in NGUAdvisor.sln. `dotnet build NGUAdvisor.csproj -c Release` deploys the
    // DLL and says nothing about the UI; hot reload (F5) swaps the payload and never touches
    // wwwroot. So the two drift silently and nothing reports it.
    //
    // Found the hard way 2026-08-05/06: a setting was added, wired through SavedSettings, UiBridge and
    // index.html, deployed, reloaded — and the toggle was simply absent from the panel. The deployed
    // UI turned out to be SIX DAYS and SEVEN COMMITS stale. Every one of those commits was correct
    // and reviewed; none of them had ever reached the running app, and the only symptom was a control
    // that did not exist. Cost: a full round of "is the setting broken?" on a setting that was fine.
    //
    // This is the same shape as CompatibilityGate — it blocks nothing and changes no behaviour, it
    // just refuses to let a version mismatch be silent.
    //
    // It is the DETECTOR, not the fix. The fix is build/deploy.ps1: one command that stops the
    // companion, builds and deploys BOTH halves, and verifies the bytes landed. This class stays
    // because a deploy path can always be bypassed, and a bypass must not be silent.
    //
    // ── THE STAMP ─────────────────────────────────────────────────────────────────────────────────
    // NGUAdvisor.csproj bakes a unique identity per build so Mono's Assembly.Load(byte[]) cannot
    // dedupe a hot reload: AssemblyName = "NGUAdvisor.r" + yyMMddHHmmss (local time at build). That
    // stamp is therefore an exact build timestamp, already present, and needs no new plumbing.
    //
    // ── WHAT THE COMPANION HALF CAN AND CANNOT ANSWER (audit/42 §3.2, §3.3) ──────────────────────
    // The two halves measure different KINDS of thing: the advisor half is "what is running", the
    // companion half is a file on disk. Audit 42 found two ways that second read can be healthy and
    // wrong, and only one of them is closable here.
    //
    //   §3.2 THE WRONG FILE (closed — see CompanionExe / IndexHtmlIn / IsDeployedCopy below).
    //   The stat was hard-coded to <injectorDir>\companion\wwwroot\index.html, the DEPLOYED copy, on
    //   the assumption that the companion is always the auto-launched one. NGUAdvisorCompanion.csproj
    //   tells a developer to run it from its own bin\ instead, and MainForm maps
    //   AppContext.BaseDirectory\wwwroot — so the page ON SCREEN is then a different file entirely.
    //   Measured 2026-08-06: bin\Debug\...\wwwroot\index.html 565,706 bytes / 2026-07-31 against the
    //   deployed 607,179 / 2026-08-06. Six days old on screen; the gate read the healthy copy and said
    //   nothing. The fix is not a better heuristic, it is measuring the right thing (41 §7.4): stat the
    //   wwwroot beside the RUNNING companion exe. Still an advisor-side read — the advisor LOCATES the
    //   copy rather than asking the page what it is, so no constant in the page can lie about it.
    //
    //   §3.3 THE WRONG KIND OF NUMBER (NOT closable here — stated, not papered over).
    //   An mtime is not a content stamp. `git checkout` sets mtimes to checkout time, so a fresh clone
    //   or worktree of a STALE commit yields a wwwroot dated "now" (observed: 42 §2.2, a deployed
    //   index.html carrying the moment a worktree was created). Content that is behind the repo can
    //   therefore read as current, and no timestamp anywhere can tell the difference — the DLL carries
    //   no copy of the UI it expects, so at runtime there is no NEWER reference to compare against.
    //   Supplying one (baking the source wwwroot's hash into the DLL at build time) would answer it,
    //   but only as an EQUALITY test, and equality is what this gate deliberately does not do: it would
    //   fire on every hot reload taken while index.html is mid-edit, which is the normal development
    //   loop. The honest closure is a different measurement — the page reporting which [data-setting]
    //   keys it actually rendered, checked against what the advisor publishes, which detects the §7.3
    //   symptom (a setting present in the DLL and absent from the panel) directly and without clocks.
    //   That is a design decision, not a repair, and it is not made here.
    public static class BuildStamp
    {
        public const string Prefix = "NGUAdvisor.r";
        private const string StampFormat = "yyMMddHHmmss";

        // The process whose directory holds the wwwroot actually on screen (§3.2).
        public const string CompanionExe = "NGUAdvisorCompanion.exe";

        // "NGUAdvisor.r260806164329" -> 2026-08-06 16:43:29. Null when the name carries no stamp
        // (a hand-renamed DLL, or a build from before the stamped-identity scheme).
        public static DateTime? Parse(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName)) return null;
            int i = assemblyName.IndexOf(".r", StringComparison.Ordinal);
            if (i < 0) return null;
            var s = assemblyName.Substring(i + 2);
            if (s.Length != StampFormat.Length) return null;
            return DateTime.TryParseExact(s, StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt) ? dt : (DateTime?)null;
        }

        // Short human form for the nav footer: "2026-08-06 16:43".
        public static string Format(DateTime? t)
            => t.HasValue ? t.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "unknown";

        // How far behind the UI is. Positive = the UI is OLDER than the advisor.
        //
        // ⚠ TOLERANCE, NOT EQUALITY. The two are built by separate commands minutes or hours apart
        // even when both are current, and the companion's timestamp is a FILE mtime (preserved by the
        // copy — and often set by a `git checkout` rather than by a build, see §3.3 above) while the
        // advisor's is a build stamp — they will never match exactly. The question is "did someone
        // forget to ship the UI", and a day is the scale that answers it without firing on ordinary
        // same-session drift.
        public static readonly TimeSpan Tolerance = TimeSpan.FromHours(24);

        public static bool IsUiStale(DateTime? advisor, DateTime? ui)
        {
            // Unknown on either side is NOT stale. This gate exists to catch a real, measurable gap;
            // guessing from a missing value would cry wolf on every hand-renamed DLL and on the
            // developer builds that do not deploy the companion at all.
            if (!advisor.HasValue || !ui.HasValue) return false;
            return advisor.Value - ui.Value > Tolerance;
        }

        // The line for the log. Null when there is nothing to say — the caller logs only non-null, so
        // a healthy pair stays silent.
        public static string StaleMessage(DateTime? advisor, DateTime? ui)
        {
            if (!IsUiStale(advisor, ui)) return null;
            var days = (advisor.Value - ui.Value).TotalDays;
            // The fix names build/deploy.ps1 and NOT the bare companion build: the companion is running
            // whenever this message can be seen, and it holds a write lock on its own exe — so that
            // command fails with MSB3021 after it has already copied wwwroot, leaving a partial deploy.
            // The script stops it, ships both halves, and restarts it.
            return $"UI IS STALE: the companion was deployed {Format(ui)}, the advisor {Format(advisor)}"
                 + $" ({days:0.#} days older). Settings added since then will be MISSING from the panel."
                 + " Fix: run build\\deploy.ps1 (it ships the advisor AND the UI, and restarts the companion).";
        }

        // ── §3.2: WHICH COPY IS ON SCREEN ─────────────────────────────────────────────────────────
        // Pure path helpers, so the "which wwwroot" decision is testable without a companion running.

        // wwwroot\index.html beside a companion exe directory. Null in, null out — an unresolvable
        // directory must stay unknown rather than become a path that happens to exist.
        public static string IndexHtmlIn(string companionDir)
        {
            if (string.IsNullOrEmpty(companionDir)) return null;
            try { return Path.Combine(companionDir, "wwwroot", "index.html"); }
            catch { return null; }
        }

        // Is the copy being served the deployed one? Compared as paths, not as bytes: two copies at the
        // same path ARE the same file, and two copies at different paths are two files whose contents
        // this gate has no business equating (they diverge precisely when it matters).
        //
        // Unknown on either side answers FALSE — "not established to be a dev copy". Same rule as
        // IsUiStale: a missing value never becomes a warning.
        public static bool IsDevCopy(string servedIndexHtml, string deployedIndexHtml)
        {
            if (string.IsNullOrEmpty(servedIndexHtml) || string.IsNullOrEmpty(deployedIndexHtml)) return false;
            try
            {
                var a = Path.GetFullPath(servedIndexHtml);
                var b = Path.GetFullPath(deployedIndexHtml);
                return !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }   // an unparseable path is unknown, not a finding
        }

        // Footer text for the companion stamp. The nav rail is 246px and .bf-v is white-space:nowrap,
        // so the qualifier is three letters: "2026-07-31 12:04 (dev)". Without it the footer reports a
        // timestamp with no way to tell WHICH copy it belongs to — and on a dev machine that is the one
        // question worth asking. The log line carries the paths.
        public static string FormatCompanion(DateTime? t, bool devCopy)
            => devCopy ? Format(t) + " (dev)" : Format(t);

        // The dev-copy variant of StaleMessage. Same tolerance, same unknown rule, DIFFERENT FIX:
        // build\deploy.ps1 ships to <injectorDir>\companion\ and would not touch a bin\ copy at all, so
        // naming it here would be remedy advice that cannot work — the fault StaleMessage's own test
        // pins it against. Null when there is nothing to say.
        public static string DevCopyMessage(DateTime? advisor, DateTime? ui, string servedDir, string deployedDir)
        {
            if (string.IsNullOrEmpty(servedDir)) return null;
            var where = $"The companion is serving its UI from {servedDir}, NOT the deployed copy"
                      + (string.IsNullOrEmpty(deployedDir) ? "." : $" at {deployedDir}.");
            if (!IsUiStale(advisor, ui)) return where;
            var days = (advisor.Value - ui.Value).TotalDays;
            return "UI IS STALE: " + where
                 + $" That copy is dated {Format(ui)}, the advisor {Format(advisor)} ({days:0.#} days older);"
                 + " settings added since then will be MISSING from the panel."
                 + " Fix: close the companion and rebuild it (it holds a write lock on its own exe while"
                 + " running), or run the deployed copy instead.";
        }

        // ── THE BOOTSTRAP — A DIFFERENT QUESTION, AND THE ONLY ONE WORTH ASKING HERE ───────────────
        //
        // NGUAdvisorBootstrap.dll is artifact 4 of audit/42 §1 and rank 2 of §9: until 2026-08-06 it
        // was the only deployable in the tree with NO deploy target at all, in sync purely because the
        // project has ONE commit in its entire history. It has one now (NGUAdvisorBootstrap.csproj /
        // DeployBootstrap) and build/deploy.ps1 hash-verifies it, so the BUILT-vs-DEPLOYED gap is
        // closed repo-side, where both sides of that comparison actually exist.
        //
        // ⚠ THE STAMP MECHANISM ABOVE DOES NOT EXTEND TO IT, and could not be made to. The advisor's
        // build time is legible only because its ASSEMBLY NAME carries it, and the bootstrap's file
        // name is hardcoded by everything that injects it — NGUAdvisorLauncher/Program.cs passes
        // "-a .\injector\NGUAdvisorBootstrap.dll", and so do both Run NGU Advisor*.bat — so it cannot
        // be renamed per build. Stamping its assembly VERSION instead would make every build produce
        // different bytes, which destroys the deploy script's "the bytes did not change, so say
        // nothing" property. It stays deterministic and unstamped on purpose.
        //
        // What CAN be answered from here is the question the deploy script cannot see at all:
        //
        //      IS THIS GAME SESSION RUNNING THE BOOTSTRAP THAT IS ON DISK?
        //
        // The bootstrap is injected ONCE per session and F5 never replaces it: Main.PerformHotReload
        // walks the loaded assemblies for "NGUAdvisorBootstrap" and calls Boot.Reload() on the copy
        // ALREADY IN MEMORY, which byte-loads the payload THROUGH it. So a freshly deployed bootstrap
        // does nothing whatsoever until NGU Idle is restarted, and the only symptom is hot reload
        // behaving like the old build — audit/42 §8's "unattributable hot-reload misbehaviour",
        // precisely, and the reason that entry is ranked above the launcher.
        //
        // Both sides of THAT comparison are readable from where UiBridge runs:
        //
        //   on disk   File.GetLastWriteTime(<injectorDir>\NGUAdvisorBootstrap.dll) — the same folder
        //             the companion half already resolves. MSBuild's Copy preserves the source
        //             timestamp, so this is the bootstrap's BUILD time. Note it is therefore NOT
        //             exposed to audit/42 §2.2's trap, where a `git checkout` re-dates a file and the
        //             companion's mtime records when a worktree was created rather than what it holds.
        //   injected  the bootstrap's OWN log. Boot.Init writes "bootstrap up; payload path: ..."
        //             exactly once per injection and nothing else in that file says it; the reload
        //             lines say "reload requested" / "payload loaded and started".
        public const string BootstrapFile = "NGUAdvisorBootstrap.dll";
        private const string BootstrapUpMarker = "bootstrap up";
        private const string BootLogTimeFormat = "yyyy-MM-dd HH:mm:ss";

        // Boot.Log writes "{yyyy-MM-dd HH:mm:ss} {msg}" — SECOND resolution, so the recorded injection
        // instant can be up to a second EARLIER than the true one, while a file mtime is exact. A
        // minute of grace costs nothing (a deploy and an injection a minute apart are one act) and
        // removes the only way this can fire on a healthy session. Same principle as Tolerance above,
        // three orders of magnitude smaller because the two numbers here are meant to be comparable.
        public static readonly TimeSpan BootstrapGrace = TimeSpan.FromMinutes(1);

        // When the bootstrap now in memory was injected, from the tail of bootstrap.log. Reads the
        // LAST "bootstrap up" line, because the log spans sessions. Null when there is none — a
        // hand-injected session, a first run, or a truncated tail — and null is NOT stale.
        public static DateTime? ParseInjectionTime(string bootstrapLogText)
        {
            if (string.IsNullOrEmpty(bootstrapLogText)) return null;
            var lines = bootstrapLogText.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                // Trim handles the \r of the CRLF the bootstrap writes, and any leading partial line
                // left by reading only the tail of the file is rejected by the exact-format parse.
                var line = lines[i].Trim();
                if (line.Length < BootLogTimeFormat.Length) continue;
                if (line.IndexOf(BootstrapUpMarker, StringComparison.Ordinal) < 0) continue;
                DateTime dt;
                if (DateTime.TryParseExact(line.Substring(0, BootLogTimeFormat.Length), BootLogTimeFormat,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                    return dt;
            }
            return null;
        }

        // True when the file on disk was built AFTER this session injected — i.e. someone deployed a
        // bootstrap that this game is not running. Unknown on either side is NOT stale, same law as
        // IsUiStale: this fires on a measured gap or not at all.
        public static bool IsBootstrapStale(DateTime? injectedAt, DateTime? onDisk)
        {
            if (!injectedAt.HasValue || !onDisk.HasValue) return false;
            return onDisk.Value - injectedAt.Value > BootstrapGrace;
        }

        // Null when there is nothing to say, so a healthy session stays silent. The fix is a RESTART
        // and explicitly not a reload — naming the reload here would send someone round the exact
        // loop that produced the unattributable symptom in the first place.
        public static string BootstrapStaleMessage(DateTime? injectedAt, DateTime? onDisk)
        {
            if (!IsBootstrapStale(injectedAt, onDisk)) return null;
            return $"BOOTSTRAP IS STALE IN THIS SESSION: {BootstrapFile} on disk was built"
                 + $" {Format(onDisk)}, but this session injected the copy from {Format(injectedAt)}."
                 + " The bootstrap is injected ONCE per game session and F5 reloads the payload THROUGH"
                 + " the injected copy, never replacing it. Fix: RESTART NGU Idle and start the advisor"
                 + " again. Reloading will NOT pick it up.";
        }
    }
}
