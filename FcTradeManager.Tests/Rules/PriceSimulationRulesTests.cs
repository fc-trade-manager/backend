using FcTradeManager.Domain.Rules;

namespace FcTradeManager.Tests.Rules;

// the pure model behind the simulated prices (same model as the seed script)
public class PriceSimulationRulesTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0, int s = 0) =>
        new(y, m, d, h, min, s, DateTimeKind.Utc);

    [Fact]
    public void FloorToSlot_returns_the_start_of_the_hour()
    {
        Assert.Equal(Utc(2026, 11, 25, 22), PriceSimulationRules.FloorToSlot(Utc(2026, 11, 25, 22, 47, 13)));
        Assert.Equal(Utc(2026, 11, 25, 22), PriceSimulationRules.FloorToSlot(Utc(2026, 11, 25, 22)));
    }

    [Fact]
    public void NextSlot_is_always_the_following_full_hour()
    {
        Assert.Equal(Utc(2026, 11, 25, 23), PriceSimulationRules.NextSlot(Utc(2026, 11, 25, 22)));
        Assert.Equal(Utc(2026, 11, 26, 0), PriceSimulationRules.NextSlot(Utc(2026, 11, 25, 23, 30)));
    }

    [Fact]
    public void SlotsBetween_includes_both_ends_and_is_empty_when_there_is_nothing_to_fill()
    {
        var slots = PriceSimulationRules.SlotsBetween(Utc(2026, 11, 25, 23), Utc(2026, 11, 26, 2));

        Assert.Equal(4, slots.Count);
        Assert.Equal(Utc(2026, 11, 25, 23), slots[0]);
        Assert.Equal(Utc(2026, 11, 26, 2), slots[^1]);
        Assert.Empty(PriceSimulationRules.SlotsBetween(Utc(2026, 11, 26, 3), Utc(2026, 11, 26, 2)));
    }

    [Fact]
    public void MinuteKey_is_equal_inside_the_same_minute_and_different_across_minutes()
    {
        Assert.Equal(
            PriceSimulationRules.MinuteKey(Utc(2026, 11, 25, 20, 0, 0)),
            PriceSimulationRules.MinuteKey(Utc(2026, 11, 25, 20, 0, 59)));
        Assert.NotEqual(
            PriceSimulationRules.MinuteKey(Utc(2026, 11, 25, 20, 0, 59)),
            PriceSimulationRules.MinuteKey(Utc(2026, 11, 25, 20, 1, 0)));
    }

    [Fact]
    public void FridayBoost_peaks_on_friday_midnight()
    {
        Assert.Equal(0.08, PriceSimulationRules.FridayBoost(Utc(2026, 11, 27, 0), overall: 90), 10);
    }

    [Fact]
    public void FridayBoost_is_zero_from_monday_to_thursday()
    {
        Assert.Equal(0.0, PriceSimulationRules.FridayBoost(Utc(2026, 11, 26, 20), overall: 90)); // Thursday
        Assert.Equal(0.0, PriceSimulationRules.FridayBoost(Utc(2026, 12, 1, 12), overall: 90));  // Tuesday
    }

    [Fact]
    public void FridayBoost_only_applies_to_overall_86_or_more_and_can_be_turned_off()
    {
        var friday = Utc(2026, 11, 27, 0);

        Assert.Equal(0.0, PriceSimulationRules.FridayBoost(friday, overall: 85));
        Assert.True(PriceSimulationRules.FridayBoost(friday, overall: 86) > 0);
        Assert.Equal(0.0, PriceSimulationRules.FridayBoost(friday, overall: 95, enabled: false));
    }

    [Fact]
    public void EstimateState_with_no_history_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            PriceSimulationRules.EstimateState(new List<(DateTime, int)>(), overall: 80));
    }

    [Fact]
    public void PriceAt_with_no_noise_and_no_boost_is_the_level_itself()
    {
        Assert.Equal(10_000, PriceSimulationRules.PriceAt(Math.Log(10_000), 0, Utc(2026, 11, 24, 12), overall: 90));
    }
}
