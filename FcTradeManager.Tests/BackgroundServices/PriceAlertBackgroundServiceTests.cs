using FcTradeManager.Domain.Entities;
using FcTradeManager.Domain.Enums;
using FcTradeManager.Infrastructure.BackgroundServices;
using FcTradeManager.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FcTradeManager.Tests.BackgroundServices;

// RN06 + RN07 running against the database, one evaluation cycle at a time
public class PriceAlertBackgroundServiceTests
{
    private static PriceAlertBackgroundService Service() =>
        new(Substitute.For<IServiceProvider>(), NullLogger<PriceAlertBackgroundService>.Instance);

    private sealed class Ctx : IDisposable
    {
        public TestDb Db { get; } = new();
        public User U { get; }
        public Card Card { get; }
        public WatchlistItem Item { get; }
        private int _minutesAgo = 60;

        public Ctx(int buy = 1000, int sell = 2000)
        {
            U = Seed.AddUser(Db.Context);
            Card = Seed.AddCard(Db.Context);
            Item = Seed.AddWatch(Db.Context, U.Id, Card.Id, buy, sell);
        }

        // each new price is more recent than the previous one
        public void Price(int price) =>
            Seed.AddSnapshot(Db.Context, Card.Id, price, DateTime.UtcNow.AddMinutes(-(_minutesAgo--)));

        public Task Run() => Service().EvaluatePricesAndGenerateAlertsAsync(Db.Context, CancellationToken.None);

        public List<Alert> Alerts() => Db.NewContext().Alerts.ToList();

        public WatchlistItem SavedItem() => Db.NewContext().WatchlistItems.Single(w => w.Id == Item.Id);

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task RN06_price_below_the_buy_target_creates_a_buy_alert()
    {
        using var c = new Ctx();
        c.Price(900);

        await c.Run();

        var alert = Assert.Single(c.Alerts());
        Assert.Equal(AlertKind.Buy, alert.Kind);
        Assert.Equal(900, alert.TriggeredPrice);
        Assert.Equal(c.U.Id, alert.UserId);
        Assert.Equal(c.Card.Id, alert.CardId);
        Assert.Null(alert.ReadAt);
        Assert.True(c.SavedItem().BuyAlertTriggered);
        Assert.False(c.SavedItem().SellAlertTriggered);
    }

    [Fact]
    public async Task RN06_price_above_the_sell_target_creates_a_sell_alert()
    {
        using var c = new Ctx();
        c.Price(2100);

        await c.Run();

        var alert = Assert.Single(c.Alerts());
        Assert.Equal(AlertKind.Sell, alert.Kind);
        Assert.Equal(2100, alert.TriggeredPrice);
        Assert.True(c.SavedItem().SellAlertTriggered);
    }

    [Theory]
    [InlineData(1000)] // equal to the buy target
    [InlineData(2000)] // equal to the sell target
    public async Task RN06_prices_equal_to_a_target_fire(int price)
    {
        using var c = new Ctx();
        c.Price(price);

        await c.Run();

        Assert.Single(c.Alerts());
    }

    [Fact]
    public async Task RN06_price_between_the_targets_creates_nothing()
    {
        using var c = new Ctx();
        c.Price(1500);

        await c.Run();

        Assert.Empty(c.Alerts());
    }

    [Fact]
    public async Task A_card_without_prices_creates_nothing()
    {
        using var c = new Ctx();

        await c.Run();

        Assert.Empty(c.Alerts());
    }

    [Fact]
    public async Task No_watchlist_items_is_a_no_op()
    {
        using var db = new TestDb();

        await Service().EvaluatePricesAndGenerateAlertsAsync(db.Context, CancellationToken.None);

        Assert.Empty(db.NewContext().Alerts);
    }

    [Fact]
    public async Task The_latest_price_is_the_one_evaluated()
    {
        using var c = new Ctx();
        c.Price(500);  // old price below the buy target
        c.Price(1500); // latest price is fine

        await c.Run();

        Assert.Empty(c.Alerts());
    }

    [Fact]
    public async Task RN07_running_again_with_the_price_still_below_does_not_duplicate_the_alert()
    {
        using var c = new Ctx();
        c.Price(900);

        await c.Run();
        await c.Run();
        await c.Run();

        Assert.Single(c.Alerts());
    }

    [Fact]
    public async Task RN07_the_alert_fires_again_after_the_price_goes_back_and_crosses_again()
    {
        using var c = new Ctx();

        c.Price(900);
        await c.Run();
        Assert.Single(c.Alerts());

        c.Price(1500); // back above the target: re-arms, no new alert
        await c.Run();
        Assert.Single(c.Alerts());
        Assert.False(c.SavedItem().BuyAlertTriggered);

        c.Price(800); // crosses again
        await c.Run();
        Assert.Equal(2, c.Alerts().Count);
    }

    [Fact]
    public async Task RN07_a_target_reset_by_the_user_lets_the_alert_fire_again()
    {
        using var c = new Ctx();
        c.Price(900);
        await c.Run();

        // the user raises the buy target (the watchlist service clears the flag)
        var item = c.Db.Context.WatchlistItems.Single();
        item.BuyTarget = 950;
        item.BuyAlertTriggered = false;
        c.Db.Context.SaveChanges();
        await c.Run();

        Assert.Equal(2, c.Alerts().Count);
    }

    [Fact]
    public async Task Each_user_gets_their_own_alert_for_the_same_card()
    {
        using var c = new Ctx();
        var other = Seed.AddUser(c.Db.Context, "other@test.com");
        Seed.AddWatch(c.Db.Context, other.Id, c.Card.Id, 1000, 2000);
        c.Price(900);

        await c.Run();

        var alerts = c.Alerts();
        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a.UserId == c.U.Id);
        Assert.Contains(alerts, a => a.UserId == other.Id);
    }

    [Fact]
    public async Task Each_card_is_evaluated_against_its_own_price()
    {
        using var c = new Ctx();
        var second = Seed.AddCard(c.Db.Context, "Second");
        Seed.AddWatch(c.Db.Context, c.U.Id, second.Id, 1000, 2000);
        c.Price(900);                                                        // first card below target
        Seed.AddSnapshot(c.Db.Context, second.Id, 1500, DateTime.UtcNow);   // second card in between

        await c.Run();

        var alert = Assert.Single(c.Alerts());
        Assert.Equal(c.Card.Id, alert.CardId);
    }

    [Fact]
    public async Task Simulated_prices_trigger_alerts_too()
    {
        using var c = new Ctx();
        Seed.AddSnapshot(c.Db.Context, c.Card.Id, 900, DateTime.UtcNow.AddMinutes(-1), PriceSource.Simulated);

        await c.Run();

        Assert.Single(c.Alerts());
    }
}
