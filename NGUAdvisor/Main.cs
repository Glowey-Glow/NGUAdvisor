using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using NGUAdvisor.AllocationProfiles;
using NGUAdvisor.AllocationProfiles.RebirthStuff;
using NGUAdvisor.Managers;
using UnityEngine;
using Application = UnityEngine.Application;

namespace NGUAdvisor
{
    public class Main : MonoBehaviour
    {
        public static readonly Character Character = FindObjectOfType<Character>();
        public static readonly InventoryController InventoryController = Character.inventoryController;
        public static StreamWriter OutputWriter;
        public static StreamWriter LootWriter;
        public static StreamWriter CombatWriter;
        public static StreamWriter PitSpinWriter;
        public static StreamWriter CardsWriter;
        public static StreamWriter DebugWriter;
        private static CustomAllocation _profile;
        public static CustomAllocation Profile => _profile;
        private float _timeLeft = 10.0f;
        private int _lastProgress = -1;
        private static GUIStyle _overlayStyle;
        private static float _overlayStyleScale = -1f;
        public static SettingsForm settingsForm;
        // NGU Advisor's own product version (SemVer). Bump by hand only at real milestones; the per-build
        // identity is the auto BuildTag below, so this no longer needs touching every compile.
        public const string Version = "1.0.0";
        // Build stamp, derived automatically from the hot-reload assembly identity (NGUAdvisor.r<yyMMddHHmmss>,
        // the unique per-compile name that already exists for Mono byte-load dedup). Replaces the old
        // hand-bumped codename — every compile yields a unique, sortable id (yyMMdd-HHmm) with zero edits.
        private static string _buildTag;
        public static string BuildTag
        {
            get
            {
                if (_buildTag != null) return _buildTag;
                try
                {
                    var name = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;
                    int i = name.IndexOf(".r", StringComparison.Ordinal);
                    var digits = i >= 0 ? new string(name.Substring(i + 2).Where(char.IsDigit).ToArray()) : "";
                    _buildTag = digits.Length >= 10 ? $"{digits.Substring(0, 6)}-{digits.Substring(6, 4)}" : "dev";
                }
                catch { _buildTag = "dev"; }
                return _buildTag;
            }
        }
        // -1 = unknown/unseeded. MUST NOT default to 0: statics reset on advisor reload, and a 0
        // baseline made SetResnipe read any real zone as "new zone fightable" — wiping the
        // completed snipe mid-run (user-reported). SetResnipe re-seeds from the current best zone.
        private static int _furthestZone = -1;

        private static string _dir;
        private static string _profilesDir;

        private static bool _tempSwapped = false;

        // FileSystemWatcher events fire on background ThreadPool threads. Their handlers must NOT touch
        // Unity/WinForms objects (doing so hard-crashes the game). Instead they set these flags, which the
        // main-thread Update() drains. See the deferred handling in Update().
        private static volatile bool _reloadAllocationPending;
        private static volatile bool _reloadProfilesPending;
        private static volatile bool _reloadSettingsPending;

        // MAIN-THREAD RULE: WinForms handlers (profile Switch/Apply buttons etc.) must NEVER call
        // LoadAllocation directly — allocation work touches Unity objects and hard-crashes off the
        // Unity thread (user-reported: dashboard Switch crash). They request; Update() drains.
        public static void RequestAllocationReload() => _reloadAllocationPending = true;

        public static FileSystemWatcher ConfigWatcher;
        public static FileSystemWatcher AllocationWatcher;
        public static FileSystemWatcher ZoneWatcher;

        public static bool IgnoreNextChange { get; set; }

        public static SavedSettings Settings;

        private static void WriterLog(StreamWriter writer, string msg)
        {
            var formattedDate = $"{DateTime.Now.ToShortDateString()}-{DateTime.Now.ToShortTimeString()} ({Math.Floor(Character.rebirthTime.totalseconds)}s)";
            writer.WriteLine($"{formattedDate}: {msg}");
        }

        public static void Log(string msg) => WriterLog(OutputWriter, msg);

        // In-memory mirror of loot.log for the LOGS reader (ring, newest first — same pattern as
        // the advisor feed). File writes are unchanged.
        public static readonly System.Collections.Generic.List<string> LootFeed
            = new System.Collections.Generic.List<string>();

        public static void LogLoot(string msg)
        {
            WriterLog(LootWriter, msg);
            try
            {
                // A kill's drops can arrive as one multi-line message — one ring entry per line.
                foreach (var line in (msg ?? "").Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length == 0) continue;
                    LootFeed.Insert(0, $"{DateTime.Now:HH:mm} {t}");
                }
                if (LootFeed.Count > 400) LootFeed.RemoveRange(400, LootFeed.Count - 400);
            }
            catch { }
        }

        public static void LogCombat(string msg) => WriterLog(CombatWriter, msg);

        public static void LogPitSpin(string msg) => WriterLog(PitSpinWriter, msg);

        public static void LogCard(string msg) => WriterLog(CardsWriter, msg);

        public static void LogDebug(string msg) => WriterLog(DebugWriter, msg);

        public static string GetSettingsDir() => _dir;

        public static string GetLogDir() => Path.Combine(_dir, "logs");

        public static string GetProfilesDir() => _profilesDir;

        // Safe item-name lookup for the gear editor (the game knows every item's name by id).
        public static string ItemName(int id)
        {
            try
            {
                if (id <= 0) return "";
                return InventoryController.itemInfo.itemName[id];
            }
            catch { return "?"; }
        }

        // C1 naming convention (user-approved): collapse the game's stacked "Ascended Ascended ..."
        // prefixes to "Ascended x{n} ..." from the second repetition onward. Counts at runtime, so it
        // adapts to any chain depth. Applied everywhere the advisor renders item names.
        public static string CollapseAscended(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int n = 0, pos = 0;
            while (name.Length - pos >= 9 && string.CompareOrdinal(name, pos, "Ascended ", 0, 9) == 0)
            {
                n++;
                pos += 9;
            }
            return n < 2 ? name : $"Ascended x{n} {name.Substring(pos)}";
        }

        public static string ItemNameNice(int id) => CollapseAscended(ItemName(id));

        public void Unload()
        {
            // Every step individually guarded: a single throw here used to ABORT the bootstrap's
            // reload half-done (form closed, new payload never loaded — user-reported). Worse, the
            // old catch called LogDebug AFTER DebugWriter.Close(), so the logging itself threw.
            // Writers close LAST; nothing below may escape.
            void Try(Action a) { try { a(); } catch { } }

            Try(() => CancelInvoke("AutomationRoutine"));
            Try(() => CancelInvoke("MonitorLog"));
            Try(() => CancelInvoke("QuickStuff"));
            Try(() => CancelInvoke("SetResnipe"));
            Try(() => CancelInvoke("ShowBoostProgress"));

            Try(() => settingsForm.Close());
            Try(() => settingsForm.Dispose());
            Try(ProfileEditorForm.CloseEditor);

            Try(() => ConfigWatcher.Dispose());
            Try(() => AllocationWatcher.Dispose());
            Try(() => ZoneWatcher.Dispose());

            Try(() => LootWriter.Close());
            Try(() => CombatWriter.Close());
            Try(() => PitSpinWriter.Close());
            Try(() => CardsWriter.Close());
            Try(() => DebugWriter.Close());
            Try(() => OutputWriter.Close());
        }

        public void Start()
        {
            try
            {
                _dir = Path.Combine(Environment.ExpandEnvironmentVariables("%userprofile%/AppData/LocalLow"), "NGUAdvisor");
                if (!Directory.Exists(_dir))
                    Directory.CreateDirectory(_dir);

                // One-time migration: the product was renamed from "NGUInjector" to NGU Advisor. Move any
                // settings/profiles/logs the user already had in LocalLow\NGUInjector into the new folder.
                // Merge per-entry (don't gate on the new folder being absent) because Run NGU Advisor.bat
                // may have already created it holding only injector-path.txt.
                try
                {
                    var oldDir = Path.Combine(Environment.ExpandEnvironmentVariables("%userprofile%/AppData/LocalLow"), "NGUInjector");
                    if (Directory.Exists(oldDir) && !string.Equals(oldDir, _dir, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var d in Directory.GetDirectories(oldDir))
                        {
                            var dest = Path.Combine(_dir, Path.GetFileName(d));
                            if (!Directory.Exists(dest)) Directory.Move(d, dest);
                        }
                        foreach (var f in Directory.GetFiles(oldDir))
                        {
                            var dest = Path.Combine(_dir, Path.GetFileName(f));
                            if (!File.Exists(dest)) File.Move(f, dest);
                        }
                    }
                }
                catch { /* best-effort; a fresh install just starts clean in the new folder */ }

                var logDir = Path.Combine(_dir, "logs");
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);

                OutputWriter = new StreamWriter(Path.Combine(logDir, "inject.log")) { AutoFlush = true };
                LootWriter = new StreamWriter(Path.Combine(logDir, "loot.log")) { AutoFlush = true };
                CombatWriter = new StreamWriter(Path.Combine(logDir, "combat.log")) { AutoFlush = true };
                PitSpinWriter = new StreamWriter(Path.Combine(logDir, "pitspin.log"), true) { AutoFlush = true };
                CardsWriter = new StreamWriter(Path.Combine(logDir, "cards.log"), true) { AutoFlush = true };
                DebugWriter = new StreamWriter(Path.Combine(logDir, "debug.log")) { AutoFlush = true };
                // Health probe: if debug.log stays empty even of this line, the writer itself is broken
                // and every "Advisor ... failed" message has been invisible.
                LogDebug($"debug.log writer alive (v{Version} build {BuildTag})");

                _profilesDir = Path.Combine(_dir, "profiles");
                if (!Directory.Exists(_profilesDir))
                    Directory.CreateDirectory(_profilesDir);

                // Install any missing embedded goal-loadout presets before profiles are listed/loaded.
                Managers.PresetInstaller.InstallMissing(_profilesDir);

                var oldPath = Path.Combine(_dir, "allocation.json");
                var newPath = Path.Combine(_profilesDir, "default.json");

                if (File.Exists(oldPath) && !File.Exists(newPath))
                    File.Move(oldPath, newPath);
            }
            catch (Exception e)
            {
                LogDebug(e.Message);
                LogDebug(e.StackTrace);
                Loader.Unload();
                return;
            }

            try
            {
                Log("Injected");
                LogLoot("Starting Loot Writer");
                LogCombat("Starting Combat Writer");
                LockManager.ReleaseLock();

                Settings = new SavedSettings(_dir);

                if (!Settings.LoadSettings())
                {
                    var temp = new SavedSettings(null);

                    Settings.MassUpdate(temp);

                    Log($"Created default settings");
                }

                settingsForm = new SettingsForm();

                Settings.SetSaveDisabled(true);

                if (string.IsNullOrEmpty(Settings.AllocationFile))
                    Settings.AllocationFile = "default";

                Settings.SetSaveDisabled(false);

                LoadAllocation();
                LoadAllocationProfiles();

                ZoneWatcher = new FileSystemWatcher
                {
                    Path = _dir,
                    Filter = "zoneOverride.json",
                    NotifyFilter = NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };

                ZoneWatcher.Changed += (sender, args) =>
                {
                    Log(_dir);
                    ZoneStatHelper.CreateOverrides(_dir);
                };

                ConfigWatcher = new FileSystemWatcher
                {
                    Path = _dir,
                    Filter = "settings.json",
                    NotifyFilter = NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };

                ConfigWatcher.Changed += (sender, args) =>
                {
                    if (IgnoreNextChange)
                    {
                        IgnoreNextChange = false;
                        return;
                    }
                    // Defer to the main thread (touches Settings/WinForms/Unity).
                    _reloadSettingsPending = true;
                };

                AllocationWatcher = new FileSystemWatcher
                {
                    Path = _profilesDir,
                    Filter = "*.json",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };

                // These fire on background threads; defer the actual work (which reloads allocations and
                // touches Unity/WinForms) to the main thread via flags drained in Update().
                AllocationWatcher.Changed += (sender, args) => { _reloadAllocationPending = true; };
                AllocationWatcher.Created += (sender, args) => { _reloadProfilesPending = true; };
                AllocationWatcher.Deleted += (sender, args) => { _reloadProfilesPending = true; };
                AllocationWatcher.Renamed += (sender, args) => { _reloadProfilesPending = true; };

                Settings.SaveSettings();
                Settings.LoadSettings();

                ZoneStatHelper.CreateOverrides(_dir);

                settingsForm.UpdateFromSettings(Settings);
                settingsForm.Show();

                InvokeRepeating("AutomationRoutine", 0.0f, 10.0f);
                InvokeRepeating("MonitorLog", 0.0f, 1f);
                InvokeRepeating("QuickStuff", 0.0f, .5f);
                InvokeRepeating("ShowBoostProgress", 0.0f, 60.0f);
                InvokeRepeating("SetResnipe", 0f, 1f);
            }
            catch (Exception e)
            {
                LogDebug(e.ToString());
                LogDebug(e.StackTrace);
                LogDebug(e.InnerException.ToString());
            }
        }

        // Settings saves used to rebuild the ENTIRE legacy form synchronously — with the advisor
        // writing settings constantly, that ran heavy list refreshes mid-click (and during Start,
        // BEFORE the form ever showed: a throw there = no GUI at all). Saves now only request;
        // Update() coalesces bursts and refreshes at most once a second, guarded.
        private static volatile bool _updateFormPending;
        private static float _formUpdateCooldown;

        public static void UpdateForm(SavedSettings newSettings) => _updateFormPending = true;

        public void Update()
        {
            // Drain deferred file-watcher work on the main thread (see the watcher handlers). Doing this
            // off-thread previously crashed the game (e.g. digger menu UI refresh from a background thread).
            if (_reloadSettingsPending)
            {
                _reloadSettingsPending = false;
                try { Settings.LoadSettings(); settingsForm.UpdateFromSettings(Settings); LoadAllocation(); }
                catch (Exception e) { LogDebug($"Deferred settings reload failed: {e.Message}"); }
            }
            if (_reloadProfilesPending)
            {
                _reloadProfilesPending = false;
                try { LoadAllocationProfiles(); }
                catch (Exception e) { LogDebug($"Deferred profile-list reload failed: {e.Message}"); }
            }
            if (_reloadAllocationPending)
            {
                _reloadAllocationPending = false;
                try { LoadAllocation(); }
                catch (Exception e) { LogDebug($"Deferred allocation reload failed: {e.Message}"); }
            }

            _formUpdateCooldown -= Time.deltaTime;
            if (_updateFormPending && _formUpdateCooldown <= 0)
            {
                _updateFormPending = false;
                _formUpdateCooldown = 1f;
                try { settingsForm.UpdateFromSettings(Settings); }
                catch (Exception e) { LogDebug($"Deferred form update failed: {e.Message}"); }
            }

            // Refresh the live status strip in the settings window (main thread; throttled + guarded).
            settingsForm.UpdateStatus();

            _timeLeft -= Time.deltaTime;

            int progress = (int)Math.Floor(_timeLeft / 10 * 100);
            if (progress != _lastProgress)
            {
                _lastProgress = progress;
                settingsForm.UpdateProgressBar(progress);
            }

            if (Input.GetKeyDown(KeyCode.F1))
            {
                if (!settingsForm.Visible)
                    settingsForm.Show();

                settingsForm.BringToFront();
            }

            if (Input.GetKeyDown(KeyCode.F2))
                Settings.GlobalEnabled = !Settings.GlobalEnabled;

            if (Input.GetKeyDown(KeyCode.F3))
                QuickSave();

            if (Input.GetKeyDown(KeyCode.F7))
                QuickLoad();

            if (Input.GetKeyDown(KeyCode.F5))
                DumpEquipped();

            if (Input.GetKeyDown(KeyCode.F9))
                ProfileEditorForm.ShowEditor(_profilesDir, Settings.AllocationFile);

            if (Input.GetKeyDown(KeyCode.F10))
                Managers.GearOptimizerDiagnostic.Run();

            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (Settings.QuickLoadout.Length > 0)
                {
                    if (_tempSwapped)
                    {
                        Log("Restoring Previous Loadout");
                        LoadoutManager.RestoreTempLoadout();
                    }
                    else
                    {
                        Log("Equipping Quick Loadout");
                        LoadoutManager.SaveTempLoadout();
                        LoadoutManager.ChangeGear(Settings.QuickLoadout);
                    }
                }

                if (Settings.QuickDiggers.Length > 0)
                {
                    if (_tempSwapped)
                    {
                        Log("Equipping Previous Diggers");
                        DiggerManager.RestoreTempDiggers();
                        DiggerManager.RecapDiggers();
                    }
                    else
                    {
                        Log("Equipping Quick Diggers");
                        DiggerManager.SaveTempDiggers();
                        DiggerManager.EquipDiggers(Settings.QuickDiggers);
                        DiggerManager.RecapDiggers();
                    }
                }

                if (Settings.QuickBeards.Length > 0)
                {
                    if (_tempSwapped)
                    {
                        Log("Equipping Previous Beards");
                        BeardManager.RestoreTempBeards();
                    }
                    else
                    {
                        Log("Equipping Quick Beards");
                        BeardManager.SaveTempBeards();
                        BeardManager.EquipBeards(Settings.QuickBeards);
                    }
                }

                _tempSwapped = !_tempSwapped;
            }

            // F11 reserved for testing
            if (Input.GetKeyDown(KeyCode.F11))
            {
            }
        }

        public void LateUpdate() => SnipeZone();

        public float NakedAdventurePower() => InventoryController.adventureAttackBonus();

        public float CubePower() => InventoryController.cubePower();

        public float NakedAdventureToughness() => InventoryController.adventureDefenseBonus();

        public float CubeToughness() => InventoryController.cubeToughness();

        public long TotalNudeEnergyCap()
        {
            var num = (double)
                // Base Energy Cap
                Character.capEnergy

                // Perk Modifier
                * Character.adventureController.itopod.totalEnergyCapBonus()

                // MacGuffin Modifier
                * Character.inventory.macguffinBonuses[1];

            // Quirk Modifier
            num *= Character.beastQuestPerkController.totalEnergyCapBonus();

            // Wish modifier
            num *= Character.wishesController.totalEnergyCapBonus();

            if (num < 1.0)
                num = 1.0;

            return num >= Character.hardCap() ? Character.hardCap() : (long)num;
        }

        public long TotalNudeMagicCap()
        {
            var num = (double)
                // Base Magic Cap
                Character.magic.capMagic

                // Perk Modifier
                * Character.adventureController.itopod.totalMagicCapBonus()

                // MacGuffin Modifier
                * Character.inventory.macguffinBonuses[3];

            // Quirk Modifier
            num *= Character.beastQuestPerkController.totalMagicCapBonus();

            // Wish modifier
            num *= Character.wishesController.totalMagicCapBonus();

            if (num < 1.0)
                num = 1.0;

            return num >= Character.hardCap() ? Character.hardCap() : (long)num;
        }

        public double TotalNudeEnergyPower()
        {
            var num = (double)Character.energyPower * Character.adventureController.itopod.totalEnergyPowerBonus();
            num *= Character.inventory.macguffinBonuses[0];
            num *= Character.beastQuestPerkController.totalEnergyPowerBonus();
            num *= Character.wishesController.totalEnergyPowerBonus();
            if (num < 1.0)
                num = 1.0;

            if (num >= Character.hardCapPowBar())
                num = Character.hardCapPowBar();

            return num;
        }

        public double TotalNudeMagicPower()
        {
            var num = (double)Character.magic.magicPower * Character.adventureController.itopod.totalMagicPowerBonus();
            num *= Character.inventory.macguffinBonuses[2];
            num *= Character.beastQuestPerkController.totalMagicPowerBonus();
            num *= Character.wishesController.totalMagicPowerBonus();

            if (num < 1.0)
                num = 1.0;

            if (num >= Character.hardCapPowBar())
                num = Character.hardCapPowBar();

            return num;
        }

        public double TotalNudeEnergyBar()
        {
            var num = (double)Character.energyBars * Character.adventureController.itopod.totalEnergyBarBonus();
            num *= Character.beastQuestPerkController.totalEnergyBarBonus();
            num *= Character.wishesController.totalEnergyBarBonus();
            num *= Character.inventory.macguffinBonuses[6];

            if (num < 1.0)
                num = 1.0;

            if (num > Character.hardCapPowBar())
                num = Character.hardCapPowBar();

            return num;
        }

        public double TotalNudeMagicBar()
        {
            var num = (double)Character.magic.magicPerBar * Character.adventureController.itopod.totalMagicBarBonus();
            num *= Character.beastQuestPerkController.totalMagicBarBonus();
            num *= Character.wishesController.totalMagicBarBonus();
            num *= Character.inventory.macguffinBonuses[7];

            if (num < 1.0)
                num = 1.0;

            if (num > Character.hardCapPowBar())
                num = Character.hardCapPowBar();

            return num;
        }

        private void QuickSave()
        {
            Log("Writing quicksave and json");
            var data = Character.importExport.getBase64Data();
            using (var writer = new StreamWriter(Path.Combine(_dir, "NGUSave.txt")))
                writer.WriteLine(data);

            data = JsonUtility.ToJson(Character.importExport.gameStateToData());
            using (var writer = new StreamWriter(Path.Combine(_dir, "NGUSave.json")))
                writer.WriteLine(data);

            // Base Power
            Log($"Base Power: {NakedAdventurePower()}");
            // Base Toughness
            Log($"Base Toughness: {NakedAdventureToughness()}");
            // Cube Power
            Log($"Cube Power: {CubePower()}");
            // Cube Toughness
            Log($"Cube Power: {CubeToughness()}");
            // Nude Energy Cap
            Log($"Nude Energy Cap: {TotalNudeEnergyCap()}");
            // Nude Magic Cap
            Log($"Nude Magic Cap: {TotalNudeMagicCap()}");
            // Nude Energy Power
            Log($"Nude Energy Power: {TotalNudeEnergyPower()}");
            // Nude Magic Power
            Log($"Nude Magic Power: {TotalNudeMagicPower()}");
            // Nude Energy Bars
            Log($"Nude Energy Bars: {TotalNudeEnergyBar()}");
            // Nude Magic Bars
            Log($"Nude Magic Bars: {TotalNudeMagicBar()}");

            Character.saveLoad.saveGamestateToSteamCloud();
        }

        private void QuickLoad()
        {
            var filename = Path.Combine(_dir, "NGUSave.txt");
            if (!File.Exists(filename))
            {
                Log("Quicksave doesn't exist");
                return;
            }

            var saveTime = File.GetLastWriteTime(filename);
            var s = DateTime.Now.Subtract(saveTime);
            var secDiff = (int)s.TotalSeconds;
            if (secDiff > 120)
            {
                var diff = saveTime.GetPrettyDate();

                var confirmResult = MessageBox.Show($"Last quicksave was {diff}. Are you sure you want to load?",
                    "Load Quicksave"
                    , MessageBoxButtons.YesNo);

                if (confirmResult == DialogResult.No)
                    return;
            }

            Log("Loading quicksave");
            string base64Data;
            try
            {
                base64Data = File.ReadAllText(filename);
            }
            catch (Exception e)
            {
                LogDebug($"Failed to read quicksave: {e.Message}");
                return;
            }

            try
            {
                var saveDataFromString = Character.importExport.getSaveDataFromString(base64Data);
                var dataFromString = Character.importExport.getDataFromString(base64Data);

                if ((dataFromString == null || dataFromString.version < 361) &&
                    Application.platform != RuntimePlatform.WindowsEditor)
                {
                    Log("Bad save version");
                    return;
                }

                if (dataFromString.version > Character.getVersion())
                {
                    Log("Bad save version");
                    return;
                }

                Character.saveLoad.loadintoGame(saveDataFromString);
            }
            catch (Exception e)
            {
                LogDebug($"Failed to load quicksave: {e.Message}");
            }
        }

        // Stuff on a very short timer
        private void QuickStuff()
        {
            try
            {
                if (!Settings.GlobalEnabled)
                    return;

                var needsAllocation = false;
                if (Character.bossID == 0)
                    needsAllocation = true;

                if (Settings.AutoFight || Settings.MoneyPitRunMode)
                {
                    var bc = Character.bossController;
                    if (!bc.isFighting && !bc.nukeBoss)
                    {
                        var canNuke = bc.character.attack / 5.0 > bc.character.bossDefense && bc.character.defense / 5.0 > bc.character.bossAttack;
                        var shouldNuke = !MoneyPitManager.NeedsGold() || Character.rebirthTime.totalseconds > 180.0;
                        if (canNuke && shouldNuke)
                        {
                            bc.startNuke();
                        }
                        else if (shouldNuke || Character.bossID < 29)
                        {
                            double characterDamage = (bc.character.attack - bc.character.bossDefense - bc.character.bossRegen) * 0.02;
                            double bossDamage = (bc.character.bossAttack - bc.character.defense - bc.character.hpRegen) * 0.02;

                            bool doFight;

                            if (characterDamage <= 0)
                            {
                                // Character does no damage - don't fight
                                doFight = false;
                            }
                            else if (bossDamage <= 0)
                            {
                                // Boss does no damage - fight
                                doFight = true;
                            }
                            else if (bc.character.curHP == bc.character.maxHP)
                            {
                                // Character is at full HP - there is no use for waiting
                                doFight = true;
                            }
                            else
                            {
                                double characterAttacksToKill = Math.Ceiling(bc.character.bossCurHP / characterDamage);
                                double bossAttacksToKill = Math.Ceiling(bc.character.curHP / bossDamage);

                                // Boss attack logic executes first, so fight only if the character will kill the boss in fewer attacks than the boss will kill the character
                                doFight = characterAttacksToKill < bossAttacksToKill;
                            }

                            if (doFight)
                            {
                                bc.beginFight();
                                bc.stopButton.gameObject.SetActive(true);
                            }
                        }
                    }
                }

                if (Settings.MoneyPitRunMode && Character.machine.realBaseGold <= 0.0 && MoneyPitManager.NeedsLowerTier())
                {
                    if (Character.buttons.bloodMagic.interactable)
                    {
                        var tier = MoneyPitManager.ShockwaveTier();

                        var startIndex = 0;
                        if (tier == 1e15 && Character.realGold >= 1e18)
                            startIndex = 4;
                        else if (tier == 1e13 && Character.realGold >= 1e15)
                            startIndex = 3;

                        if (startIndex > 0)
                        {
                            Character.removeMostMagic();
                            for (var i = startIndex; i < Character.bloodMagicController.ritualsUnlocked(); i++)
                                Character.bloodMagicController.bloodMagics[i].cap();
                        }
                    }
                }

                if (needsAllocation)
                    _profile.DoAllocations();

                QuestManager.ManageQuests();

                if (Settings.AutoMoneyPit)
                    MoneyPitManager.CheckMoneyPit();

                if (Settings.AutoSpin)
                    MoneyPitManager.DoDailySpin();

                if (Settings.AutoSpellSwap)
                {
                    var spaghetti = (int)Math.Round((Character.bloodMagicController.lootBonus() - 1) * 100);
                    var counterfeit = (int)Math.Round((Character.bloodMagicController.goldBonus() - 1) * 100);
                    double number = Character.bloodMagic.rebirthPower;
                    Character.bloodMagic.rebirthAutoSpell = Settings.BloodNumberThreshold > 0 && number < Settings.BloodNumberThreshold;
                    Character.bloodMagic.goldAutoSpell = Settings.CounterfeitThreshold > 0 && counterfeit < Settings.CounterfeitThreshold;
                    Character.bloodMagic.lootAutoSpell = Settings.SpaghettiThreshold > 0 && spaghetti < Settings.SpaghettiThreshold;
                    Character.bloodSpells.updateGoldToggleState();
                    Character.bloodSpells.updateLootToggleState();
                    Character.bloodSpells.updateRebirthToggleState();
                }

                WishManager.UpdateWishMenu();
            }
            catch (Exception e)
            {
                LogDebug(e.Message);
                LogDebug(e.StackTrace);
            }
        }

        // Runs every 10 seconds, our main loop
        private void AutomationRoutine()
        {
            try
            {
                if (!Settings.GlobalEnabled)
                {
                    _timeLeft = 10f;
                    return;
                }

                if (Settings.ManageInventory && !InventoryController.midDrag)
                {
                    ih[] converted = Character.inventory.GetConvertedInventory().ToArray();
                    ih[] boostSlots = InventoryManager.GetBoostSlots(converted);
                    InventoryManager.EnsureFiltered(converted);
                    InventoryManager.ManageConvertibles(converted);
                    InventoryManager.MergeEquipped(converted);
                    InventoryManager.MergeInventory(converted);
                    InventoryManager.MergeBoosts(converted);
                    InventoryManager.MergeGuffs(converted);
                    InventoryManager.BoostInventory(boostSlots);
                    InventoryManager.BoostInfinityCube();
                    InventoryManager.ManageBoostConversion(boostSlots);
                    InventoryController.updateInventory();
                }

                if (Settings.Autosave && Character.settings.dailySaveRewardTime.totalseconds >= 82800.0)
                {
                    Character.settings.dailySaveRewardTime.reset();
                    Character.addAP(200);
                    var customPath = $"{Application.persistentDataPath}/NGUSave-Build-{Character.getVersion()}-{DateTime.Now:MMMM-dd-HH-mm} (advisor).txt";
                    PlayerPrefs.SetString("savedPath", customPath);
                    Character.lastTime = Epoch.Current();
                    var data = Character.importExport.getBase64Data();
                    using (var writer = new StreamWriter(customPath))
                        writer.WriteLine(data);
                }

                ZoneHelpers.RefreshTitanSnapshots();
                if (Settings.ManageTitans || Settings.NeedsGoldSwap())
                {
                    if (ZoneHelpers.AnyTitansSpawningSoon() != LockManager.HasTitanLock())
                        LockManager.TryTitanSwap();
                }
                else if (LockManager.HasTitanLock())
                {
                    LockManager.TryTitanSwap();
                }

                if (Settings.ManageYggdrasil && Character.buttons.yggdrasil.interactable)
                {
                    YggdrasilManager.ManageYggHarvest();
                    YggdrasilManager.CheckFruits();
                }

                // Advisor auto-apply (Phase B): opt-in per-system application of advisor recs.
                // Before the AutoBuy block, which can return early.
                AdvisorApply.Tick();

                if (Settings.AutoBuyEM || Settings.AutoBuyAdventure)
                {
                    // We haven't unlocked custom purchases yet
                    if (Character.highestBoss < 17)
                        return;

                    long total = 0;

                    var buyEnergy = false;
                    var buyR3 = false;
                    var buyMagic = false;

                    var buyPower = false;
                    var buyToughness = false;
                    var buyHP = false;
                    var buyRegen = false;

                    var ePurchase = Character.energyPurchases;
                    var mPurchase = Character.magicPurchases;
                    var r3Purchase = Character.res3Purchases;

                    if (Settings.AutoBuyEM)
                    {
                        var energy = ePurchase.customAllCost() > 0;
                        var r3 = Character.res3.res3On && r3Purchase.customAllCost() > 0;
                        var magic = Character.highestBoss >= 37 && mPurchase.customAllCost() > 0;

                        if (energy)
                            total += ePurchase.customAllCost();

                        if (magic)
                            total += mPurchase.customAllCost();

                        if (r3)
                            total += r3Purchase.customAllCost();

                        buyEnergy = energy;
                        buyR3 = r3;
                        buyMagic = magic;
                    }

                    var aPurchase = Character.adventurePurchases;
                    long power = aPurchase.customPowerCost(Character.settings.customPowerInput);
                    long toughness = aPurchase.customToughnessCost(Character.settings.customToughnessInput);
                    long health = aPurchase.customHPCost(Character.settings.customHPInput);
                    long regen = aPurchase.customRegenCost(Character.settings.customRegenInput);

                    if (Settings.AutoBuyAdventure)
                    {
                        buyPower = power > 0;
                        buyToughness = toughness > 0;
                        buyHP = health > 3; // UI does NOT allow you to set HP purchase to less than 10 (for 3xp)
                        buyRegen = regen > 0;

                        total += (buyPower ? power : 0)
                            + (buyToughness ? toughness : 0)
                            + (buyHP ? health : 0)
                            + (buyRegen ? regen : 0);
                    }

                    if (total > 0)
                    {
                        double numPurchases = Math.Floor((double)(Character.realExp / total));
                        numPurchases = Math.Min(numPurchases, 10);

                        if (numPurchases > 0)
                        {
                            var t = string.Empty;
                            if (buyEnergy)
                                t += "/exp";

                            if (buyMagic)
                                t += "/magic";

                            if (buyR3)
                                t += "/res3";

                            if (buyPower)
                                t += "/power";

                            if (buyToughness)
                                t += "/tougness";

                            if (buyHP)
                                t += "/hp";

                            if (buyHP)
                                t += "/regen";

                            t = t.Substring(1);

                            Log($"Buying {numPurchases} {t} purchases");
                            for (var i = 0; i < numPurchases; i++)
                            {
                                if (buyEnergy)
                                    ePurchase.CallMethod("buyCustomAll");

                                if (buyMagic)
                                    mPurchase.CallMethod("buyCustomAll");

                                if (buyR3)
                                    r3Purchase.CallMethod("buyCustomAll");

                                if (buyPower)
                                    aPurchase.CallMethod("buyCustomPower");

                                if (buyToughness)
                                    aPurchase.CallMethod("buyCustomToughness");

                                if (buyHP)
                                    aPurchase.CallMethod("buyCustomHP");
                            }
                        }
                    }
                }

                _profile.DoAllocations();

                _profile.CastBloodSpells();

                if (Settings.AutoQuest && Character.buttons.beast.interactable)
                {
                    // Only build the converted inventory snapshot when it will actually be used
                    if (!InventoryController.midDrag)
                    {
                        ih[] converted = Character.inventory.GetConvertedInventory().ToArray();
                        InventoryManager.ManageQuestItems(converted);
                    }
                    QuestManager.PerformSlowActions();
                }

                if (Character.adventure.zone >= 1000)
                    ITOPODManager.UpdateMaxFloor();

                if (!Settings.AutoRebirth || !_profile.DoRebirth())
                {
                    if (Settings.MoneyPitRunMode && MoneyPitRunRebirth.RebirthAvailable())
                        BaseRebirth.DoRebirth();
                }

                if (Settings.ManageMayo)
                    CardManager.CheckManas();
                if (Settings.TrashCards)
                    CardManager.TrashCards();
                if (Settings.AutoCastCards)
                    CardManager.CastCards();
                if (Settings.CardSortEnabled && Settings.CardSortOrder.Length > 0)
                    CardManager.SortCards();

                if (Settings.ManageCooking)
                    CookingManager.ManageFood();

                if (Settings.ManageTitans)
                {
                    for (int i = 6; i <= 12; i++)
                    {
                        if (!Settings.TitanSwapTargets[i])
                            continue;

                        var version = ZoneHelpers.TitanVersion(i);
                        while (version < 4)
                        {
                            if (ZoneHelpers.AutokillAvailable(i, version + 1))
                                version++;
                            else
                                break;
                        }

                        if (Settings.TitanCombatMode == 4)
                        {
                            while (version > 0)
                            {
                                if (ZoneHelpers.AutokillAvailable(i, version))
                                    break;
                                version--;
                            }
                            if (version <= 0)
                                Settings.TitanSwapTargets[i] = false;
                        }

                        if (version > 0)
                            ZoneHelpers.SetTitanVersion(i, version);
                    }
                }

                settingsForm.UpdateTitanVersions();
            }
            catch (Exception e)
            {
                LogDebug(e.Message);
                LogDebug(e.StackTrace);
            }
            _timeLeft = 10f;
        }

        public static void LoadAllocation()
        {
            _profile = new CustomAllocation(_profilesDir, Settings.AllocationFile);
            try
            {
                _profile.ReloadAllocation();
            }
            catch (Exception e)
            {
                LogDebug(e.Message);
            }
        }

        private static void LoadAllocationProfiles()
        {
            string[] files = Directory.GetFiles(_profilesDir);
            settingsForm.UpdateProfileList(files.Select(Path.GetFileNameWithoutExtension).ToArray(), Settings.AllocationFile);
        }

        private void SnipeZone()
        {
            try
            {
                CombatHelpers.IsCurrentlyGoldSniping = false;
                CombatHelpers.IsCurrentlyQuesting = false;
                CombatHelpers.IsCurrentlyAdventuring = false;
                CombatHelpers.IsCurrentlyFightingTitan = false;

                if (!Settings.GlobalEnabled)
                    return;

                if (!Character.buttons.adventure.interactable)
                    return;

                CombatManager.UpdateFightTimer(Time.deltaTime);

                // If tm ever drops to 0, reset our gold loadout stuff (the "rebirth" snipe trigger —
                // gated by its S3 toggle in manual mode; advisor always re-snipes here).
                if (Character.machine.realBaseGold == 0.0 && Settings.GoldSnipeComplete)
                {
                    _furthestZone = -1;
                    Settings.TitanMoneyDone = new bool[ZoneHelpers.TitanZones.Length];
                    if (Settings.AdvisorGold || Settings.SnipeOnRebirth)
                    {
                        Log("Time Machine Gold is 0. Lets reset gold snipe zone.");
                        Settings.GoldSnipeComplete = false;
                        LastSnipeTrigger = "rebirth (TM empty)";
                    }
                }

                // Pit run logic
                if (MoneyPitManager.ShockwaveTier() <= 1e18 && MoneyPitManager.MoneyPitReady() && !MoneyPitManager.NeedsRebirth())
                {
                    if (MoneyPitManager.NeedsGold())
                    {
                        CombatManager.DoZone(0);
                    }
                    else // To avoid getting more gold
                    {
                        CombatHelpers.IsCurrentlyAdventuring = true;
                        CombatManager.DoZone(1000); // Checks fight timer and gold lock
                        ITOPODManager.Update();
                    }
                    return;
                }
                // This logic should trigger only if Time Machine is ready
                else if (Character.buttons.brokenTimeMachine.interactable && !Character.challenges.timeMachineChallenge.inChallenge)
                {
                    if (Character.machine.realBaseGold == 0.0)
                    {
                        CombatManager.DoZone(0);
                        return;
                    }

                    // Go to our gold loadout zone next to get a high gold drop
                    if (Settings.ManageGoldLoadouts && !Settings.GoldSnipeComplete)
                    {
                        // Could be busy with other actions
                        if (LockManager.HasGoldLock() || LockManager.CanSwap())
                        {
                            UpdateFurthestZone();
                            if (_furthestZone >= 0)
                            {
                                CombatHelpers.IsCurrentlyGoldSniping = true;
                                CombatManager.DoZone(_furthestZone);
                                return;
                            }
                            // No fightable zone right now — fall through to normal routing (ITOPOD)
                            // instead of parking in the Safe Zone.
                        }
                    }
                }

                if (Settings.ManageTitans && LockManager.HasTitanLock())
                {
                    int? titanZone = ZoneHelpers.GetHighestSpawningTitanZone();
                    if (titanZone.HasValue && !ZoneHelpers.AutokillAvailable(Array.IndexOf(ZoneHelpers.TitanZones, titanZone.Value)))
                    {
                        CombatHelpers.IsCurrentlyFightingTitan = true;
                        CombatManager.DoZone(titanZone.Value);
                        return;
                    }
                }

                int questZone = QuestManager.IsQuesting();
                if (questZone >= 0)
                {
                    CombatHelpers.IsCurrentlyQuesting = true;
                    CombatManager.DoZone(questZone);
                    return;
                }

                if (Settings.GoldCBlockMode)
                {
                    if (!Character.buttons.brokenTimeMachine.interactable || Character.challenges.timeMachineChallenge.inChallenge)
                    {
                        UpdateFurthestZone();
                        if (_furthestZone >= 0)
                        {
                            CombatHelpers.IsCurrentlyAdventuring = true; // Not equipping gold loadout
                            CombatManager.DoZone(_furthestZone);
                            return;
                        }
                        // Nothing fightable yet (fresh challenge rebirth, moves locked) — fall
                        // through to normal routing (ITOPOD) until stats support an idle zone.
                    }
                }

                if (!Settings.CombatEnabled)
                    return;

                int tempZone = Settings.AdventureTargetITOPOD ? 1000 : Settings.SnipeZone;
                if (tempZone < 1000 && !CombatManager.IsZoneUnlocked(Settings.SnipeZone))
                    tempZone = Settings.AllowZoneFallback ? ZoneHelpers.GetMaxReachableZone(false) : 1000;

                // No Time Machine (locked early / TM challenge) and headed to the ITOPOD: ITOPOD enemies
                // drop NO gold, and without gold the Augments that drive Power/Toughness stall. While we
                // can't afford two of the cheapest augment upgrade, farm the best clearable gold zone
                // instead; the moment gold recovers, normal ITOPOD routing resumes.
                bool tmUnavailable = !Character.buttons.brokenTimeMachine.interactable
                    || Character.challenges.timeMachineChallenge.inChallenge;
                if (tempZone >= 1000 && tmUnavailable && OptimizationAdvisor.GoldStarvedForAugs(Character, 2.0))
                {
                    UpdateFurthestZone();
                    if (_furthestZone >= 0)
                    {
                        CombatHelpers.IsCurrentlyAdventuring = true;
                        CombatManager.DoZone(_furthestZone);
                        return;
                    }
                }

                CombatHelpers.IsCurrentlyAdventuring = true;
                CombatManager.DoZone(tempZone);

                if (tempZone >= 1000)
                    ITOPODManager.Update();
            }
            catch (Exception e)
            {
                LogDebug(e.Message);
                LogDebug(e.StackTrace);
            }
        }

        private void DumpEquipped()
        {
            var list = new List<int>
            {
                Character.inventory.head.id,
                Character.inventory.chest.id,
                Character.inventory.legs.id,
                Character.inventory.boots.id,
                Character.inventory.weapon.id
            };

            if (InventoryController.weapon2Unlocked())
                list.Add(Character.inventory.weapon2.id);

            foreach (var acc in Character.inventory.accs)
                list.Add(acc.id);

            list.RemoveAll(x => x == 0);
            var items = $"[{string.Join(", ", list)}]";

            Log($"Equipped Items: {items}");
            Clipboard.SetText(items);
        }

        public void OnGUI()
        {
            if (Settings.DisableOverlay)
                return;
            float scale = UnityEngine.Screen.height / 900f;
            float offset = 10f * scale;
            float width = 200f * scale;
            float height = 40f * scale;
            // Cache the GUIStyle instead of allocating one on every OnGUI event (fires multiple times per frame)
            if (_overlayStyle == null || _overlayStyleScale != scale)
            {
                _overlayStyle = new GUIStyle("label")
                {
                    fontSize = Mathf.CeilToInt(10 * scale)
                };
                _overlayStyleScale = scale;
            }
            var style = _overlayStyle;
            GUI.Label(new Rect(offset, 0 * offset, width, height), $"Automation - {(Settings.GlobalEnabled ? "Active" : "Inactive")}", style);
            GUI.Label(new Rect(offset, 1 * offset, width, height), $"Next Loop - {_timeLeft:00.0}s", style);
            GUI.Label(new Rect(offset, 2 * offset, width, height), $"Profile - {Settings.AllocationFile}", style);
            GUI.Label(new Rect(offset, 3 * offset, width, height), $"Action - {LockManager.GetLockTypeName()}", style);
            var prog = Managers.ProgressionAnalyzer.Detect();
            GUI.Label(new Rect(offset, 4 * offset, width * 1.5f, height), $"Stage - {prog.Label}", style);
            if (prog.Known)
                GUI.Label(new Rect(offset, 5 * offset, width * 2f, height), $"Goal - {prog.NextGoal}", style);
        }

        public void MonitorLog()
        {
            var bLog = Character.adventureController.log;
            var log = bLog.GetFieldValue<PlayerLog, List<string>>("Eventlog");
            for (var i = log.Count - 1; i >= 0; i--)
            {
                var line = log[i];
                if (!line.Contains("dropped")) continue;
                if (line.Contains("gold")) continue;
                var lower = line.ToLower();
                if (lower.Contains("special boost")) continue;
                if (lower.Contains("toughness boost")) continue;
                if (lower.Contains("power boost")) continue;
                if (line.EndsWith("<b></b>")) continue;
                var result = line;
                if (result.Contains("\n"))
                    result = result.Split('\n').Last();

                var sb = new StringBuilder(result);
                sb.Replace("<color=blue>", "");
                sb.Replace("<b>", "");
                sb.Replace("</color>", "");
                sb.Replace("</b>", "");

                LogLoot(sb.ToString());
                log[i] = $"{line}<b></b>";
            }
        }

        // Keep the furthest-zone ratchet honest: ratchet UP to the best currently-clearable zone, but
        // when a rebirth crashed our stats (challenge chains) and the ratcheted zone is no longer
        // fightable AT ALL, drop back to the best idle-able zone — the old behavior kept sending us
        // to the stale high zone, and CombatManager parked in the Safe Zone forever.
        private static void UpdateFurthestZone()
        {
            int before = _furthestZone;
            var best = ZoneStatHelper.GetBestZone();
            if (best == null)
            {
                if (_furthestZone > 0 && ZoneStatHelper.ZoneFightType(_furthestZone) == 0)
                    _furthestZone = -1;
                return;
            }
            if (best.Zone > _furthestZone)
                _furthestZone = best.Zone;
            else if (_furthestZone > best.Zone && ZoneStatHelper.ZoneFightType(_furthestZone) == 0)
            {
                Log($"Gold zone {_furthestZone} is no longer fightable after rebirth; dropping back to {best.Zone}.");
                _furthestZone = best.Zone;
            }
            if (_furthestZone != before)
                AdviseZoneDropChance(_furthestZone);
        }

        // RvL-style drop-chance advice, once per zone change: how much total drop chance the new farm
        // zone wants before its regular drops are capped (from the game's own loot tables), vs what we
        // have now (Character.lootFactor is the exact multiplier the drop rolls use).
        private static int _lastDcAdviceZone = -1;

        private static void AdviseZoneDropChance(int zone)
        {
            if (zone < 0 || zone == _lastDcAdviceZone) return;
            _lastDcAdviceZone = zone;
            try
            {
                if (!ZoneStatHelper.RecommendedDcPercent.TryGetValue(zone, out var recPct)) return;
                double curPct = Character.lootFactor() * 100.0;
                string name = ZoneHelpers.ZoneList.TryGetValue(zone, out var n) ? n : $"Zone {zone}";
                if (curPct < recPct)
                    Log($"{name}: recommend >= {FmtPct(recPct)} total drop chance to cap its regular drops (currently {FmtPct(curPct)}).");
            }
            catch (Exception e) { LogDebug($"DC advice: {e.Message}"); }
        }

        private static string FmtPct(double v)
        {
            string[] suf = { "%", "K%", "M%", "B%" };
            int i = 0;
            while (v >= 1000 && i < suf.Length - 1) { v /= 1000; i++; }
            return v >= 100 ? $"{v:0}{suf[i]}" : $"{v:0.#}{suf[i]}";
        }

        public static void ResetFurthestZone() => _furthestZone = -1;

        // S3 trigger engine: which event fired last (Gold pipeline's snipe stage shows it).
        public static string LastSnipeTrigger = "";
        public static int FurthestZone => _furthestZone;

        public void SetResnipe()
        {
            bool advisor = Settings.AdvisorGold;
            string trigger = null;

            // New zone fightable: previously CBlock-only — now a first-class trigger everywhere.
            if (advisor || Settings.SnipeOnNewZone || Settings.GoldCBlockMode)
            {
                var best = ZoneStatHelper.GetBestZone();
                // Reload seeding: the baseline is a static and comes back unknown (-1) after an
                // advisor reload. With the snipe already complete, adopt the current best zone
                // silently instead of firing — genuinely new unlocks still trigger from here on.
                if (best != null && _furthestZone < 0 && Settings.GoldSnipeComplete)
                    _furthestZone = best.Zone;
                else if (best != null && _furthestZone >= 0 && best.Zone > _furthestZone)
                    trigger = "new zone fightable";
            }

            // Timer: manual-only (advisor is event-driven); fires once at ResnipeTime into the run.
            if (trigger == null && !advisor && Settings.SnipeOnTimer && Settings.ResnipeTime > 0
                && Math.Abs(Character.rebirthTime.totalseconds - Settings.ResnipeTime) < 1)
                trigger = "timer";

            if (trigger != null && Settings.GoldSnipeComplete)
            {
                Settings.GoldSnipeComplete = false;
                LastSnipeTrigger = trigger;
                Log($"Re-snipe: {trigger}");
            }
        }

        public void ShowBoostProgress()
        {
            ih[] boostSlots = InventoryManager.GetBoostSlots(Character.inventory.GetConvertedInventory().ToArray());
            try
            {
                InventoryManager.ShowBoostProgress(boostSlots);
            }
            catch (Exception e)
            {
                LogDebug(e.Message);
                LogDebug(e.StackTrace);
            }
        }

        public void OnApplicationQuit() => Loader.Unload();

        public static void ResetBoostProgress()
        {
            Log($"Resetting Boost Average");
            InventoryManager.Reset();
        }
    }
}
