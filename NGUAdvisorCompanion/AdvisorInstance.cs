// Explicit, though this project has ImplicitUsings on: the test project links this file with
// ImplicitUsings DISABLED, so the Environment calls below would not compile there without it.
using System;

namespace NGUAdvisorCompanion;

/// <summary>
/// Which advisor this companion talks to — the UI-side half of <c>NGUAdvisor\Managers\AdvisorInstance.cs</c>.
/// </summary>
/// <remarks>
/// DELIBERATELY NOT the linked injector file. That one is C# 7.3 net48 source compiled into the DLL that
/// lives inside the game's Mono domain; this project already links three injector sources and pays for it
/// with <c>&lt;Nullable&gt;disable&lt;/Nullable&gt;</c>. More to the point, the two halves READ the id from
/// different places — the injector from its own environment, this from argv first — so a shared file would
/// be a shared shell around two different bodies. What must not drift is the four literals below, and the
/// test suite links BOTH files and asserts they agree.
///
/// Resolution order, first hit wins:
///
///   argv[1]                 passed by Main.LaunchCompanionNow, and the only source that survives a
///                           companion started by hand from a shortcut or by build\deploy.ps1's restart.
///   NGUADVISOR_INSTANCE     inherited from the game process when the advisor auto-launched us. Kept as a
///                           fallback so a companion launched WITHOUT the argument (an older advisor DLL
///                           meeting a newer companion.exe, which is a normal state during a partial
///                           deploy) still lands on the right pipes.
///   nothing                 the default instance: names byte-identical to the pre-instance constants.
///
/// A default-instance companion is indistinguishable from every 2.4.0 companion in the wild. That is the
/// property to preserve when editing this file.
/// </remarks>
internal static class AdvisorInstance
{
    public const string EnvVar = "NGUADVISOR_INSTANCE";

    public const string SnapshotPipeBase = "NGUAdvisorUI";
    public const string CommandPipeBase = "NGUAdvisorUICmd";
    public const string CompanionMutexBase = "NGUAdvisorCompanionSingleton";

    public const int MaxIdChars = 24;

    private static readonly string _id = Resolve();

    /// <summary>The sanitised instance id; "" for the default (live) instance.</summary>
    public static string Id => _id;

    /// <summary>
    /// The two pipe names, passed to <see cref="PipeClient"/>'s TWO-argument constructor. Not the
    /// one-argument one: that derives the command pipe as snapshot + "Cmd", which aliases once ids
    /// exist ("NGUAdvisorUI-x" + "Cmd" is instance "xCmd"'s snapshot pipe). Each base is decorated
    /// independently on both sides instead.
    /// </summary>
    public static string SnapshotPipe => SnapshotPipeFor(_id);

    public static string CommandPipe => CommandPipeFor(_id);

    public static string CompanionMutex => CompanionMutexFor(_id);

    // Pure functions of an id, so the tests can compare these rules against the injector's for ids
    // neither process has. AdvisorInstanceTests links both files and asserts they agree.
    public static string SnapshotPipeFor(string id) => Decorate(SnapshotPipeBase, id);
    public static string CommandPipeFor(string id) => Decorate(CommandPipeBase, id);
    public static string CompanionMutexFor(string id) => Decorate(CompanionMutexBase, id);

    private static string Resolve()
    {
        try
        {
            // GetCommandLineArgs()[0] is this exe; [1] is the game pid; [2] is the instance id.
            var argv = Environment.GetCommandLineArgs();
            if (argv.Length > 2)
            {
                var fromArg = Sanitize(argv[2]);
                if (fromArg.Length > 0) return fromArg;
            }
        }
        catch { /* fall through to the environment */ }

        try { return Sanitize(Environment.GetEnvironmentVariable(EnvVar)); }
        catch { return ""; }
    }

    /// <summary>Keeps [A-Za-z0-9_-], drops the rest, truncates to <see cref="MaxIdChars"/>.</summary>
    /// <remarks>Must match NGUAdvisor.Managers.AdvisorInstance.Sanitize byte for byte; see the tests.</remarks>
    public static string Sanitize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new System.Text.StringBuilder(MaxIdChars);
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
    public static string Decorate(string baseName, string id) =>
        string.IsNullOrEmpty(id) ? baseName : baseName + "-" + id;
}
