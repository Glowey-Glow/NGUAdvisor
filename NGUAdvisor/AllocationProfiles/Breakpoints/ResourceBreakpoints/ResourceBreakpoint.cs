using SimpleJSON;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    public enum ResourceType
    {
        Energy,
        Magic,
        R3,
        Consumable
    }

    public abstract class ResourceBreakpoint
    {
        // PROPERTY, not `static readonly ... = Main.Character`. The field form ran Main.Character —
        // and therefore FindObjectOfType<Character>() — inside this type's INITIALIZER, so merely
        // NAMING ResourceBreakpoint (or any subclass) required a live Unity scene. Two consequences it
        // cost us: nothing in this hierarchy could be linked into the headless net9.0 test project, and
        // a throw in that initializer poisons the type for the whole process lifetime (audit 01 §R4).
        // As a property the Unity read happens at USE, not at type-init, so the type loads anywhere and
        // a failed read is a local fault. The read itself is unchanged — Main.Character is still the
        // one cached root, so this is not a per-access FindObjectOfType.
        protected static Character _character => Main.Character;

        private double? CapPercent { get; set; }

        protected int Index { get; private set; }

        protected ResourceType Type { get; private set; }

        public bool IsCap { get; private set; }

        protected long MaxAllocation { get; private set; }

        private long GetIdleResourceAmount()
        {
            switch (Type)
            {
                case ResourceType.Energy:
                    return _character.idleEnergy;
                case ResourceType.Magic:
                    return _character.magic.idleMagic;
                case ResourceType.R3:
                    return _character.res3.idleRes3;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private long GetMaxResourceAmount()
        {
            if (!IsCap)
                return GetIdleResourceAmount();

            switch (Type)
            {
                case ResourceType.Energy:
                    return _character.curEnergy;
                case ResourceType.Magic:
                    return _character.magic.curMagic;
                case ResourceType.R3:
                    return _character.res3.curRes3;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public void UpdateMaxAllocation(int prioCount = 1)
        {
            long capMax = GetMaxResourceAmount();
            if (CapPercent.HasValue)
                capMax = (long)Math.Ceiling(capMax * CapPercent.Value);
            else if (!IsCap)
                capMax = capMax / prioCount + Math.Sign(capMax % prioCount);
            MaxAllocation = Math.Min(capMax, GetIdleResourceAmount());
        }

        // ---- Diagnostic surface (read-only; see AllocDiagnostic) ----
        // Index/MaxAllocation are protected because nothing outside the lane should STEER on them. The
        // energy/magic telemetry still has to NAME a lane and report the budget it resolved to, so those
        // two facts get a public read-only view rather than loosening the setters.

        // Mirrors the profile token syntax, so a log line can be pasted back into a profile: "CAPNGU-5".
        public string Label => Managers.LaneTargets.Label(GetType().Name, IsCap, Index);

        // What UpdateMaxAllocation resolved to. The gap between this and what the lane actually committed
        // is the stair-snap, which is CORRECT: every system caps at one level per game tick (decomp
        // NGUController.updateNGU zeroes progress on level-up and discards the overflow), so a lane that
        // takes less than its budget is taking everything it can use. Only the trailing idle is waste.
        public long Budget => MaxAllocation;

        // ---- Constraint-layer wiring surface (ConstraintLayerBridge) ----
        // The new path never calls UpdateMaxAllocation: the constraint layer decides each lane's
        // budget (spec §2's fill) and hands it in here; Allocate() then self-limits below it exactly
        // as before — the lane's own stair-snap math IS the Pass 2 capacity (CapacityPass.Table's
        // Advisor rows). Index/Type/CapPercent get read-only views because the bridge must NAME a
        // lane family and, for parity logging, replay the old share arithmetic — nothing outside the
        // lane steers on them.
        public void OfferBudget(long amount) => MaxAllocation = amount > 0 ? amount : 0;

        public int LaneIndex => Index;

        public ResourceType LaneType => Type;

        public double? CapPercentView => CapPercent;

        // Managers/LaneTargets holds the composition and the per-lane TargetMet() inventory. FIVE of the
        // ten lanes hardcode TargetMet() => false, so for them this reduces to "correct pool AND
        // unlocked" and they can never leave the priority list by being satisfied — decision record
        // amendment 01 §P1 is the fix for that, and it is deliberately NOT applied here.
        public bool IsValid() => Managers.LaneTargets.IsValid(CorrectResourceType(), Unlocked(), TargetMet());

        protected abstract bool Unlocked();

        protected abstract bool TargetMet();

        public abstract bool Allocate();

        protected abstract bool CorrectResourceType();

        protected void SetInput(long val)
        {
            _character.energyMagicPanel.energyRequested.text = val.ToString();
            _character.energyMagicPanel.validateInput();
        }

        // No rebirthTime parameter: the rebirth deadline is a property of the RUN, not of a parsed
        // breakpoint. It was never passed by any caller (BR and BestAug both read 0 and their
        // "will it finish before the rebirth" guards silently never fired); both now read it live
        // from Main.Profile.NextRebirthTargetSeconds().
        public static IEnumerable<ResourceBreakpoint> ParseBreakpointArray(JSONNode node, ResourceType type)
        {
            var prios = node.AsArray.Children.Select(x => x.Value.ToUpper());
            foreach (var prio in prios)
            {
                double? cap;
                int index;
                var temp = prio;
                if (temp.Contains(":"))
                {
                    var split = prio.Split(':');
                    temp = split[0];
                    // SKIP, do not default to 100. A malformed percent used to mean "take the entire
                    // pool" — the worst possible reading of a typo, and indistinguishable from an
                    // intentional CAPX:100. Same principle as the index sentinel below.
                    if (!int.TryParse(split[1], out var tempCap))
                        continue;
                    if (tempCap > 100)
                        cap = 100;
                    else if (tempCap < 0)
                        cap = 0;
                    else
                        cap = tempCap;
                    cap /= 100;
                }
                else
                {
                    cap = null;
                }


                if (temp.Contains("-"))
                {
                    var split = temp.Split('-');
                    temp = split[0];
                    // SKIP the whole token rather than emitting a poisoned breakpoint. This used to
                    // set index = -1, which every lane then had to guard against independently —
                    // and six of them did not, or did it wrong. -1 travels into eleven `Index = index`
                    // construction sites and then indexes a game array: ngus[-1], bloodMagics[-1],
                    // ControllerFor(-1). Some throw and kill the lane for a profile lifetime; AUG-x
                    // did not even throw (-1 % 2 == -1 in C#, so it took the upgrade branch and
                    // reported unlocked), silently inflating the prioCount divisor for every other
                    // lane in the pool — the same mechanism as the RIT-7 defect.
                    //
                    // Setting index = 0 would be worse: RIT-x would silently become RIT-0, a valid
                    // fundable lane the user never asked for. A malformed token must produce nothing.
                    // (audit 09 §5; advisors/02:718; decision record amendment 07)
                    if (!int.TryParse(split[1], out index))
                        continue;
                }
                else
                {
                    index = 0;
                }

                if (temp.StartsWith("NGU") || temp.StartsWith("CAPNGU"))
                {
                    yield return new NGUBP
                    {
                        CapPercent = cap,
                        Index = index,
                        IsCap = temp.Contains("CAP"),
                        Type = type
                    };
                }
                else if (temp.Contains("ALLNGU"))
                {
                    var top = 0;
                    if (type == ResourceType.Energy)
                        top = 9;
                    else if (type == ResourceType.Magic)
                        top = 7;
                    for (var i = 0; i < top; i++)
                    {
                        yield return new NGUBP
                        {
                            CapPercent = cap,
                            Index = i,
                            IsCap = temp.Contains("CAP"),
                            Type = type
                        };
                    }
                }
                else if (temp.StartsWith("CAPAT") || temp.StartsWith("AT"))
                {
                    yield return new AdvancedTrainingBP
                    {
                        CapPercent = cap,
                        Index = index,
                        IsCap = temp.Contains("CAP"),
                        Type = type
                    };
                }
                else if (temp.StartsWith("AUG") || temp.StartsWith("CAPAUG"))
                {
                    yield return new AugmentBP
                    {
                        CapPercent = cap,
                        Index = index,
                        IsCap = temp.Contains("CAP"),
                        Type = type
                    };
                }
                else if (temp.StartsWith("BESTAUG") || temp.StartsWith("CAPBESTAUG"))
                {
                    yield return new BestAug
                    {
                        CapPercent = cap,
                        Index = index,
                        IsCap = temp.Contains("CAP"),
                        Type = type
                    };
                }
                else if (temp.StartsWith("BT") || temp.StartsWith("CAPBT"))
                {
                    yield return new BasicTrainingBP
                    {
                        CapPercent = cap,
                        Index = index,
                        IsCap = temp.Contains("CAP"),
                        Type = type
                    };
                }
                else if (temp.StartsWith("HACK") || temp.StartsWith("CAPHACK"))
                {
                    yield return new HackBP
                    {
                        CapPercent = cap,
                        Index = index,
                        IsCap = temp.Contains("CAP"),
                        Type = type
                    };
                }
                else if (temp.StartsWith("WAN") || temp.StartsWith("CAPWAN"))
                {
                    yield return new WandoosBP
                    {
                        CapPercent = cap,
                        Index = index,
                        IsCap = temp.Contains("CAP"),
                        Type = type
                    };
                }
                else if (temp.StartsWith("ALLBT") || temp.StartsWith("CAPALLBT"))
                {
                    var top = _character.settings.syncTraining ? 6 : 12;
                    for (var i = 0; i < top; i++)
                    {
                        yield return new BasicTrainingBP
                        {
                            CapPercent = cap,
                            Index = i,
                            IsCap = temp.Contains("CAP"),
                            Type = type
                        };
                    }
                }
                else if (temp.StartsWith("BR") || temp.StartsWith("CAPBR"))
                {
                    yield return new BR
                    {
                        CapPercent = cap,
                        Index = index,
                        IsCap = prio.Contains("CAP"),
                        Type = type
                    };
                }
                else if (temp.StartsWith("TM") || temp.StartsWith("CAPTM"))
                {
                    yield return new TimeMachineBP
                    {
                        CapPercent = cap,
                        Index = index,
                        IsCap = prio.Contains("CAP"),
                        Type = type
                    };
                }
                else if (temp.StartsWith("RIT") || temp.StartsWith("CAPRIT"))
                {
                    yield return new RitualBP
                    {
                        CapPercent = cap,
                        Index = index,
                        IsCap = prio.Contains("CAP"),
                        Type = type
                    };
                }
                else if (temp.StartsWith("ALLAT") || temp.StartsWith("CAPALLAT"))
                {
                    // GRAMMAR UNCHANGED, one flag fewer: this used to also set GroupSpread = true, the
                    // per-group waterfill AdvancedTrainingBP no longer has. The five slots it yields,
                    // their indices and their order are exactly as before — see the header comment on
                    // AdvancedTrainingBP for why the local waterfill went.
                    for (var i = 0; i < 5; i++)
                    {
                        yield return new AdvancedTrainingBP
                        {
                            CapPercent = cap,
                            Index = i,
                            IsCap = temp.Contains("CAP"),
                            Type = type
                        };
                    }
                }
                else if (temp.StartsWith("ALLHACK") || temp.StartsWith("CAPALLHACK"))
                {
                    // Plain 0..14, and let IsValid() sort them out at allocation time.
                    //
                    // This used to read live hack state HERE — targeted hacks first, then untargeted — but
                    // parsing happens once when the profile loads. The resulting order was therefore frozen
                    // at load: a target set later never reordered anything, and a hack that reached its
                    // target kept its slot until the profile was reloaded. ChallengeOverlay made it worse by
                    // caching the parsed list for the whole session.
                    //
                    // The partition also had a hole. It bucketed `target > 0` and `target == 0`, so hacks at
                    // target == -1 — the game's own "never fund this" marker — matched neither and vanished
                    // from ALLHACK entirely. hitTarget already returns true for -1, so letting IsValid()
                    // filter is both simpler and correct.
                    //
                    // Index 15 ("THE END") is excluded outright rather than emitted and filtered later: it
                    // takes no R3, and including it inflated the priority count the share math divides by.
                    for (var i = 0; i <= 14; i++)
                    {
                        yield return new HackBP
                        {
                            CapPercent = cap,
                            Index = i,
                            IsCap = temp.Contains("CAP"),
                            Type = type
                        };
                    }
                }
                else
                {
                    yield return null;
                }
            }
        }
    }
}
