using Xunit;

namespace NGUAdvisor.Tests
{
    // ---- SERIALISATION FOR PROCESS-WIDE MUTABLE STATE -------------------------------------------
    //
    // xunit runs each test CLASS in its own collection, and collections run IN PARALLEL. That is
    // correct for the rest of this suite, which is pure: every other test builds its inputs, calls a
    // static function, and asserts on the return value. Nothing else here writes shared state.
    //
    // ZoneRouting is the exception. Its latch is four process-wide statics — ZoneRouting.cs:206-209
    // (_last, _lastIntended, _lastRouted, _lastSpoke) — and three members MUTATE them:
    // ShouldSurface() (:211), Spoke() (:227) and Reset() (:233). ZoneRouting.Last (:229) reads
    // _last back, and two test classes assert on it. Two classes on one static, running in
    // parallel, is a race regardless of what either class does internally.
    //
    // ⚠ A Reset() AT THE START OF A TEST DOES NOT MAKE IT ISOLATED. It clears whatever the last
    // writer left, but it cannot stop the next one writing midway. That is exactly how this was
    // found: ZoneRoutingTests' latch tests (:223, :241, :258, :270, :281, :294) each Reset() on
    // entry and then drive the latch to Cause.Quest without resetting on exit, while
    // ZoneOwnerNoteTests.Producing_the_note_does_not_touch_the_resolver_state holds Cause.Titan
    // across a 96-iteration loop and asserts it afterwards. Interleave the two and the assert reads
    // Quest. It surfaced as ZoneOwnerNoteTests.cs:180 "Expected: Titan, Actual: Quest", and only in
    // the FULL suite — a filtered run has nothing to race against and passes every time.
    //
    // ⚠ ADDING A CLASS THAT TOUCHES ZoneRouting? PUT IT IN THIS COLLECTION. The name describes the
    // STATE, not the test that happened to fail, so membership is decidable by looking at what the
    // class calls: if it touches ShouldSurface, Spoke, Reset or Last, it belongs here.
    //
    // Scope is deliberately minimal — the suite runs in ~9s and serialising unrelated classes would
    // trade a real defect for a slow suite. ZoneRouting is the ONLY process-wide mutable static any
    // test in this project mutates; GearWatch.Reset() (GearWatch.cs:115) and
    // InventoryManager.Reset() (InventoryManager.cs:89) have the same shape but no test touches
    // them, so they are latent, not live. Re-check that before assuming this collection is enough.
    // ⚠ THE NAME IS A const, NOT A LITERAL AT EACH SITE, AND THAT IS LOAD-BEARING. xunit matches
    // collections by string. A typo in one [Collection] attribute does not fail the build and does
    // not fail a test — it silently puts that class in a collection of its own, which is the exact
    // parallelism this file exists to prevent. Referencing one const makes a typo a compile error.
    public static class TestCollections
    {
        public const string ZoneRoutingState = "ZoneRouting static latch";
    }

    [CollectionDefinition(TestCollections.ZoneRoutingState)]
    public class ZoneRoutingStateCollection
    {
        // Marker only — no ICollectionFixture. The classes share no fixture; they share a static,
        // and all this attribute buys is "do not run us at the same time".
    }
}
