namespace FcTradeManager.Domain.Rules;

// these are the pure rules that govern how the price simulation works, without any external dependencies (same as initial gen script)
// TO DO (maybe): discard the script and generate initially w this service
// reminder: price = anchor level + Friday boost + noise that "remembers" yesterday and is pulled back to the center
public static class PriceSimulationRules
{
    public const int MinPrice = 200; // RN08
    public const int MaxPrice = 15_000_000; // RN08

    public const int BoostMinOverall = 86; // only cards with overall >= 86 get the Friday rise
    public const double BoostPeak = 0.08; // +8% on Friday 00:00 UTC
    public const int BoostHours = 72; // falls back to normal on Monday 00:00 UTC

    public const double NoiseMemory = 0.97; // each hour keeps 97% of the previous deviation
    public const double NoiseSigma = 0.006; // size of the random step (log)

    public const int AnchorWindowHours = 168; // the "normal" level is the average of the last 7 days
    public const int MaxBackfillDays = 30; // never fill more than the longest chart period (30d)

    public static readonly TimeSpan SlotLength = TimeSpan.FromHours(1);

    public static DateTime FloorToSlot(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % SlotLength.Ticks, DateTimeKind.Utc);

    public static DateTime NextSlot(DateTime utc) => FloorToSlot(utc).Add(SlotLength);

    // Every slot from <paramref name="first"/> to <paramref name="last"/> (contains both)
    public static List<DateTime> SlotsBetween(DateTime first, DateTime last)
    {
        var slots = new List<DateTime>();
        for (var s = FloorToSlot(first); s <= last; s = s.Add(SlotLength))
            slots.Add(s);
        return slots;
    }

    // RN10: two snapshots are "at the same minute" when they share this key
    public static long MinuteKey(DateTime utc) => utc.Ticks / TimeSpan.TicksPerMinute;

    public static double FridayBoost(DateTime utc, int overall, bool enabled = true)
    {
        if (!enabled || overall < BoostMinOverall) return 0;

        // hours since Friday 00:00 UTC
        var daysSinceFriday = ((int)utc.DayOfWeek - (int)DayOfWeek.Friday + 7) % 7;
        var sinceFriday = daysSinceFriday * 24 + utc.Hour;

        return sinceFriday < BoostHours ? BoostPeak * (1 - (double)sinceFriday / BoostHours) : 0;
    }

    public static int Round(double price)
    {
        var clamped = Math.Clamp(price, MinPrice, MaxPrice);
        var scale = Math.Pow(10, Math.Floor(Math.Log10(clamped)) - 2);
        var rounded = (int)(Math.Round(clamped / scale, MidpointRounding.ToEven) * scale);
        return Math.Clamp(rounded, MinPrice, MaxPrice);
    }

    public static (double Anchor, double Noise) EstimateState(
        IReadOnlyList<(DateTime RecordedAt, int Price)> history, int overall, bool fridayBoost = true)
    {
        if (history.Count == 0)
            throw new ArgumentException("History must have at least one price.", nameof(history));

        var levels = history
            .TakeLast(AnchorWindowHours)
            .Select(h => Math.Log(h.Price) - Math.Log(1 + FridayBoost(h.RecordedAt, overall, fridayBoost)))
            .ToList();

        var anchor = levels.Average();
        return (anchor, levels[^1] - anchor);
    }

    public static double NextNoise(double noise, double standardNormal) =>
        NoiseMemory * noise + NoiseSigma * standardNormal;

    public static int PriceAt(double anchor, double noise, DateTime slot, int overall, bool fridayBoost = true) =>
        Round(Math.Exp(anchor + Math.Log(1 + FridayBoost(slot, overall, fridayBoost)) + noise));
}
