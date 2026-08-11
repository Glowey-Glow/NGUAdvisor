using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // "How long would it take to farm all the rares?" — the number behind the offer the advisor shows
    // when Farm Rare Accessories is OFF ([OPERATOR] 2026-08-05: "n rare accessories available for
    // farm, approx time to farm n"). Unity-free, linked into the test project.
    //
    // ⚠ IT IS NOT A SUM. Rares that drop in the SAME zone advance TOGETHER — items 226 and 227 share
    // one roll with Span 2, so standing in Chocolate World farms both at once. Adding their hours
    // would roughly double the quoted cost of that zone and could talk the operator out of a farm
    // that is half the price advertised. The honest figure is the sum over distinct ZONES of the
    // slowest rare in each: what farming them one zone after another actually costs.
    //
    // Sequential, not parallel, because routing farms ONE zone at a time — SnipeZone is a single
    // value (audit/40 §0).
    public static class RareRollup
    {
        // zones[i] / hours[i] describe one eligible rare. Mismatched lengths are treated as the
        // shorter of the two rather than throwing: this feeds a display line, and a malformed input
        // must not take down the zone pass.
        public static double SequentialHours(IList<int> zones, IList<double> hours)
        {
            if (zones == null || hours == null) return 0;
            int n = zones.Count < hours.Count ? zones.Count : hours.Count;

            var worst = new Dictionary<int, double>();
            for (int i = 0; i < n; i++)
            {
                double h = hours[i];
                // An unreachable rare contributes nothing rather than poisoning the total with
                // infinity — the caller has already filtered to ELIGIBLE, so this is belt-and-braces.
                if (double.IsNaN(h) || double.IsInfinity(h) || h < 0) h = 0;
                if (!worst.TryGetValue(zones[i], out var cur) || h > cur) worst[zones[i]] = h;
            }

            double total = 0;
            foreach (var kv in worst) total += kv.Value;
            return total;
        }
    }
}
