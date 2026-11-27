using FcTradeManager.Domain.Enums;
using FcTradeManager.Domain.Rules;
using FcTradeManager.Infrastructure.Services;
using FcTradeManager.Tests.Support;

namespace FcTradeManager.Tests.Services;

// RN10 on the read side: the chart never shows two prices for the same minute when one of them is manual
public class PriceHistoryManualPrevailsTests
{
    private static DateTime ThisHour() => PriceSimulationRules.FloorToSlot(DateTime.UtcNow);

    [Fact]
    public async Task RN10_history_hides_the_simulated_price_when_a_manual_one_exists_at_the_same_minute()
    {
        using var db = new TestDb();
        var card = Seed.AddCard(db.Context);
        var t = ThisHour().AddHours(-3);
        Seed.AddSnapshot(db.Context, card.Id, 20_000, t, PriceSource.Simulated);
        Seed.AddSnapshot(db.Context, card.Id, 900, t.AddSeconds(30), PriceSource.Manual);
        Seed.AddSnapshot(db.Context, card.Id, 21_000, t.AddHours(1), PriceSource.Simulated);

        var history = (await new PriceService(db.Context).GetPriceHistoryAsync(card.Id, "24h")).ToList();

        Assert.Equal(2, history.Count);
        Assert.Equal(PriceSource.Manual, history[0].Source);
        Assert.Equal(900, history[0].Price);
        Assert.Equal(21_000, history[1].Price);
    }

    [Fact]
    public async Task History_keeps_a_simulated_price_when_the_manual_one_is_in_another_minute()
    {
        using var db = new TestDb();
        var card = Seed.AddCard(db.Context);
        var t = ThisHour().AddHours(-3);
        Seed.AddSnapshot(db.Context, card.Id, 20_000, t, PriceSource.Simulated);
        Seed.AddSnapshot(db.Context, card.Id, 900, t.AddMinutes(1), PriceSource.Manual);

        var history = (await new PriceService(db.Context).GetPriceHistoryAsync(card.Id, "24h")).ToList();

        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task History_keeps_every_manual_price_even_in_the_same_minute()
    {
        using var db = new TestDb();
        var card = Seed.AddCard(db.Context);
        var t = ThisHour().AddHours(-3);
        Seed.AddSnapshot(db.Context, card.Id, 900, t, PriceSource.Manual);
        Seed.AddSnapshot(db.Context, card.Id, 950, t.AddSeconds(20), PriceSource.Manual);

        var history = (await new PriceService(db.Context).GetPriceHistoryAsync(card.Id, "24h")).ToList();

        Assert.Equal(2, history.Count);
    }
}
