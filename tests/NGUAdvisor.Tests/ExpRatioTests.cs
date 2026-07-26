using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Headless guard for the difficulty/phase EXP split (audit M5). Locks the guide's ratio matrix —
    // in particular the Evil ch.5 rule the balancer was missing: energy-only pre-T7 but ONLY once the
    // Ygg/EXP magic NGUs are capped, base 3:1 while still building them, and base 3:1 post-T7 — versus
    // the Normal-only magic skew. Guards against silently drifting back to base 3:1 on Evil, and against
    // going energy-only too early (the first fix's mistake).
    public class ExpRatioTests
    {
        private static (double e, double m) Split(bool evil, bool t7, bool capped, bool normalSkew)
        {
            ExpRatio.Split(evil, t7, capped, normalSkew, out double pe, out double pm);
            return (pe, pm);
        }

        [Fact]
        public void Base_split_is_even()
            => Assert.Equal((0.5, 0.5), Split(evil: false, t7: false, capped: false, normalSkew: false));

        [Fact]
        public void Normal_D4_skews_toward_magic()
            => Assert.Equal((0.4, 0.6), Split(evil: false, t7: false, capped: false, normalSkew: true));

        [Fact]
        public void Evil_pre_T7_before_magic_ngus_cap_stays_base()
            => Assert.Equal((0.5, 0.5), Split(evil: true, t7: false, capped: false, normalSkew: false));

        [Fact]
        public void Evil_pre_T7_once_magic_ngus_capped_is_energy_only()
            => Assert.Equal((1.0, 0.0), Split(evil: true, t7: false, capped: true, normalSkew: false));

        [Fact]
        public void Evil_post_T7_returns_to_base_3to1_regardless_of_cap()
        {
            Assert.Equal((0.5, 0.5), Split(evil: true, t7: true, capped: true, normalSkew: false));
            Assert.Equal((0.5, 0.5), Split(evil: true, t7: true, capped: false, normalSkew: false));
        }

        [Fact]
        public void Evil_ignores_the_normal_magic_skew()
        {
            Assert.Equal((1.0, 0.0), Split(evil: true, t7: false, capped: true, normalSkew: true));
            Assert.Equal((0.5, 0.5), Split(evil: true, t7: false, capped: false, normalSkew: true));
        }
    }
}
