using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;
using SimpleJSON;

namespace NGUAdvisor.Managers
{
    /// <summary>
    /// Out-of-process modern-UI bridge — snapshots out (M1), commands in (M2).
    ///
    /// The injector stays headless: it publishes a JSON snapshot of advisor state over a named pipe
    /// ("NGUAdvisorUI") that the WebView2 companion host renders, and applies command JSON the UI sends back.
    ///
    /// Threading contract (mirrors the codebase's FileSystemWatcher -> volatile-flag pattern):
    ///   * <see cref="Publish"/> is called ONLY from the Unity main thread (Main's InvokeRepeating tick).
    ///     ALL game/Unity reads happen there, while building the JSON string.
    ///   * Two UNIDIRECTIONAL pipes (each synchronous): <see cref="PumpLoop"/> WRITES snapshots on
    ///     "NGUAdvisorUI"; <see cref="CmdLoop"/> READS command lines on "NGUAdvisorUICmd" and only
    ///     ENQUEUES them. Separate handles, so a parked read never serializes the writer (a single
    ///     synchronous duplex handle deadlocks; async pipes are unreliable under Mono). Neither pipe
    ///     thread ever touches Unity objects.
    ///   * <see cref="DrainCommands"/> applies queued commands on the Unity main thread (Main.Update),
    ///     each in its own try/catch — the AdvisorApply.RunStep containment pattern.
    ///
    /// Mono teardown lesson (from the WAMIAdvisor pipe bridge): Dispose cannot interrupt a thread parked
    /// in the native WaitForConnection(); we must ALSO "poke" the pipe with a throwaway local client so
    /// the accept thread unblocks — a thread stuck in native pipe code during runtime teardown crashes
    /// the process on exit.
    /// </summary>
    internal sealed class UiBridge : IDisposable
    {
        private const string PipeName = "NGUAdvisorUI";       // snapshots: injector -> UI (write-only)
        private const string CmdPipeName = "NGUAdvisorUICmd"; // commands: UI -> injector (read-only)
        private const int ProtocolVersion = 1;
        private const int SparkPoints = 24;
        private const int FeedMax = 20;
        // M7 (audit): a command is small JSON — cap the buffered line so a client that never sends a
        // newline can't grow memory without limit (DoS). Over-long lines are dropped.
        private const int MaxCommandLineChars = 65536;
        // M7: cap ids accepted in one setSettingList so a crafted command can't build an oversized array.
        private const int MaxListIds = 4096;

        private Thread _pumpThread;
        private volatile bool _stopping;
        private volatile string _latest;                 // newest snapshot JSON; atomic ref swap
        private volatile string _pumpError;              // error stashed by the pipe thread; logged on the main thread
        private volatile bool _pipeSecUnavailable;       // M7: set on the pipe thread if the ACL overload isn't supported (Mono)
        private volatile bool _secNoteLogged;            // M7: guards the one-time main-thread "ACL unavailable" log
        private volatile NamedPipeServerStream _server;     // snapshot (out) accept/serve stream
        private volatile NamedPipeServerStream _cmdServer;  // command (in) accept/serve stream
        private Thread _cmdThread;                          // command accept/read loop
        private long _seq;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly ConcurrentQueue<string> _inbound = new ConcurrentQueue<string>();  // command lines from the UI

        // Bridge-owned feed ring: the advisor has no multi-entry feed (Activity is a single slot),
        // so we accumulate distinct Activity.Current outcomes here, newest last.
        private readonly LinkedList<JSONObject> _feed = new LinkedList<JSONObject>();
        private int _lastActivitySeq = int.MinValue;
        private string[] _profilesCache;                 // profile names, refreshed ~every 5s (avoid per-tick disk)
        // One-shot "show this view" request from an in-game hotkey (F9 -> Profile Editor). Snapshots repeat
        // every second, so the page acts on a CHANGE of `seq`; the node also expires (NavTtlSnaps) so a
        // companion that opens minutes later doesn't jump to a view the user asked for long ago.
        private string _navView;
        private long _navSeq;
        private long _navUntilSeq;
        private const int NavTtlSnaps = 15;              // ~15 s at one snapshot/second
        private JSONObject _macguffinsCache;             // static macguffin id->name map (FavoredMacguffin dropdown); built once
        private JSONObject _zonesCache;                  // static non-titan zone id->name map (SnipeZone / GearHuntZone dropdowns); built once
        private JSONObject _advEnemiesCache;             // static adventure enemy spriteId->name map (blacklist picker); built once
        private JSONArray _gearObjectivesCache;          // static gear-objective name list (loadout Advisor dropdowns); built once
        private JSONArray _transformMetaCache;           // transform-chain {name, step} descriptors (grid labels); refreshed ~every 5s
        private JSONObject _cardMetaCache;               // static card meta (bonus types / rarities / costs / sort vocab); built once

        // Bridge-sampled sparkline rings: one growth-rate value per publish, per metric.
        private readonly Dictionary<string, Queue<double>> _spark = new Dictionary<string, Queue<double>>();

        // Per-system two-layer toggles: AUTOMATION (act gate = Manage*/Auto*/Cast*/Combat*) and DECISIONS
        // (advisor strategy = Advisor*). Both are change-guarded auto-save setters; all ANDed with the
        // GlobalEnabled master. One table drives both the snapshot (read state) and commands (apply).
        private sealed class SysToggle
        {
            public readonly string Id;
            public readonly Func<SavedSettings, bool> GetAuto;
            public readonly Action<SavedSettings, bool> SetAuto;
            public readonly Func<SavedSettings, bool> GetAdvisor;
            public readonly Action<SavedSettings, bool> SetAdvisor;
            public SysToggle(string id, Func<SavedSettings, bool> ga, Action<SavedSettings, bool> sa,
                             Func<SavedSettings, bool> gd, Action<SavedSettings, bool> sd)
            { Id = id; GetAuto = ga; SetAuto = sa; GetAdvisor = gd; SetAdvisor = sd; }
        }

        private static readonly SysToggle[] Toggles =
        {
            new SysToggle("titans",    s => s.ManageTitans,       (s, v) => s.ManageTitans = v,       s => s.AdvisorTitans,      (s, v) => s.AdvisorTitans = v),
            new SysToggle("adventure", s => s.CombatEnabled,      (s, v) => s.CombatEnabled = v,      s => s.AdvisorZones,       (s, v) => s.AdvisorZones = v),
            new SysToggle("gear",      s => s.ManageGear,         (s, v) => s.ManageGear = v,         s => s.AdvisorGearRefresh, (s, v) => s.AdvisorGearRefresh = v),
            new SysToggle("wandoos",   s => s.ManageWandoos,      (s, v) => s.ManageWandoos = v,      s => s.AdvisorWandoosOS,   (s, v) => s.AdvisorWandoosOS = v),
            new SysToggle("diggers",   s => s.ManageDiggers,      (s, v) => s.ManageDiggers = v,      s => s.AdvisorDiggers,     (s, v) => s.AdvisorDiggers = v),
            new SysToggle("beards",    s => s.ManageBeards,       (s, v) => s.ManageBeards = v,       s => s.AdvisorBeards,      (s, v) => s.AdvisorBeards = v),
            new SysToggle("yggdrasil", s => s.ManageYggdrasil,    (s, v) => s.ManageYggdrasil = v,    null,                      null),
            new SysToggle("blood",     s => s.CastBloodSpells,    (s, v) => s.CastBloodSpells = v,    s => s.AdvisorBlood,       (s, v) => s.AdvisorBlood = v),
            new SysToggle("gold",      s => s.ManageGoldLoadouts, (s, v) => s.ManageGoldLoadouts = v, s => s.AdvisorGold,        (s, v) => s.AdvisorGold = v),
            new SysToggle("boosts",    s => s.ManageInventory,    (s, v) => s.ManageInventory = v,    s => s.AutoBoostPriority,  (s, v) => s.AutoBoostPriority = v),
            new SysToggle("inventory", s => s.ManageInventory,    (s, v) => s.ManageInventory = v,    null,                      null),
            new SysToggle("quests",    s => s.AutoQuest,          (s, v) => s.AutoQuest = v,          s => s.AdvisorQuests,      (s, v) => s.AdvisorQuests = v),
            new SysToggle("moneypit",  s => s.AutoMoneyPit,       (s, v) => s.AutoMoneyPit = v,       s => s.AdvisorPit,         (s, v) => s.AdvisorPit = v),
            new SysToggle("exp",       null,                      null,                               s => s.AdvisorExpBuys,     (s, v) => s.AdvisorExpBuys = v),
            // Resources is 3 independent gates (Energy/Magic/R3): report ON only when all three are on,
            // and toggle all three together so "resources automation off" actually stops all allocation.
            new SysToggle("resources", s => s.ManageEnergy && s.ManageMagic && s.ManageR3,
                                       (s, v) => { s.ManageEnergy = v; s.ManageMagic = v; s.ManageR3 = v; }, null, null),
        };

        private static SysToggle FindToggle(string id)
        {
            foreach (var t in Toggles) if (t.Id == id) return t;
            return null;
        }

        // ---- scalar setting bindings (W1): bool / int / double over SavedSettings ----
        // One registry drives BOTH the snapshot's `settings` node (read current value) and the
        // setSetting command (coerce + clamp + write). Every SavedSettings setter is change-guarded
        // and auto-saves, so we only assign — never SaveSettings ourselves.
        private sealed class Binding
        {
            public readonly string Key;
            public readonly Action<JSONObject, SavedSettings> Write;   // settings[Key] = current value
            public readonly Action<SavedSettings, JSONNode> Apply;     // coerce + clamp + set
            private Binding(string key, Action<JSONObject, SavedSettings> w, Action<SavedSettings, JSONNode> a)
            { Key = key; Write = w; Apply = a; }

            public static Binding Bool(string key, Func<SavedSettings, bool> g, Action<SavedSettings, bool> s)
                => new Binding(key, (o, st) => o[key] = g(st),
                                    (st, v) => { if (v != null && v.IsBoolean) s(st, v.AsBool); });
            public static Binding Int(string key, Func<SavedSettings, int> g, Action<SavedSettings, int> s, int min, int max)
                => new Binding(key, (o, st) => o[key] = g(st),
                                    (st, v) => { if (v != null) s(st, (int)Math.Round(Math.Min(Math.Max(v.AsDouble, min), max))); });
            public static Binding Dbl(string key, Func<SavedSettings, double> g, Action<SavedSettings, double> s, double min, double max)
                => new Binding(key, (o, st) => o[key] = g(st),
                                    (st, v) => { if (v != null) s(st, Math.Min(Math.Max(v.AsDouble, min), max)); });
            public static Binding Str(string key, Func<SavedSettings, string> g, Action<SavedSettings, string> s)
                => new Binding(key, (o, st) => o[key] = g(st) ?? "",
                                    (st, v) => { if (v != null && v.IsString) s(st, v.Value); });
        }

        // W2: every simple scalar control across all systems (bool / int / double / enum-as-int).
        // The two-layer per-system gates also live in Toggles (setAutomation/setLayer) — binding them
        // here too lets the flat Settings grid write them by key; both hit the same change-guarded setter.
        // List/grid editors (blacklists, kill grid, priority lists, loadout IDs, card grids) are W3;
        // the 12 internal-state fields are intentionally NOT bound.
        private static readonly Binding[] BindingList =
        {
            // Global / Settings
            Binding.Bool("GlobalEnabled",       s => s.GlobalEnabled,       (s, v) => s.GlobalEnabled = v),
            Binding.Bool("AutoProfile",         s => s.AutoProfile,         (s, v) => s.AutoProfile = v),
            Binding.Bool("Autosave",            s => s.Autosave,            (s, v) => s.Autosave = v),
            Binding.Bool("DisableOverlay",      s => s.DisableOverlay,      (s, v) => s.DisableOverlay = v),
            Binding.Bool("LaunchCompanion",     s => s.LaunchCompanion,     (s, v) => s.LaunchCompanion = v),
            Binding.Bool("AdvisorShowOptimal",  s => s.AdvisorShowOptimal,  (s, v) => s.AdvisorShowOptimal = v),
            Binding.Dbl ("DiggerCap",           s => s.DiggerCap,           (s, v) => s.DiggerCap = v, 0, 100),
            // Automation gates
            Binding.Bool("ManageEnergy",        s => s.ManageEnergy,        (s, v) => s.ManageEnergy = v),
            Binding.Bool("ManageMagic",         s => s.ManageMagic,         (s, v) => s.ManageMagic = v),
            Binding.Bool("ManageR3",            s => s.ManageR3,            (s, v) => s.ManageR3 = v),
            Binding.Bool("ManageWandoos",       s => s.ManageWandoos,       (s, v) => s.ManageWandoos = v),
            Binding.Bool("ManageNGUDiff",       s => s.ManageNGUDiff,       (s, v) => s.ManageNGUDiff = v),
            Binding.Bool("ManageDiggers",       s => s.ManageDiggers,       (s, v) => s.ManageDiggers = v),
            Binding.Bool("ManageBeards",        s => s.ManageBeards,        (s, v) => s.ManageBeards = v),
            Binding.Bool("ManageGear",          s => s.ManageGear,          (s, v) => s.ManageGear = v),
            Binding.Bool("ManageYggdrasil",     s => s.ManageYggdrasil,     (s, v) => s.ManageYggdrasil = v),
            Binding.Bool("ManageTitans",        s => s.ManageTitans,        (s, v) => s.ManageTitans = v),
            Binding.Bool("ManageInventory",     s => s.ManageInventory,     (s, v) => s.ManageInventory = v),
            Binding.Bool("ManageConsumables",   s => s.ManageConsumables,   (s, v) => s.ManageConsumables = v),
            Binding.Bool("ManageWishes",        s => s.ManageWishes,        (s, v) => s.ManageWishes = v),
            Binding.Bool("ManageMayo",          s => s.ManageMayo,          (s, v) => s.ManageMayo = v),
            Binding.Bool("ManageCooking",       s => s.ManageCooking,       (s, v) => s.ManageCooking = v),
            Binding.Bool("ManageCookingLoadouts", s => s.ManageCookingLoadouts, (s, v) => s.ManageCookingLoadouts = v),
            Binding.Bool("ManageGoldLoadouts",  s => s.ManageGoldLoadouts,  (s, v) => s.ManageGoldLoadouts = v),
            Binding.Bool("ManageQuestLoadouts", s => s.ManageQuestLoadouts, (s, v) => s.ManageQuestLoadouts = v),
            Binding.Bool("CastBloodSpells",     s => s.CastBloodSpells,     (s, v) => s.CastBloodSpells = v),
            Binding.Bool("CombatEnabled",       s => s.CombatEnabled,       (s, v) => s.CombatEnabled = v),
            Binding.Bool("AutoQuest",           s => s.AutoQuest,           (s, v) => s.AutoQuest = v),
            // AUTO
            Binding.Bool("AutoFight",           s => s.AutoFight,           (s, v) => s.AutoFight = v),
            Binding.Bool("AutoRebirth",         s => s.AutoRebirth,         (s, v) => s.AutoRebirth = v),
            Binding.Bool("AutoConvertBoosts",   s => s.AutoConvertBoosts,   (s, v) => s.AutoConvertBoosts = v),
            Binding.Bool("AutoTitanGold",       s => s.AutoTitanGold,       (s, v) => s.AutoTitanGold = v),
            Binding.Bool("UpgradeDiggers",      s => s.UpgradeDiggers,      (s, v) => s.UpgradeDiggers = v),
            Binding.Bool("AutoBuyEM",           s => s.AutoBuyEM,           (s, v) => s.AutoBuyEM = v),
            Binding.Bool("AutoBuyAdventure",    s => s.AutoBuyAdventure,    (s, v) => s.AutoBuyAdventure = v),
            Binding.Bool("AutoBuyConsumables",  s => s.AutoBuyConsumables,  (s, v) => s.AutoBuyConsumables = v),
            Binding.Bool("ConsumeIfAlreadyRunning", s => s.ConsumeIfAlreadyRunning, (s, v) => s.ConsumeIfAlreadyRunning = v),
            Binding.Bool("AutoSpin",            s => s.AutoSpin,            (s, v) => s.AutoSpin = v),
            // Swap gear/diggers/beards
            Binding.Bool("SwapTitanLoadouts",   s => s.SwapTitanLoadouts,   (s, v) => s.SwapTitanLoadouts = v),
            Binding.Bool("SwapTitanDiggers",    s => s.SwapTitanDiggers,    (s, v) => s.SwapTitanDiggers = v),
            Binding.Bool("SwapTitanBeards",     s => s.SwapTitanBeards,     (s, v) => s.SwapTitanBeards = v),
            Binding.Bool("SwapYggdrasilLoadouts", s => s.SwapYggdrasilLoadouts, (s, v) => s.SwapYggdrasilLoadouts = v),
            Binding.Bool("SwapYggdrasilDiggers", s => s.SwapYggdrasilDiggers, (s, v) => s.SwapYggdrasilDiggers = v),
            Binding.Bool("SwapYggdrasilBeards", s => s.SwapYggdrasilBeards, (s, v) => s.SwapYggdrasilBeards = v),
            Binding.Bool("SwapPitDiggers",      s => s.SwapPitDiggers,      (s, v) => s.SwapPitDiggers = v),
            // Adventure / ITOPOD
            Binding.Bool("BeastMode",           s => s.BeastMode,           (s, v) => s.BeastMode = v),
            Binding.Bool("SnipeBossOnly",       s => s.SnipeBossOnly,       (s, v) => s.SnipeBossOnly = v),
            Binding.Bool("AllowZoneFallback",   s => s.AllowZoneFallback,   (s, v) => s.AllowZoneFallback = v),
            Binding.Int ("CombatMode",          s => s.CombatMode,          (s, v) => s.CombatMode = v, 0, 4),
            Binding.Bool("AdvisorFarmGear",     s => s.AdvisorFarmGear,     (s, v) => s.AdvisorFarmGear = v),
            Binding.Bool("AdvisorFarmBoost",    s => s.AdvisorFarmBoost,    (s, v) => s.AdvisorFarmBoost = v),
            Binding.Bool("GearHuntEnabled",     s => s.GearHuntEnabled,     (s, v) => s.GearHuntEnabled = v),
            Binding.Bool("AdventureTargetITOPOD", s => s.AdventureTargetITOPOD, (s, v) => s.AdventureTargetITOPOD = v),
            Binding.Bool("ITOPODAutoPush",      s => s.ITOPODAutoPush,      (s, v) => s.ITOPODAutoPush = v),
            Binding.Bool("ITOPODBeastMode",     s => s.ITOPODBeastMode,     (s, v) => s.ITOPODBeastMode = v),
            Binding.Int ("ITOPODOptimizeMode",  s => s.ITOPODOptimizeMode,  (s, v) => s.ITOPODOptimizeMode = v, 0, 3),
            Binding.Int ("ITOPODCombatMode",    s => s.ITOPODCombatMode,    (s, v) => s.ITOPODCombatMode = v, 0, 1),  // binary: Idle/Snipe only (ITOPODManager uses Convert.ToBoolean; SavedSettings validates 0..1) — the UI dropdown must offer 2, not 4
            // Zone pickers: the companion sends only valid ids from the `zones` dropdown; -1 = auto/none,
            // 1000 = ITOPOD (SnipeZone only). Range is permissive; the dropdown constrains the actual value.
            Binding.Int ("SnipeZone",           s => s.SnipeZone,           (s, v) => s.SnipeZone = v, -1, 1000),
            Binding.Int ("GearHuntZone",        s => s.GearHuntZone,        (s, v) => s.GearHuntZone = v, -1, 1000),
            // Titans
            Binding.Int ("TitanCombatMode",     s => s.TitanCombatMode,     (s, v) => s.TitanCombatMode = v, 0, 4),
            Binding.Bool("TitanBeastMode",      s => s.TitanBeastMode,      (s, v) => s.TitanBeastMode = v),
            // Loadouts — per-mode optimizer objective (string; "" = manual item list) + Keep-Respawn; the
            // item-id lists ride setSettingList, and Loot Hunter's pool quotas are ints.
            Binding.Str ("TitanObjective",      s => s.TitanObjective,      (s, v) => s.TitanObjective = v),
            Binding.Bool("TitanObjectiveRespawn", s => s.TitanObjectiveRespawn, (s, v) => s.TitanObjectiveRespawn = v),
            Binding.Str ("GoldObjective",       s => s.GoldObjective,       (s, v) => s.GoldObjective = v),
            Binding.Bool("GoldObjectiveRespawn", s => s.GoldObjectiveRespawn, (s, v) => s.GoldObjectiveRespawn = v),
            Binding.Str ("QuestObjective",      s => s.QuestObjective,      (s, v) => s.QuestObjective = v),
            Binding.Bool("QuestObjectiveRespawn", s => s.QuestObjectiveRespawn, (s, v) => s.QuestObjectiveRespawn = v),
            Binding.Str ("YggdrasilObjective",  s => s.YggdrasilObjective,  (s, v) => s.YggdrasilObjective = v),
            Binding.Bool("YggdrasilObjectiveRespawn", s => s.YggdrasilObjectiveRespawn, (s, v) => s.YggdrasilObjectiveRespawn = v),
            Binding.Str ("CookingObjective",    s => s.CookingObjective,    (s, v) => s.CookingObjective = v),
            Binding.Bool("CookingObjectiveRespawn", s => s.CookingObjectiveRespawn, (s, v) => s.CookingObjectiveRespawn = v),
            Binding.Int ("LootHunterRespawnCount", s => s.LootHunterRespawnCount, (s, v) => s.LootHunterRespawnCount = v, 0, 20),
            Binding.Int ("LootHunterDropCount", s => s.LootHunterDropCount,  (s, v) => s.LootHunterDropCount = v, 0, 20),
            // Challenges
            Binding.Bool("AdvisorChallenges",   s => s.AdvisorChallenges,   (s, v) => s.AdvisorChallenges = v),
            // Quests
            Binding.Bool("AllowMajorQuests",    s => s.AllowMajorQuests,    (s, v) => s.AllowMajorQuests = v),
            Binding.Bool("QuestsFullBank",      s => s.QuestsFullBank,      (s, v) => s.QuestsFullBank = v),
            Binding.Bool("ManualMinors",        s => s.ManualMinors,        (s, v) => s.ManualMinors = v),
            Binding.Bool("AbandonMinors",       s => s.AbandonMinors,       (s, v) => s.AbandonMinors = v),
            Binding.Int ("MinorAbandonThreshold", s => s.MinorAbandonThreshold, (s, v) => s.MinorAbandonThreshold = v, 0, 100),
            Binding.Bool("FiftyItemMinors",     s => s.FiftyItemMinors,     (s, v) => s.FiftyItemMinors = v),
            Binding.Bool("UseButterMinor",      s => s.UseButterMinor,      (s, v) => s.UseButterMinor = v),
            Binding.Bool("UseButterMajor",      s => s.UseButterMajor,      (s, v) => s.UseButterMajor = v),
            Binding.Int ("QuestCombatMode",     s => s.QuestCombatMode,     (s, v) => s.QuestCombatMode = v, 0, 4),
            Binding.Bool("QuestBeastMode",      s => s.QuestBeastMode,      (s, v) => s.QuestBeastMode = v),
            Binding.Bool("PoolMajorQuests",     s => s.PoolMajorQuests,     (s, v) => s.PoolMajorQuests = v),
            Binding.Bool("QuestHoldForGear",    s => s.QuestHoldForGear,    (s, v) => s.QuestHoldForGear = v),
            // Gold
            Binding.Bool("SnipeOnNewZone",      s => s.SnipeOnNewZone,      (s, v) => s.SnipeOnNewZone = v),
            Binding.Bool("SnipeOnRebirth",      s => s.SnipeOnRebirth,      (s, v) => s.SnipeOnRebirth = v),
            Binding.Bool("SnipeOnGoldStarved",  s => s.SnipeOnGoldStarved,  (s, v) => s.SnipeOnGoldStarved = v),
            Binding.Bool("SnipeOnTimer",        s => s.SnipeOnTimer,        (s, v) => s.SnipeOnTimer = v),
            Binding.Int ("ResnipeTime",         s => s.ResnipeTime,         (s, v) => s.ResnipeTime = v, 0, 86400),
            Binding.Bool("GoldCBlockMode",      s => s.GoldCBlockMode,      (s, v) => s.GoldCBlockMode = v),
            // Money pit
            Binding.Bool("AutoMoneyPit",        s => s.AutoMoneyPit,        (s, v) => s.AutoMoneyPit = v),
            Binding.Dbl ("MoneyPitThreshold",   s => s.MoneyPitThreshold,   (s, v) => s.MoneyPitThreshold = v, 0, 1e300),
            Binding.Int ("DaycareThreshold",    s => s.DaycareThreshold,    (s, v) => s.DaycareThreshold = v, 0, 100),
            Binding.Bool("MoneyPitRunMode",     s => s.MoneyPitRunMode,     (s, v) => s.MoneyPitRunMode = v),
            Binding.Bool("PredictMoneyPit",     s => s.PredictMoneyPit,     (s, v) => s.PredictMoneyPit = v),
            Binding.Bool("MoneyPitDaycare",     s => s.MoneyPitDaycare,     (s, v) => s.MoneyPitDaycare = v),
            // Boosts
            Binding.Int ("CubePriority",        s => s.CubePriority,        (s, v) => s.CubePriority = v, 0, 4),
            Binding.Int ("FavoredMacguffin",    s => s.FavoredMacguffin,    (s, v) => s.FavoredMacguffin = v, -1, 64),
            // Wishes
            Binding.Int ("WishLimit",           s => s.WishLimit,           (s, v) => s.WishLimit = v, 1, 4),
            Binding.Int ("WishMode",            s => s.WishMode,            (s, v) => s.WishMode = v, 0, 3),
            Binding.Dbl ("WishEnergy",          s => s.WishEnergy,          (s, v) => s.WishEnergy = v, 0, 100),
            Binding.Dbl ("WishMagic",           s => s.WishMagic,           (s, v) => s.WishMagic = v, 0, 100),
            Binding.Dbl ("WishR3",              s => s.WishR3,              (s, v) => s.WishR3 = v, 0, 100),
            Binding.Bool("WeakPriorities",      s => s.WeakPriorities,      (s, v) => s.WeakPriorities = v),
            // Cards
            Binding.Bool("AutoCastCards",       s => s.AutoCastCards,       (s, v) => s.AutoCastCards = v),
            Binding.Bool("TrashCards",          s => s.TrashCards,          (s, v) => s.TrashCards = v),
            Binding.Bool("CardSortEnabled",     s => s.CardSortEnabled,     (s, v) => s.CardSortEnabled = v),
            Binding.Bool("CastProtectedCards",  s => s.CastProtectedCards,  (s, v) => s.CastProtectedCards = v),
            Binding.Bool("TrashProtectedCards", s => s.TrashProtectedCards, (s, v) => s.TrashProtectedCards = v),
            // Yggdrasil
            Binding.Bool("ActivateFruits",      s => s.ActivateFruits,      (s, v) => s.ActivateFruits = v),
            Binding.Int ("YggSwapThreshold",    s => s.YggSwapThreshold,    (s, v) => s.YggSwapThreshold = v, 1, 24),
            // Perks & Quirks
            Binding.Bool("AdvisorPerks",        s => s.AdvisorPerks,        (s, v) => s.AdvisorPerks = v),
            Binding.Bool("AdvisorQuirks",       s => s.AdvisorQuirks,       (s, v) => s.AdvisorQuirks = v),

            // --- Blood (W1.1) ---
            Binding.Bool("AutoSpellSwap",            s => s.AutoSpellSwap,            (s, v) => s.AutoSpellSwap = v),
            Binding.Bool("IronPillOnRebirth",        s => s.IronPillOnRebirth,        (s, v) => s.IronPillOnRebirth = v),
            Binding.Bool("BloodMacGuffinAOnRebirth", s => s.BloodMacGuffinAOnRebirth, (s, v) => s.BloodMacGuffinAOnRebirth = v),
            Binding.Bool("BloodMacGuffinBOnRebirth", s => s.BloodMacGuffinBOnRebirth, (s, v) => s.BloodMacGuffinBOnRebirth = v),
            Binding.Int ("SpaghettiThreshold",       s => s.SpaghettiThreshold,       (s, v) => s.SpaghettiThreshold = v,       0, 100),
            Binding.Int ("CounterfeitThreshold",     s => s.CounterfeitThreshold,     (s, v) => s.CounterfeitThreshold = v,     0, 100000),
            Binding.Int ("BloodMacGuffinAThreshold", s => s.BloodMacGuffinAThreshold, (s, v) => s.BloodMacGuffinAThreshold = v, 0, 100000),
            Binding.Int ("BloodMacGuffinBThreshold", s => s.BloodMacGuffinBThreshold, (s, v) => s.BloodMacGuffinBThreshold = v, 0, 100000),
            Binding.Dbl ("BloodNumberThreshold",     s => s.BloodNumberThreshold,     (s, v) => s.BloodNumberThreshold = v,     0, 1e18),
        };

        private static readonly Dictionary<string, Binding> Bindings = BuildBindings();
        private static Dictionary<string, Binding> BuildBindings()
        {
            var d = new Dictionary<string, Binding>();
            foreach (var b in BindingList) d[b.Key] = b;
            return d;
        }

        // One-shot outbound notice (e.g. the result of a button action): set on the main thread by a
        // command, emitted in the NEXT snapshot as root["notice"], then cleared. Companion shows a toast.
        private static string _notice;

        // ---- doAction registry: fire-and-forget UI actions (buttons), not settings. Each maps to a
        //      safe, existing main-thread entry point — mirrors what the WinForms buttons already do.
        //      Drained via DrainCommands on the Unity main thread with a per-command try/catch.
        private static readonly Dictionary<string, Action> Actions = BuildActions();
        private static Dictionary<string, Action> BuildActions()
        {
            var d = new Dictionary<string, Action>(StringComparer.Ordinal);
            // "Refresh" — identical to the Reload button.
            d["reloadAdvisor"] = () => Main.RequestSettingsReload();
            // "Snipe Now" — arm a gold snipe by clearing the completion latch (mirrors GoldSnipeNow_Click).
            d["snipeNow"] = () => { if (Main.Settings != null) Main.Settings.GoldSnipeComplete = false; };
            // "Re-optimize gear now" — force an immediate optimize+equip of the active objective's best set
            // (main thread) and stash the human-readable outcome for the next snapshot's `notice` toast.
            d["refreshGear"] = () => { _notice = AdvisorApply.ForceGearReoptimize(); };
            return d;
        }

        // ---------------------------------------------------------------- lifecycle

        public void Start()
        {
            // Two UNIDIRECTIONAL pipes, each synchronous (PipeOptions.None — the M1-proven pattern on Mono).
            // Splitting the directions avoids concurrent read+write on a single handle, which a synchronous
            // pipe serializes (deadlock) and which the async-pipe workaround can't fix reliably under Mono
            // (sync WaitForConnection on an Asynchronous pipe fails with ERROR_PIPE_LISTENING there).
            _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "NGUAdvisorUiBridgeTx" };
            _pumpThread.Start();
            _cmdThread = new Thread(CmdLoop) { IsBackground = true, Name = "NGUAdvisorUiBridgeRx" };
            _cmdThread.Start();
            try { Main.Log("UiBridge started (snapshots '" + PipeName + "', commands '" + CmdPipeName + "', v" + ProtocolVersion + ")."); }
            catch { /* logging must never break startup */ }
        }

        public void Dispose()
        {
            _stopping = true;
            try { _signal.Set(); } catch { }
            // Disconnect (DisconnectNamedPipe on Windows-Mono fails any pending I/O) then dispose both
            // servers; poke each to release a thread parked in WaitForConnection; join both threads.
            DisconnectAndDispose(_server);
            DisconnectAndDispose(_cmdServer);
            Poke(PipeName);
            Poke(CmdPipeName);
            try { if (_pumpThread != null) _pumpThread.Join(1000); } catch { }
            try { if (_cmdThread != null) _cmdThread.Join(1000); } catch { }
            try { _signal.Close(); } catch { }
        }

        private static void DisconnectAndDispose(NamedPipeServerStream s)
        {
            if (s == null) return;
            try { if (s.IsConnected) s.Disconnect(); } catch { }
            try { s.Dispose(); } catch { }
        }

        private static void Poke(string name)
        {
            try { using (var p = new NamedPipeClientStream(".", name, PipeDirection.InOut)) p.Connect(200); }
            catch { /* nobody parked / already gone — fine */ }
        }

        // ------------------------------------------------------------- main-thread publish

        /// <summary>Build a snapshot from live advisor state and hand it to the pipe thread. MAIN THREAD ONLY.</summary>
        public void Publish(float nextLoopSeconds)
        {
            // Drain any error the background pipe thread stashed. Logging must happen HERE (main thread):
            // Main.Log* reads the game Character object, which hard-crashes if touched off-thread.
            var pumpErr = _pumpError;
            if (pumpErr != null) { _pumpError = null; try { Main.LogDebug("UiBridge pump: " + pumpErr); } catch { } }

            string json;
            try { json = BuildSnapshotJson(nextLoopSeconds); }
            catch (Exception e) { try { Main.LogDebug("UiBridge snapshot build failed: " + e); } catch { } return; }
            _latest = json;
            try { _signal.Set(); } catch { }
        }

        /// <summary>
        /// Ask the companion page to show a view (the F9 hotkey opens the Profile Editor). Main thread only —
        /// it is called from Main.Update and read while the snapshot is built on the same thread.
        /// The request rides the normal snapshot; it expires after <see cref="NavTtlSnaps"/> snapshots so a
        /// companion launched by the same keypress still catches it, but a later one is not yanked around.
        /// </summary>
        public void RequestView(string view)
        {
            if (string.IsNullOrEmpty(view)) return;
            _navView = view;
            _navSeq++;
            _navUntilSeq = _seq + NavTtlSnaps;
        }

        // ------------------------------------------------------------- background pipe pump

        // M7 (audit): create a hardened pipe server. Best-effort restrict the DACL to the current user,
        // shrinking the surface from "any user / remote / lower-integrity" to this user's processes.
        // Mono's System.IO.Pipes may not implement the PipeSecurity overload, and breaking the bridge
        // over hardening would be worse than the risk — so on any failure we fall back to the default
        // constructor and note it once (from the main thread; the pipe thread must not touch the log).
        private NamedPipeServerStream CreateServer(string name)
        {
            if (!_pipeSecUnavailable)
            {
                try
                {
                    var sec = new PipeSecurity();
                    var self = WindowsIdentity.GetCurrent().User;
                    if (self != null)
                        sec.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
                    return new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, sec);
                }
                catch { _pipeSecUnavailable = true; }
            }
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
        }

        // Bounded replacement for StreamReader.ReadLine: never buffer an unbounded line. An over-long
        // line is drained to the next newline and dropped (returned as "" — DrainCommands ignores empty
        // lines). Silent by design: this runs on the pipe thread, which must not touch the log.
        private static string ReadBoundedLine(TextReader reader)
        {
            var sb = new StringBuilder();
            int ci;
            while ((ci = reader.Read()) >= 0)
            {
                char ch = (char)ci;
                if (ch == '\n') return sb.ToString().TrimEnd('\r');
                if (sb.Length >= MaxCommandLineChars)
                {
                    while ((ci = reader.Read()) >= 0 && (char)ci != '\n') { }   // discard rest of the over-long line
                    return string.Empty;
                }
                sb.Append(ch);
            }
            return sb.Length == 0 ? null : sb.ToString().TrimEnd('\r');           // EOF
        }

        // Snapshot pipe: WRITE-only. This handle never reads, so nothing serializes its WriteLine.
        private void PumpLoop()
        {
            while (!_stopping)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreateServer(PipeName);
                    _server = server;
                    if (_stopping) return;                      // stop raced in before we park (finally disposes)
                    server.WaitForConnection();                 // blocks until the companion connects (or the poke)
                    if (_stopping) return;

                    var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
                    string lastSent = null;
                    while (!_stopping && server.IsConnected)
                    {
                        string snap = _latest;
                        if (snap != null && !ReferenceEquals(snap, lastSent))
                        {
                            writer.WriteLine(snap);             // throws IOException when the client drops
                            lastSent = snap;
                        }
                        _signal.WaitOne(500);                   // wake on new snapshot; 500ms heartbeat otherwise
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (IOException) { /* client dropped or shutting down — re-accept below */ }
                catch (Exception e) { _pumpError = e.Message; }   // NEVER log here — this is the pipe thread
                finally
                {
                    _server = null;
                    DisconnectAndDispose(server);
                }
                if (!_stopping) Thread.Sleep(200);              // brief backoff before re-accepting
            }
        }

        // ------------------------------------------------------------- inbound commands (M2)

        // Command pipe: READ-only. A separate handle from the snapshot pipe, so its parked ReadLine can
        // never serialize the snapshot writer. The companion connects per-command, sends one line, closes;
        // we read to EOF then re-accept. This method ONLY enqueues raw lines — no game/Unity access here.
        private void CmdLoop()
        {
            while (!_stopping)
            {
                NamedPipeServerStream cmd = null;
                try
                {
                    cmd = CreateServer(CmdPipeName);
                    _cmdServer = cmd;
                    if (_stopping) return;
                    cmd.WaitForConnection();
                    if (_stopping) return;

                    var reader = new StreamReader(cmd, new UTF8Encoding(false));
                    string line;
                    while (!_stopping && cmd.IsConnected && (line = ReadBoundedLine(reader)) != null)
                    {
                        if (line.Length > 0 && _inbound.Count < 256) _inbound.Enqueue(line);  // bound vs flood
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (IOException) { /* client closed after sending — re-accept below */ }
                catch (Exception e) { _pumpError = e.Message; }   // NEVER log here — this is the pipe thread
                finally
                {
                    _cmdServer = null;
                    DisconnectAndDispose(cmd);
                }
                if (!_stopping) Thread.Sleep(100);              // short backoff so the next command connects fast
            }
        }

        /// <summary>Apply queued UI commands. MAIN THREAD ONLY (called from Main.Update); per-command guarded.</summary>
        public void DrainCommands()
        {
            if (_pipeSecUnavailable && !_secNoteLogged)
            {
                _secNoteLogged = true;
                try { Main.LogDebug("UiBridge: pipe ACL unavailable on this runtime; using default pipe security (M7)."); } catch { }
            }
            string line;
            int budget = 64;                                    // bound work per frame
            while (budget-- > 0 && _inbound.TryDequeue(out line))
            {
                try { Dispatch(line); }
                catch (Exception e) { try { Main.LogDebug("UiBridge command failed: " + e.Message); } catch { } }
            }
        }

        private void Dispatch(string line)
        {
            JSONObject obj;
            try { obj = JSON.Parse(line) as JSONObject; }
            catch { try { Main.LogDebug("UiBridge: unparseable command"); } catch { } return; }
            if (obj == null) return;

            string cmd = obj["cmd"].Value;
            if (string.IsNullOrEmpty(cmd)) return;

            var settings = Main.Settings;
            switch (cmd)
            {
                case "setAutomation":
                {
                    // Require a real boolean so a missing/garbled 'on' can't silently pause a system.
                    var onNode = obj["on"];
                    if (onNode == null || !onNode.IsBoolean) { Main.LogDebug("UiBridge: setAutomation missing/invalid 'on'"); break; }
                    bool on = onNode.AsBool;
                    string sys = obj["system"].Value;
                    if (string.IsNullOrEmpty(sys))
                    {
                        // Master. The GlobalEnabled setter persists + refreshes the form + logs and is
                        // change-guarded, so we only assign it — never SaveSettings ourselves.
                        if (settings != null) settings.GlobalEnabled = on;
                        Main.Log("UI command: automation " + (on ? "ON" : "OFF"));
                    }
                    else
                    {
                        var t = FindToggle(sys);
                        if (t != null && t.SetAuto != null && settings != null)
                        { t.SetAuto(settings, on); Main.Log("UI command: " + sys + " automation " + (on ? "ON" : "OFF")); }
                        else Main.LogDebug("UiBridge: setAutomation unsupported system '" + sys + "'");
                    }
                    break;
                }

                case "setLayer":
                {
                    var t = FindToggle(obj["system"].Value);
                    if (t == null || t.SetAdvisor == null || settings == null) { Main.LogDebug("UiBridge: setLayer unsupported system '" + obj["system"].Value + "'"); break; }
                    string layer = obj["layer"].Value;
                    if (layer != "advisor" && layer != "manual") { Main.LogDebug("UiBridge: setLayer invalid layer '" + layer + "'"); break; }
                    bool advisor = layer == "advisor";       // ADVISOR vs MANUAL
                    t.SetAdvisor(settings, advisor);
                    Main.Log("UI command: " + t.Id + " decisions -> " + (advisor ? "ADVISOR" : "MANUAL"));
                    break;
                }

                case "setSetting":
                {
                    string key = obj["key"].Value;
                    var val = obj["value"];
                    if (string.IsNullOrEmpty(key) || settings == null) { Main.LogDebug("UiBridge: setSetting missing key"); break; }
                    if (val == null || !(val.IsBoolean || val.IsNumber || val.IsString)) { Main.LogDebug("UiBridge: setSetting invalid value for '" + key + "'"); break; }
                    Binding b;
                    if (!Bindings.TryGetValue(key, out b)) { Main.LogDebug("UiBridge: setSetting unknown key '" + key + "'"); break; }
                    b.Apply(settings, val);
                    Main.Log("UI command: set " + key);
                    break;
                }

                case "switchProfile":
                {
                    string name = obj["name"].Value;
                    if (string.IsNullOrEmpty(name) || settings == null) { Main.LogDebug("UiBridge: switchProfile missing name"); break; }
                    // Only switch to an EXISTING profile; reject path separators (the UI sends names from
                    // profiles.list). Prevents a typo creating an empty profile / any path traversal.
                    if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains(".."))
                    { Main.LogDebug("UiBridge: switchProfile rejected name '" + name + "'"); break; }
                    try
                    {
                        var dir = Main.GetProfilesDir();
                        if (dir == null || !File.Exists(Path.Combine(dir, name + ".json")))
                        { Main.LogDebug("UiBridge: switchProfile no such profile '" + name + "'"); break; }
                    }
                    catch { break; }
                    settings.AllocationFile = name;                        // change-guarded setter persists
                    Main.RequestAllocationReload();                        // main-thread allocation reload next frame
                    Main.Log("UI command: switch profile -> " + name);
                    break;
                }

                case "setAutoProfile":
                {
                    var onNode = obj["on"];
                    if (onNode == null || !onNode.IsBoolean || settings == null) { Main.LogDebug("UiBridge: setAutoProfile invalid"); break; }
                    settings.AutoProfile = onNode.AsBool;
                    Main.Log("UI command: auto-profile " + (onNode.AsBool ? "ON" : "OFF"));
                    break;
                }

                case "setLaunchCompanion":
                {
                    var onNode = obj["on"];
                    if (onNode == null || !onNode.IsBoolean || settings == null) { Main.LogDebug("UiBridge: setLaunchCompanion invalid"); break; }
                    settings.LaunchCompanion = onNode.AsBool;
                    Main.Log("UI command: auto-launch companion " + (onNode.AsBool ? "ON" : "OFF"));
                    break;
                }

                case "reloadAdvisor":
                    Main.RequestSettingsReload();               // drained next frame: settings + form + allocation
                    Main.Log("UI command: reload requested");
                    break;

                case "doAction":
                {
                    string action = obj["action"].Value;
                    if (string.IsNullOrEmpty(action)) { Main.LogDebug("UiBridge: doAction missing action"); break; }
                    Action fn;
                    if (!Actions.TryGetValue(action, out fn)) { Main.LogDebug("UiBridge: doAction unknown action '" + action + "'"); break; }
                    fn();
                    Main.Log("UI command: action " + action);
                    break;
                }

                case "setSettingFlag":
                {
                    // Set one element of a bool[] setting (e.g. the titan kill grid). CLONE before mutating:
                    // the setter's SequenceEqual guard would treat an in-place edit of the same array as no-change.
                    string key = obj["key"].Value;
                    var idxNode = obj["index"]; var valNode = obj["value"];
                    if (settings == null || idxNode == null || !idxNode.IsNumber || valNode == null || !valNode.IsBoolean)
                    { Main.LogDebug("UiBridge: setSettingFlag invalid"); break; }
                    int index = idxNode.AsInt; bool value = valNode.AsBool;
                    if (key == "TitanSwapTargets")
                    {
                        var arr = settings.TitanSwapTargets;
                        if (arr == null || index < 0 || index >= arr.Length) { Main.LogDebug("UiBridge: setSettingFlag index oob"); break; }
                        var copy = (bool[])arr.Clone(); copy[index] = value; settings.TitanSwapTargets = copy;
                        Main.Log("UI command: TitanSwapTargets[" + index + "] = " + value);
                    }
                    else Main.LogDebug("UiBridge: setSettingFlag unknown key '" + key + "'");
                    break;
                }

                case "setSettingList":
                {
                    // Replace an int[] setting (boost priority / blacklist). The setters here don't validate,
                    // so range-check every id (mass-update validates on load; we mirror the safe floor).
                    string key = obj["key"].Value;
                    var valsNode = obj["values"] as JSONArray;
                    if (settings == null || valsNode == null) { Main.LogDebug("UiBridge: setSettingList invalid"); break; }
                    var raw = new List<int>();
                    for (int i = 0; i < valsNode.Count && raw.Count < MaxListIds; i++)
                    {
                        var n = valsNode[i];
                        if (n != null && n.IsNumber) raw.Add(n.AsInt);
                    }
                    // Per-key validation: the enemy blacklist accepts only real adventure sprite ids (mirrors
                    // SavedSettings.IsAdvEnemy); every other list is gear-ids clamped to the gear range.
                    if (key == "BlacklistedBosses")
                    {
                        var f = new List<int>();
                        foreach (var id in raw) if (IsAdvEnemyId(id)) f.Add(id);
                        settings.BlacklistedBosses = f.ToArray();   // setter re-applies CombatManager.UpdateBlacklists()
                        Main.Log("UI command: BlacklistedBosses (" + f.Count + " ids)");
                    }
                    else if (key == "WishPriorities" || key == "WishBlacklist")
                    {
                        var f = new List<int>();
                        foreach (var id in raw) if (id >= 0 && id <= Consts.MAX_WISH_ID) f.Add(id);
                        var arr = f.ToArray();
                        if (key == "WishPriorities") settings.WishPriorities = arr; else settings.WishBlacklist = arr;
                        Main.Log("UI command: " + key + " (" + arr.Length + " ids)");
                    }
                    else
                    {
                        var f = new List<int>();
                        foreach (var id in raw) if (id >= 0 && id <= Consts.MAX_GEAR_ID) f.Add(id);
                        var arr = f.ToArray();
                        if (AssignGearList(settings, key, arr)) Main.Log("UI command: " + key + " (" + arr.Length + " ids)");
                        else Main.LogDebug("UiBridge: setSettingList unknown key '" + key + "'");
                    }
                    break;
                }

                case "setLoadoutFromGear":
                {
                    // Fill a loadout from the currently-equipped gear (the "Use current gear" button).
                    string key = obj["key"].Value;
                    if (settings == null || string.IsNullOrEmpty(key)) { Main.LogDebug("UiBridge: setLoadoutFromGear missing key"); break; }
                    int[] ids;
                    try { ids = LoadoutManager.CurrentGearIds() ?? new int[0]; } catch { ids = new int[0]; }
                    var f = new List<int>();
                    foreach (var id in ids) if (id >= 0 && id <= Consts.MAX_GEAR_ID) f.Add(id);
                    if (AssignGearList(settings, key, f.ToArray())) Main.Log("UI command: setLoadoutFromGear " + key + " (" + f.Count + " ids)");
                    else Main.LogDebug("UiBridge: setLoadoutFromGear unknown key '" + key + "'");
                    break;
                }

                case "setIntArray":
                {
                    // Whole int[] for fixed-index arrays (transform per-chain flags, card rarity/cost thresholds).
                    // The companion edits the array it received in the snapshot (correct length) and sends it back.
                    string key = obj["key"].Value;
                    var valsNode = obj["values"] as JSONArray;
                    if (settings == null || valsNode == null) { Main.LogDebug("UiBridge: setIntArray invalid"); break; }
                    var arr = new int[Math.Min(valsNode.Count, MaxListIds)];
                    for (int i = 0; i < arr.Length; i++) { var n = valsNode[i]; arr[i] = (n != null && n.IsNumber) ? n.AsInt : 0; }
                    if (AssignIntArray(settings, key, arr)) Main.Log("UI command: " + key + " (" + arr.Length + " values)");
                    else Main.LogDebug("UiBridge: setIntArray unknown key '" + key + "'");
                    break;
                }

                case "setSettingStrList":
                {
                    // Whole string[] for fixed-vocabulary lists (BoostPriority types, CardSortOrder criteria).
                    string key = obj["key"].Value;
                    var valsNode = obj["values"] as JSONArray;
                    if (settings == null || valsNode == null) { Main.LogDebug("UiBridge: setSettingStrList invalid"); break; }
                    var raw = new List<string>();
                    for (int i = 0; i < valsNode.Count && raw.Count < MaxListIds; i++) { var n = valsNode[i]; if (n != null && n.IsString) raw.Add(n.Value); }
                    var arr = FilterStrList(key, raw);
                    if (arr == null) { Main.LogDebug("UiBridge: setSettingStrList unknown key '" + key + "'"); break; }
                    if (key == "BoostPriority") settings.BoostPriority = arr;
                    else if (key == "CardSortOrder") settings.CardSortOrder = arr;
                    Main.Log("UI command: " + key + " (" + arr.Length + " items)");
                    break;
                }

                default:
                    Main.LogDebug("UiBridge: unknown command '" + cmd + "'");
                    break;
            }
        }

        private static JSONArray IntArr(int[] a)
        {
            var arr = new JSONArray();
            if (a != null) foreach (var id in a) arr.Add(id);
            return arr;
        }

        // Assign a gear-id int[] to a named list setting (boost lists + the 7 loadout modes). Returns false
        // for an unknown key. Used by both setSettingList and setLoadoutFromGear.
        private static bool AssignGearList(SavedSettings s, string key, int[] arr)
        {
            switch (key)
            {
                case "PriorityBoosts": s.PriorityBoosts = arr; return true;
                case "BoostBlacklist": s.BoostBlacklist = arr; return true;
                case "TitanLoadout": s.TitanLoadout = arr; return true;
                case "GoldDropLoadout": s.GoldDropLoadout = arr; return true;
                case "QuestLoadout": s.QuestLoadout = arr; return true;
                case "YggdrasilLoadout": s.YggdrasilLoadout = arr; return true;
                case "CookingLoadout": s.CookingLoadout = arr; return true;
                case "LootHunterAccessories": s.LootHunterAccessories = arr; return true;
                case "Shockwave": s.Shockwave = arr; return true;
                default: return false;
            }
        }

        // Assign a whole int[] to a fixed-index array setting (transform per-chain flags, card thresholds).
        private static bool AssignIntArray(SavedSettings s, string key, int[] arr)
        {
            switch (key)
            {
                case "TransformAutoClimb": s.TransformAutoClimb = arr; return true;
                case "TransformKeepMax": s.TransformKeepMax = arr; return true;
                case "TransformFilter": s.TransformFilter = arr; return true;
                case "CardRarities": s.CardRarities = arr; return true;
                case "CardCosts": s.CardCosts = arr; return true;
                default: return false;
            }
        }

        // Validate + dedupe a fixed-vocabulary string list; null for an unknown key.
        private static string[] FilterStrList(string key, List<string> raw)
        {
            var seen = new HashSet<string>();
            var outL = new List<string>();
            if (key == "BoostPriority")
            {
                foreach (var s in raw) { var t = s == null ? "" : s.Trim(); if ((t == "Power" || t == "Toughness" || t == "Special") && seen.Add(t)) outL.Add(t); }
                return outL.ToArray();
            }
            if (key == "CardSortOrder")
            {
                var sl = CardManager.sortList;
                if (sl != null) foreach (var s in raw) { if (s != null && System.Array.IndexOf(sl, s) >= 0 && seen.Add(s)) outL.Add(s); }
                return outL.ToArray();
            }
            return null;
        }

        // Whether id is a real adventure enemy sprite (mirrors SavedSettings.IsAdvEnemy). Runs on the main
        // thread (command drain), so reading the live enemy list is safe.
        private static bool IsAdvEnemyId(int id)
        {
            try
            {
                var el = Main.Character?.adventureController?.enemyList;
                if (el == null) return false;
                for (int z = 0; z < el.Count; z++)
                {
                    var zl = el[z];
                    if (zl == null) continue;
                    foreach (var en in zl) if (en != null && en.spriteID == id) return true;
                }
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------- snapshot builder (MAIN THREAD)

        private string BuildSnapshotJson(float nextLoopSeconds)
        {
            var c = Main.Character;
            var settings = Main.Settings;
            var root = new JSONObject();
            root["v"] = ProtocolVersion;
            root["seq"] = (double)(++_seq);
            root["ts"] = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            // --- header ---
            bool automation = settings != null && settings.GlobalEnabled;
            root["automation"] = automation;
            root["profile"] = settings == null ? "-"
                : (settings.AutoProfile ? "AUTO (advisor)" : (settings.AllocationFile ?? "-"));
            root["nextLoopSec"] = Num(Math.Round(nextLoopSeconds, 1));
            Safe("action", () => root["action"] = LockManager.GetLockTypeName());

            // --- stage / progression ---
            Safe("stage", () =>
            {
                var prog = ProgressionAnalyzer.Detect();
                root["difficulty"] = prog.Difficulty ?? "Normal";
                var stage = new JSONObject();
                stage["known"] = prog.Known;
                stage["label"] = prog.Label ?? "-";
                stage["activity"] = prog.Activity ?? "";
                stage["chapter"] = prog.Chapter;
                double elapsed = c != null ? c.rebirthTime.totalseconds : 0;
                stage["rebirthElapsed"] = FormatDuration(elapsed);
                double target = -1;
                try { if (Main.Profile != null) target = Main.Profile.NextRebirthTargetSeconds(); } catch { }
                if (target > 0 && target > elapsed) stage["rebirthRemaining"] = FormatDuration(target - elapsed);
                root["stage"] = stage;

                // goal card
                var goal = new JSONObject();
                goal["text"] = prog.NextGoal ?? "";
                Safe("bossNow", () => { if (c != null) goal["bossNow"] = ZoneHelpers.CurrentHighestBoss(c); });
                root["goal"] = goal;
            });

            // --- exp balance (feeds the goal card's second gauge) ---
            Safe("exp", () =>
            {
                var xb = ExpBalancer.Analyze();
                if (!xb.Known) return;
                var g = root["goal"].AsObject;
                g["expBalance"] = Num(Math.Round(xb.BalancePct, 0));
                g["expBalanced"] = xb.Balanced;
                g["expNext"] = xb.NextShort ?? xb.NextNames ?? "";
            });

            // --- actions (OptimizationAdvisor recommendations) ---
            Safe("actions", () =>
            {
                var recs = OptimizationAdvisor.Analyze();
                var arr = new JSONArray();
                if (recs != null)
                {
                    foreach (var r in recs)
                    {
                        var o = new JSONObject();
                        o["system"] = r.System ?? "";
                        o["text"] = r.Text ?? "";
                        o["severity"] = r.Severity;
                        o["optimal"] = r.Optimal;
                        if (!string.IsNullOrEmpty(r.AutoKey)) o["auto"] = r.AutoKey;
                        arr.Add(o);
                    }
                }
                root["actions"] = arr;
            });

            // --- instruments ---
            var inst = new JSONObject();
            Safe("resources", () =>
            {
                if (c == null) return;
                var res = new JSONObject();
                res["e"] = Pct(c.curEnergy, c.totalCapEnergy());
                res["m"] = Pct(c.magic.curMagic, c.totalCapMagic());
                if (c.res3 != null && c.res3.res3On) res["r3"] = Pct(c.res3.curRes3, c.totalCapRes3());
                inst["resources"] = res;
            });
            Safe("titan", () =>
            {
                var obj = OptimizationAdvisor.NextObjective();
                var t = new JSONObject();
                t["known"] = obj.Known;
                if (obj.Known)
                {
                    // Index is a 0-based titan-ladder index (i==5 -> T6/Beast); the displayed titan number is Index+1.
                    t["name"] = "T" + (obj.Index + 1) + (obj.Version > 0 ? " v" + obj.Version : "");
                    t["stage"] = obj.Stage ?? "";
                    if (c != null && obj.ReqAttack > 0) t["atk"] = ClampPct(c.totalAdvAttack() / obj.ReqAttack * 100.0);
                    if (c != null && obj.ReqDefense > 0) t["def"] = ClampPct(c.totalAdvDefense() / obj.ReqDefense * 100.0);
                }
                inst["titan"] = t;
            });
            Safe("boostFarm", () =>
            {
                var v = BoostFarmAdvisor.Analyze();
                var b = new JSONObject();
                b["known"] = v.Known;
                if (v.Known)
                {
                    b["best"] = v.BestName ?? "";
                    b["bestZone"] = v.BestZone;
                    b["bestRate"] = Num(Math.Round(v.BestRate, 0));
                    b["itopodRate"] = Num(Math.Round(v.ItopodRate, 0));
                    b["currentZone"] = v.CurrentZone;
                    b["compliant"] = v.Compliant;
                    if (!string.IsNullOrEmpty(v.Text)) b["text"] = v.Text;
                }
                inst["boostFarm"] = b;
            });
            Safe("cube", () =>
            {
                // Infinity Cube power/toughness vs the game's softcaps (boosts stop helping a capped cube).
                var cu = new JSONObject();
                var ch = Main.Character; var ic = Main.InventoryController;
                if (ch != null && ic != null)
                {
                    double cp = ch.inventory.cubePower, ct = ch.inventory.cubeToughness;
                    double cps = ic.cubePowerSoftcap(), cts = ic.cubeToughnessSoftcap();
                    cu["known"] = true;
                    cu["power"] = Num(cp); cu["toughness"] = Num(ct);
                    cu["powerSoftcap"] = Num(cps); cu["toughSoftcap"] = Num(cts);
                    cu["powerPct"] = ClampPct(cps > 0 ? cp / cps * 100.0 : 0);
                    cu["toughPct"] = ClampPct(cts > 0 ? ct / cts * 100.0 : 0);
                }
                else cu["known"] = false;
                inst["cube"] = cu;
            });
            root["instruments"] = inst;

            // --- growth rates (+ bridge-sampled sparklines) ---
            Safe("growth", () =>
            {
                var arr = new JSONArray();
                arr.Add(GrowthNode("EXP", s => s.GExp));
                arr.Add(GrowthNode("NGU", s => s.GNgu));
                arr.Add(GrowthNode("PP", s => s.GPp));
                arr.Add(GrowthNode("AP", s => s.GAp));
                arr.Add(GrowthNode("Cube", s => s.GCubeP));
                root["growth"] = arr;
            });

            // --- feed (bridge-owned ring, fed from Activity.Current) ---
            Safe("feed", () =>
            {
                PumpFeed();
                var arr = new JSONArray();
                // newest first
                for (var n = _feed.Last; n != null; n = n.Previous) arr.Add(n.Value);
                root["feed"] = arr;
            });

            // --- per-system two-layer toggle states (M3) ---
            Safe("systems", () =>
            {
                if (settings == null) return;
                var sys = new JSONObject();
                foreach (var t in Toggles)
                {
                    var o = new JSONObject();
                    if (t.GetAuto != null) { o["hasAuto"] = true; o["auto"] = t.GetAuto(settings); }
                    if (t.GetAdvisor != null) { o["hasAdvisor"] = true; o["advisor"] = t.GetAdvisor(settings); }
                    sys[t.Id] = o;
                }
                root["systems"] = sys;
            });

            // --- scalar setting values for the wired controls (W1) ---
            Safe("settings", () =>
            {
                if (settings == null) return;
                var so = new JSONObject();
                foreach (var b in BindingList) b.Write(so, settings);
                root["settings"] = so;
            });

            // --- macguffin id->name map (static; drives the FavoredMacguffin dropdown). Built once the game
            //     list is populated, then written from cache each snapshot so a reconnecting UI always gets it.
            Safe("macguffins", () =>
            {
                if (_macguffinsCache == null)
                {
                    var list = InventoryManager.macguffinList;
                    if (list != null && list.Count > 0)
                    {
                        var mo = new JSONObject();
                        foreach (var kv in list) mo[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
                        _macguffinsCache = mo;
                    }
                }
                if (_macguffinsCache != null) root["macguffins"] = _macguffinsCache;
            });

            // --- non-titan zone id->name map (static; drives the SnipeZone / GearHuntZone dropdowns). Built
            //     once from ZoneHelpers.ZoneList, then written from cache each snapshot for reconnecting UIs. ---
            Safe("zones", () =>
            {
                if (_zonesCache == null)
                {
                    var zl = ZoneHelpers.ZoneList;
                    if (zl != null && zl.Count > 0)
                    {
                        var zo = new JSONObject();
                        foreach (var kv in zl)
                            if (kv.Key >= 0 && !ZoneHelpers.ZoneIsTitan(kv.Key))
                                zo[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
                        _zonesCache = zo;
                    }
                }
                if (_zonesCache != null) root["zones"] = _zonesCache;
            });

            // --- adventure enemy spriteId->name map (static; drives the never-snipe blacklist picker). Flat +
            //     deduped across zones (a sprite repeats across zones); built once, then written from cache. ---
            Safe("advEnemies", () =>
            {
                if (_advEnemiesCache == null)
                {
                    var el = Main.Character?.adventureController?.enemyList;
                    if (el != null && el.Count > 0)
                    {
                        var eo = new JSONObject();
                        var seen = new HashSet<int>();
                        for (int z = 0; z < el.Count; z++)
                        {
                            var zl = el[z];
                            if (zl == null) continue;
                            foreach (var en in zl)
                                if (en != null && seen.Add(en.spriteID))
                                    eo[en.spriteID.ToString(CultureInfo.InvariantCulture)] = en.name;
                        }
                        if (eo.Count > 0) _advEnemiesCache = eo;
                    }
                }
                if (_advEnemiesCache != null) root["advEnemies"] = _advEnemiesCache;
            });

            // --- current enemy blacklist (BlacklistedBosses int[]); written each snapshot like boostLists. ---
            Safe("advBlacklist", () =>
            {
                if (settings == null) return;
                root["advBlacklist"] = IntArr(settings.BlacklistedBosses);
            });

            // --- loadout id-lists (the 7 modes); written each snapshot so the list editors reconcile. ---
            Safe("loadouts", () =>
            {
                if (settings == null) return;
                var lo = new JSONObject();
                lo["TitanLoadout"] = IntArr(settings.TitanLoadout);
                lo["GoldDropLoadout"] = IntArr(settings.GoldDropLoadout);
                lo["QuestLoadout"] = IntArr(settings.QuestLoadout);
                lo["YggdrasilLoadout"] = IntArr(settings.YggdrasilLoadout);
                lo["CookingLoadout"] = IntArr(settings.CookingLoadout);
                lo["LootHunterAccessories"] = IntArr(settings.LootHunterAccessories);
                lo["Shockwave"] = IntArr(settings.Shockwave);
                root["loadouts"] = lo;
            });

            // --- gear-objective names (static; drives the loadout Advisor-objective dropdowns). Built once. ---
            Safe("gearObjectives", () =>
            {
                if (_gearObjectivesCache == null)
                {
                    var objs = GearObjectives.Objectives;
                    if (objs != null && objs.Count > 0)
                    {
                        var arr = new JSONArray();
                        foreach (var o in objs) arr.Add(o.Name);
                        _gearObjectivesCache = arr;
                    }
                }
                if (_gearObjectivesCache != null) root["gearObjectives"] = _gearObjectivesCache;
            });

            // --- wish priority + blacklist (int[] wish ids); written each snapshot like boostLists. ---
            Safe("wishLists", () =>
            {
                if (settings == null) return;
                var w = new JSONObject();
                w["priorities"] = IntArr(settings.WishPriorities);
                w["blacklist"] = IntArr(settings.WishBlacklist);
                root["wishLists"] = w;
            });

            // --- boost-type priority order (string[] of Power/Toughness/Special) for AutoBoostPriority. ---
            Safe("boostPriority", () =>
            {
                if (settings == null) return;
                var arr = new JSONArray();
                var bp = settings.BoostPriority;
                if (bp != null) foreach (var s in bp) arr.Add(s);
                root["boostPriority"] = arr;
            });

            // --- transform chains: per-chain {name, step} descriptors + the 3 per-chain flag arrays. The
            //     generic "Chain #n" names resolve to the base item's name; step = the current owned tier
            //     ("up to current unlocked level"). Descriptors are throttled (a live inventory scan each);
            //     the flags ride every snapshot for reconciliation. ---
            Safe("transform", () =>
            {
                if (settings == null) return;
                var t = new JSONObject();
                if (_transformMetaCache == null || (_seq % 5) == 1)
                {
                    var meta = new JSONArray();
                    var chains = TransformManager.Chains;
                    if (chains != null)
                        for (int i = 0; i < chains.Length; i++)
                        {
                            var ch = chains[i];
                            var o = new JSONObject();
                            string disp = ch.Name;
                            if (disp != null && disp.StartsWith("Chain #") && ch.Tiers != null && ch.Tiers.Length > 0)
                                disp = Main.ItemNameNice(ch.Tiers[0]);
                            o["name"] = disp;
                            var stepsArr = new JSONArray();
                            bool done = false;
                            try
                            {
                                var view = TransformManager.ViewChain(i);
                                done = view.Done;
                                foreach (var st in view.Steps)
                                {
                                    var so = new JSONObject();
                                    so["name"] = st.Name;
                                    so["level"] = (int)Math.Min(st.Level, int.MaxValue);
                                    so["owned"] = st.Owned;
                                    so["tier"] = st.Tier;
                                    stepsArr.Add(so);
                                }
                            }
                            catch { }
                            o["done"] = done;
                            o["steps"] = stepsArr;
                            meta.Add(o);
                        }
                    if (meta.Count > 0) _transformMetaCache = meta;
                }
                if (_transformMetaCache != null) t["chains"] = _transformMetaCache;
                t["autoClimb"] = IntArr(settings.TransformAutoClimb);
                t["keepMax"] = IntArr(settings.TransformKeepMax);
                t["filter"] = IntArr(settings.TransformFilter);
                root["transform"] = t;
            });

            // --- currently-equipped gear names (Loadouts readout; preserves the WinForms "equipped snapshot"). ---
            Safe("equipped", () =>
            {
                var arr = new JSONArray();
                try { foreach (var id in LoadoutManager.CurrentGearIds() ?? new int[0]) if (id > 0) arr.Add(Main.ItemNameNice(id)); }
                catch { }
                root["equipped"] = arr;
            });

            // --- challenge state (Challenges readout: completed / current / queued, from the overlay tracker). ---
            Safe("challengeState", () =>
            {
                var cs = new JSONObject();
                try
                {
                    var block = ChallengeOverlay.Block();
                    string active = null;
                    try { active = ChallengeDetector.Current(); } catch { }
                    var done = new JSONArray();
                    var queued = new JSONArray();
                    JSONObject cur = null;
                    if (block != null)
                        foreach (var e in block)
                        {
                            if (e.Max <= 0) continue;
                            var o = new JSONObject();
                            o["code"] = e.Code; o["cur"] = e.Cur; o["max"] = e.Max;
                            if (e.Code == active) cur = o;
                            else if (e.Cur >= e.Max) done.Add(o);
                            else queued.Add(o);
                        }
                    cs["done"] = done;
                    cs["queued"] = queued;
                    if (cur != null) cs["current"] = cur;
                    cs["active"] = active ?? "";
                }
                catch { }
                root["challengeState"] = cs;
            });

            // --- log directory: the companion reads the advisor's log files directly for the log drawer. ---
            Safe("logDir", () => { root["logDir"] = Main.GetLogDir(); });

            // --- one-shot notice: outcome of the last button action (e.g. Re-optimize gear now). Emit once
            //     then clear so the companion toasts it exactly once. Same thread as the command drain. ---
            Safe("notice", () => { if (!string.IsNullOrEmpty(_notice)) { root["notice"] = _notice; _notice = null; } });

            // --- EXP page: base Energy:Magic value ratio + the balancer's on-ratio verdict. The on/off
            //     status is the balancer's 6-stat waterfill (NOT the E:M ratio) so it matches the advisor. ---
            Safe("expRatio", () =>
            {
                var er = new JSONObject();
                var xb = ExpBalancer.Analyze();
                er["known"] = xb.Known;
                if (xb.Known && c != null)
                {
                    er["onRatio"] = xb.Balanced;
                    er["pct"] = Num(Math.Round(xb.BalancePct, 0));
                    double eE = Math.Max(0, c.energyPower) * 150.0 + c.capEnergy / 250.0 + c.energyBars * 80.0;
                    bool mu = c.highestBoss >= 37;
                    double mE = mu ? Math.Max(0, c.magic.magicPower) * 450.0 + c.magic.capMagic * 3.0 / 250.0 + c.magic.magicPerBar * 240.0 : 0;
                    er["energy"] = FormatBig(eE);
                    er["magic"] = FormatBig(mE);
                    double tot = eE + mE;
                    er["energyPct"] = Num(tot > 0 ? eE / tot * 100.0 : 0);
                    er["magicPct"] = Num(tot > 0 ? mE / tot * 100.0 : 0);
                    er["ratioText"] = mE > 0 ? ((eE / mE) * 3.0).ToString("0.0", CultureInfo.InvariantCulture) + " : 1" : "Energy only";
                }
                root["expRatio"] = er;
            });

            // --- Beards page: per-beard current level + permanent levels banked on the next rebirth. ---
            Safe("beards", () =>
            {
                var bd = new JSONObject();
                if (c != null && c.beards != null && c.beards.beards != null)
                {
                    try { bd["cap"] = c.allBeards.capBeards(); } catch { }
                    var arr = new JSONArray();
                    var names = OptimizationAdvisor.BeardNames;
                    int n = Math.Min(c.beards.beards.Count, 7);
                    for (int id = 0; id < n; id++)
                    {
                        var b = c.beards.beards[id];
                        var o = new JSONObject();
                        o["id"] = id;
                        o["name"] = id < names.Length ? names[id] : ("Beard " + id);
                        bool active = false; try { active = b.active; } catch { }
                        o["active"] = active;
                        long lvl = 0; try { lvl = b.beardLevel; } catch { }
                        o["level"] = FormatBig(lvl);
                        long gain = 0; try { if (active) gain = c.allBeards.addedTrimmings(id); } catch { }
                        o["gain"] = FormatBig(gain);
                        long perm = 0; try { perm = b.permLevel; } catch { }
                        o["perm"] = FormatBig(perm);
                        arr.Add(o);
                    }
                    bd["list"] = arr;
                }
                root["beards"] = bd;
            });

            // --- Quests page: idle-minors policy + banked/cap ("N of N") + current quest progress. ---
            Safe("quests", () =>
            {
                var q = new JSONObject();
                var st = Main.Settings;
                q["managed"] = st != null && st.AutoQuest;
                q["allowMajors"] = st != null && st.AllowMajorQuests;
                q["idleMinors"] = st != null && !st.ManualMinors;
                q["pooling"] = st != null && st.PoolMajorQuests && !st.QuestBurstActive;
                q["bursting"] = st != null && st.QuestBurstActive;
                if (c != null && c.beastQuest != null)
                {
                    var bq = c.beastQuest;
                    q["banked"] = Num(bq.curBankedQuests);
                    try { q["bankCap"] = Num(c.beastQuestController.maxBankedQuests()); } catch { }
                    bool inq = false; try { inq = bq.inQuest; } catch { }
                    q["inQuest"] = inq;
                    if (inq)
                    {
                        bool minor = false; try { minor = bq.reducedRewards; } catch { }
                        q["type"] = minor ? "minor" : "major";
                        long cd = 0, td = 0; try { cd = bq.curDrops; td = bq.targetDrops; } catch { }
                        q["drops"] = Num(cd); q["targetDrops"] = Num(td);
                        if (td > 0) q["progressPct"] = Num(cd / (double)td * 100.0);
                        try { int z = c.beastQuestController.curQuestZone(); q["zone"] = z; if (ZoneHelpers.ZoneList.TryGetValue(z, out var zn)) q["zoneName"] = zn; } catch { }
                        try { q["readyToHandIn"] = c.beastQuestController.readyToHandIn(); } catch { }
                    }
                    else q["type"] = "none";
                }
                root["quests"] = q;
            });

            // --- Money-pit: next-throw prediction (outcome CATEGORY only; the game exposes no gold value),
            //     ready/ETA, and the advisor's throw plan. ---
            Safe("moneypit", () =>
            {
                var mp = new JSONObject();
                try { mp["ready"] = MoneyPitManager.MoneyPitReady(); } catch { }
                try { mp["etaSec"] = Num(MoneyPitManager.TimeUntilReady()); } catch { }
                try { mp["predicted"] = MoneyPitManager.PredictNext().ToString(); } catch { }
                try { var plan = MoneyPitManager.AdvisorPlan(); mp["throw"] = plan.Throw; mp["verdict"] = plan.Verdict ?? ""; mp["detail"] = plan.Detail ?? ""; } catch { }
                try { mp["runMode"] = Main.Settings != null && Main.Settings.MoneyPitRunMode; } catch { }
                try { var t = MoneyPitManager.ShockwaveTier(); if (t.HasValue) mp["targetTier"] = MoneyPitManager.TierName(t.Value); } catch { }
                root["moneypit"] = mp;
            });

            // --- Yggdrasil page: per-fruit chips (active / maxxed / inactive / purchasable / locked). Rebuilds
            //     the orchard grid the retired WinForms YggPanel used to show (lost in the companion cutover). ---
            Safe("fruits", () =>
            {
                var arr = new JSONArray();
                var yc = c != null ? c.yggdrasilController : null;
                var fruits = c != null && c.yggdrasil != null ? c.yggdrasil.fruits : null;
                if (c != null && yc != null && fruits != null)
                {
                    long seeds = 0; try { seeds = c.yggdrasil.seeds; } catch { }
                    root["yggSeeds"] = FormatBig(seeds);
                    int cap = 10; try { cap = yc.capTier(); } catch { }
                    root["yggCap"] = cap;
                    var fc = (yc.fruits != null && yc.fruits.Length > 0) ? yc.fruits[0] : null;
                    double thr = 0; try { thr = fc != null ? fc.tierThreshold() : 0; } catch { }
                    bool cardsOn = false; try { cardsOn = c.cards.cardsOn; } catch { }
                    int count = fruits.Count;
                    for (int i = 0; i < count; i++)
                    {
                        long maxTier = 0; try { maxTier = fruits[i].maxTier; } catch { }
                        if (i >= 15 && !cardsOn && maxTier == 0) continue;   // hide deep card-fruits until cards unlock
                        var o = new JSONObject();
                        o["i"] = i;
                        string name = "?"; try { if (yc.fruitName != null && i < yc.fruitName.Count) name = yc.fruitName[i]; } catch { }
                        o["name"] = name;
                        string state;
                        if (maxTier == 0)
                        {
                            long cost = 0; try { if (yc.baseSeedCost != null && i < yc.baseSeedCost.Count) cost = yc.baseSeedCost[i]; } catch { }
                            bool gateOpen = true;
                            try { if (i == 8) gateOpen = false; else if (i == 9) gateOpen = c.settings.itopodOn; else if (i == 14) gateOpen = c.settings.beastOn; } catch { }
                            bool affordable = cost > 0 && seeds >= cost && gateOpen;
                            state = affordable ? "purchasable" : "locked";
                            if (cost > 0 && gateOpen) o["cost"] = FormatBig(cost);   // seeds required to unlock
                            if (affordable) o["affordable"] = true;
                        }
                        else
                        {
                            bool maxxed = false; try { maxxed = fc != null && fc.fruitMaxxed(i); } catch { }
                            bool active = false; try { active = fruits[i].growing(); } catch { }
                            int tier = 0; try { tier = fc != null ? fc.harvestTier(i) : 0; } catch { }
                            double frac = 0; try { double sec = fruits[i].seconds; if (thr > 0) frac = (sec % thr) / thr; } catch { }
                            state = maxxed ? "maxxed" : active ? "active" : "inactive";
                            o["tier"] = tier;
                            o["maxTier"] = Num(maxTier);
                            o["frac"] = Num(maxxed ? 100 : active ? Math.Round(frac * 100, 0) : 0);   // growth % through the current tier
                        }
                        o["state"] = state;
                        arr.Add(o);
                    }
                }
                root["fruits"] = arr;
            });

            // --- Perks & Quirks page: the full guide-ordered plan with per-step status, keyed to the
            //     user's current chapter (was: only the single "next buy" reached the UI). ---
            Safe("perkPlan", () => { root["perkPlan"] = PlanArr(SpendPlanner.PerkPlanView()); });
            Safe("quirkPlan", () => { root["quirkPlan"] = PlanArr(SpendPlanner.QuirkPlanView()); });

            // --- cards: static meta (bonus-type rows / rarity map / cost list / sort vocab) + live filter arrays. ---
            Safe("cards", () =>
            {
                if (settings == null) return;
                if (_cardMetaCache == null)
                {
                    var meta = new JSONObject();
                    var types = new JSONArray();
                    var tn = CardManager.bonusTypeNames;
                    if (tn != null) foreach (var n in tn) types.Add(n);
                    meta["types"] = types;
                    var rar = new JSONObject();
                    if (CardManager.rarityList != null) foreach (var kv in CardManager.rarityList) rar[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
                    meta["rarities"] = rar;
                    var costs = new JSONArray();
                    if (CardManager.costList != null) foreach (var x in CardManager.costList) costs.Add(x);
                    meta["costs"] = costs;
                    var sorts = new JSONArray();
                    if (CardManager.sortList != null) foreach (var srt in CardManager.sortList) sorts.Add(srt);
                    meta["sorts"] = sorts;
                    if (types.Count > 0) _cardMetaCache = meta;   // only cache once the game enum is populated
                }
                var cardsObj = new JSONObject();
                if (_cardMetaCache != null) cardsObj["meta"] = _cardMetaCache;
                cardsObj["rarities"] = IntArr(settings.CardRarities);
                cardsObj["costs"] = IntArr(settings.CardCosts);
                var so = new JSONArray();
                if (settings.CardSortOrder != null) foreach (var srt in settings.CardSortOrder) so.Add(srt);
                cardsObj["sortOrder"] = so;
                root["cards"] = cardsObj;
            });

            // --- titan kill grid (W3): TitanSwapTargets bool[14] + static abbrev labels. ---
            Safe("titans", () =>
            {
                if (settings == null) return;
                var t = new JSONObject();
                var names = new JSONArray();
                var ab = TitanTables.Abbrev;
                if (ab != null) foreach (var n in ab) names.Add(n);
                t["names"] = names;
                var targets = new JSONArray();
                var tst = settings.TitanSwapTargets;
                if (tst != null) foreach (var b in tst) targets.Add(b);
                t["targets"] = targets;
                root["titans"] = t;
            });

            // --- boost list editors (W3): PriorityBoosts + BoostBlacklist int[] gear-id arrays. ---
            Safe("boostLists", () =>
            {
                if (settings == null) return;
                var bl = new JSONObject();
                var pri = new JSONArray(); var pb = settings.PriorityBoosts; if (pb != null) foreach (var id in pb) pri.Add(id);
                var blk = new JSONArray(); var bb = settings.BoostBlacklist; if (bb != null) foreach (var id in bb) blk.Add(id);
                bl["priority"] = pri; bl["blacklist"] = blk;
                root["boostLists"] = bl;
            });

            // --- profiles: list (throttled disk read) + active + auto-profile (M3) ---
            Safe("profiles", () =>
            {
                if (_profilesCache == null || (_seq % 5) == 1)   // refresh ~every 5th snapshot
                {
                    try
                    {
                        var dir = Main.GetProfilesDir();
                        if (dir != null && Directory.Exists(dir))
                        {
                            var files = Directory.GetFiles(dir, "*.json");
                            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                            var names = new string[files.Length];
                            for (int i = 0; i < files.Length; i++) names[i] = Path.GetFileNameWithoutExtension(files[i]);
                            _profilesCache = names;
                        }
                    }
                    catch { }
                }
                var p = new JSONObject();
                var list = new JSONArray();
                if (_profilesCache != null) foreach (var n in _profilesCache) list.Add(n);
                p["list"] = list;
                try { var d = Main.GetProfilesDir(); if (d != null) p["dir"] = d; } catch { }  // companion reads/writes here
                if (settings != null)
                {
                    p["active"] = settings.AllocationFile ?? "default";
                    p["autoProfile"] = settings.AutoProfile;
                    p["launchCompanion"] = settings.LaunchCompanion;
                }
                root["profiles"] = p;
            });

            // --- nav: pending in-game view request (F9), emitted only while fresh ---
            Safe("nav", () =>
            {
                if (_navView == null || _seq > _navUntilSeq) return;
                var n = new JSONObject();
                n["view"] = _navView;
                n["seq"] = (double)_navSeq;
                root["nav"] = n;
            });

            return root.ToString();   // compact — no newlines, so line-framing stays intact
        }

        // ------------------------------------------------------------- helpers

        private JSONObject GrowthNode(string key, Func<GrowthTracker.Sample, double> sel)
        {
            double perHr;
            bool ok = false;
            try { ok = GrowthTracker.Rate(sel, 60, false, out perHr); }
            catch { perHr = 0; }
            if (!ok) perHr = 0;
            perHr = Num(perHr);

            Queue<double> ring;
            if (!_spark.TryGetValue(key, out ring)) { ring = new Queue<double>(SparkPoints); _spark[key] = ring; }
            ring.Enqueue(perHr);
            while (ring.Count > SparkPoints) ring.Dequeue();

            var o = new JSONObject();
            o["k"] = key;
            o["rate"] = perHr;
            o["fmt"] = FormatBig(perHr);
            o["ready"] = ok;
            var spark = new JSONArray();
            foreach (var v in ring) spark.Add(v);
            o["spark"] = spark;
            return o;
        }

        private void PumpFeed()
        {
            ActivityItem a;
            try { a = Activity.Current; } catch { return; }
            if (a == null) return;
            try { if (Activity.Expired(a, DateTime.UtcNow)) return; } catch { }
            if (a.Seq == _lastActivitySeq) return;
            _lastActivitySeq = a.Seq;

            var o = new JSONObject();
            o["t"] = a.ReportedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            o["who"] = WhoOf(a.Kind);
            o["msg"] = a.Message ?? "";
            if (!string.IsNullOrEmpty(a.Detail)) o["detail"] = a.Detail;
            _feed.AddLast(o);
            while (_feed.Count > FeedMax) _feed.RemoveFirst();
        }

        private static string WhoOf(ActivityKind k)
        {
            switch (k)
            {
                case ActivityKind.Completed: return "ACTION";
                case ActivityKind.Queued: return "QUEUED";
                case ActivityKind.Warning: return "WARN";
                case ActivityKind.Failure: return "FAIL";
                default: return "INFO";
            }
        }

        private void Safe(string name, Action read)
        {
            try { read(); }
            catch (Exception e) { try { Main.LogDebug("UiBridge read '" + name + "': " + e.Message); } catch { } }
        }

        private static double Pct(double cur, double cap)
        {
            if (cap <= 0) return 0;
            return ClampPct(cur / cap * 100.0);
        }

        private static double ClampPct(double p)
        {
            if (double.IsNaN(p) || double.IsInfinity(p)) return 0;
            if (p < 0) return 0;
            if (p > 100) return 100;
            return Math.Round(p, 0);
        }

        // NaN/Infinity serialize to the literal tokens "NaN"/"Infinity" (invalid JSON) and would break the
        // whole snapshot line, so every raw double added to the DOM passes through here first.
        private static double Num(double v)
        {
            return (double.IsNaN(v) || double.IsInfinity(v)) ? 0 : v;
        }

        private static string FormatBig(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            double a = Math.Abs(v);
            if (a < 1000) return v.ToString("0.##", CultureInfo.InvariantCulture);
            int exp = (int)Math.Floor(Math.Log10(a));
            double mant = v / Math.Pow(10, exp);
            return mant.ToString("0.0", CultureInfo.InvariantCulture) + "e" + exp;
        }

        // Serialize a SpendPlanner plan (perks/quirks) into the companion's ordered-step array.
        private static JSONArray PlanArr(System.Collections.Generic.List<SpendPlanner.PlanStep> steps)
        {
            var arr = new JSONArray();
            if (steps != null)
                foreach (var s in steps)
                {
                    var o = new JSONObject();
                    o["name"] = s.Name ?? "";
                    o["cur"] = FormatBig(s.CurLevel);
                    o["target"] = s.Target == long.MaxValue ? "max" : s.Target.ToString(CultureInfo.InvariantCulture);
                    o["cost"] = FormatBig(s.Cost);
                    o["chapter"] = s.MinChapter;
                    o["state"] = s.State ?? "";
                    arr.Add(o);
                }
            return arr;
        }

        private static string FormatDuration(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            var ts = TimeSpan.FromSeconds(seconds);
            int h = (int)ts.TotalHours;
            if (h > 0) return h + "h " + ts.Minutes + "m";
            if (ts.Minutes > 0) return ts.Minutes + "m " + ts.Seconds + "s";
            return ts.Seconds + "s";
        }
    }
}
