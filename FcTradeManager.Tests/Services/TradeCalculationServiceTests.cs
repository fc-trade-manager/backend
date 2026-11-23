using FcTradeManager.Domain.Entities;
using FcTradeManager.Domain.Enums;
using FcTradeManager.Infrastructure.Services;
using FcTradeManager.Tests.Support;

namespace FcTradeManager.Tests.Services;

public class TradeCalculationServiceTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid CardA = Guid.NewGuid();
    private static readonly Guid OwnerId = Guid.NewGuid();

    private static Trade T(TradeType type, int price, int qty, int minutes, Guid? card = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = OwnerId,
        CardId = card ?? CardA,
        Type = type,
        Price = price,
        Quantity = qty,
        ExecutedAt = T0.AddMinutes(minutes)
    };

    // PROFIT

    [Fact]
    public void RN02_profit_uses_the_average_buy_price_and_the_5_percent_fee()
    {
        var buy1 = T(TradeType.Buy, 1000, 2, 0);
        var buy2 = T(TradeType.Buy, 1300, 1, 1);
        var sell = T(TradeType.Sell, 2000, 1, 2);

        var profit = TradeCalculationService.CalculateProfit([buy1, buy2, sell], sell);

        // average = (1000*2 + 1300) / 3 = 1100; net = 2000 * 0.95 = 1900; (1900 - 1100) * 1
        Assert.Equal(800m, profit);
    }

    [Fact]
    public void RN01_net_sell_price_is_95_percent_of_the_price()
    {
        var buy = T(TradeType.Buy, 100, 1, 0);
        var sell = T(TradeType.Sell, 1000, 1, 1);

        var profit = TradeCalculationService.CalculateProfit([buy, sell], sell);

        Assert.Equal(850m, profit); // 950 - 100
    }

    [Fact]
    public void RN02_profit_is_multiplied_by_the_sold_quantity()
    {
        var buy = T(TradeType.Buy, 500, 10, 0);
        var sell = T(TradeType.Sell, 1000, 10, 1);

        var profit = TradeCalculationService.CalculateProfit([buy, sell], sell);

        Assert.Equal(4500m, profit); // (950 - 500) * 10
    }

    [Fact]
    public void RN02_profit_can_be_negative()
    {
        var buy = T(TradeType.Buy, 1000, 1, 0);
        var sell = T(TradeType.Sell, 1000, 1, 1);

        var profit = TradeCalculationService.CalculateProfit([buy, sell], sell);

        Assert.Equal(-50m, profit); // 950 - 1000
    }

    [Fact]
    public void RN02_buys_made_after_the_sale_are_ignored()
    {
        var buy = T(TradeType.Buy, 1000, 1, 0);
        var sell = T(TradeType.Sell, 2000, 1, 5);
        var laterBuy = T(TradeType.Buy, 5000, 1, 10);

        var profit = TradeCalculationService.CalculateProfit([buy, sell, laterBuy], sell);

        Assert.Equal(900m, profit); // 1900 - 1000
    }

    [Fact]
    public void RN02_buys_of_other_cards_are_ignored()
    {
        var other = T(TradeType.Buy, 9000, 5, 0, card: Guid.NewGuid());
        var buy = T(TradeType.Buy, 1000, 1, 1);
        var sell = T(TradeType.Sell, 2000, 1, 2);

        var profit = TradeCalculationService.CalculateProfit([other, buy, sell], sell);

        Assert.Equal(900m, profit);
    }

    [Fact]
    public void RN02_earlier_sales_do_not_change_the_average()
    {
        // the average is over all buys up to the sale, not reduced by previous sales
        var buy1 = T(TradeType.Buy, 1000, 2, 0);
        var buy2 = T(TradeType.Buy, 1300, 1, 1);
        var sell1 = T(TradeType.Sell, 2000, 1, 2);
        var sell2 = T(TradeType.Sell, 1000, 1, 3);
        var all = new[] { buy1, buy2, sell1, sell2 };

        Assert.Equal(800m, TradeCalculationService.CalculateProfit(all, sell1));
        Assert.Equal(-150m, TradeCalculationService.CalculateProfit(all, sell2)); // 950 - 1100
    }

    [Fact]
    public void RN02_a_buy_has_no_profit()
    {
        var buy = T(TradeType.Buy, 1000, 1, 0);

        Assert.Equal(0m, TradeCalculationService.CalculateProfit([buy], buy));
    }

    [Fact]
    public void RN02_sale_without_previous_buys_treats_the_cost_as_zero()
    {
        var sell = T(TradeType.Sell, 1000, 2, 0);

        Assert.Equal(1900m, TradeCalculationService.CalculateProfit([sell], sell));
    }

    [Fact]
    public void RN04_selling_what_you_have_is_valid()
    {
        var trades = new[] { T(TradeType.Buy, 1000, 2, 0), T(TradeType.Sell, 2000, 2, 1) };

        TradeCalculationService.EnsureNonNegativeBalance(trades); // does not throw
    }

    [Fact]
    public void RN04_selling_more_than_you_bought_is_rejected()
    {
        var trades = new[] { T(TradeType.Buy, 1000, 2, 0), T(TradeType.Sell, 2000, 3, 1) };

        Assert.Throws<InvalidOperationException>(() => TradeCalculationService.EnsureNonNegativeBalance(trades));
    }

    [Fact]
    public void RN04_at_the_same_instant_buys_are_counted_before_sells()
    {
        var trades = new[] { T(TradeType.Sell, 2000, 1, 0), T(TradeType.Buy, 1000, 1, 0) };

        TradeCalculationService.EnsureNonNegativeBalance(trades); // does not throw
    }

    [Fact]
    public void RN04_no_trades_is_valid()
    {
        TradeCalculationService.EnsureNonNegativeBalance([]);
    }

    // VALIDATION AFTER CHANGE

    private sealed class Ctx : IDisposable
    {
        public TestDb Db { get; } = new();
        public User U { get; }
        public Card A { get; }
        public Card B { get; }
        public TradeCalculationService Svc { get; }

        public Ctx()
        {
            U = Seed.AddUser(Db.Context);
            A = Seed.AddCard(Db.Context, "Card A");
            B = Seed.AddCard(Db.Context, "Card B");
            Svc = new TradeCalculationService(Db.Context);
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task RN04_adding_a_valid_sale_passes()
    {
        using var c = new Ctx();
        Seed.AddTrade(c.Db.Context, c.U.Id, c.A.Id, TradeType.Buy, 1000, 2, T0);
        var sale = new Trade { Id = Guid.NewGuid(), UserId = c.U.Id, CardId = c.A.Id, Type = TradeType.Sell, Price = 2000, Quantity = 2, ExecutedAt = T0.AddHours(1) };

        await c.Svc.ValidateAfterChangeAsync(c.U.Id, [c.A.Id], removed: null, added: sale);
    }

    [Fact]
    public async Task RN04_deleting_a_buy_that_a_later_sale_depends_on_is_rejected()
    {
        using var c = new Ctx();
        var buy = Seed.AddTrade(c.Db.Context, c.U.Id, c.A.Id, TradeType.Buy, 1000, 2, T0);
        Seed.AddTrade(c.Db.Context, c.U.Id, c.A.Id, TradeType.Sell, 2000, 2, T0.AddHours(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Svc.ValidateAfterChangeAsync(c.U.Id, [c.A.Id], removed: buy, added: null));
    }

    [Fact]
    public async Task RN04_deleting_a_sale_is_always_fine()
    {
        using var c = new Ctx();
        Seed.AddTrade(c.Db.Context, c.U.Id, c.A.Id, TradeType.Buy, 1000, 2, T0);
        var sale = Seed.AddTrade(c.Db.Context, c.U.Id, c.A.Id, TradeType.Sell, 2000, 2, T0.AddHours(1));

        await c.Svc.ValidateAfterChangeAsync(c.U.Id, [c.A.Id], removed: sale, added: null);
    }

    [Fact]
    public async Task RN04_reducing_a_buy_below_the_sold_quantity_is_rejected()
    {
        using var c = new Ctx();
        var buy = Seed.AddTrade(c.Db.Context, c.U.Id, c.A.Id, TradeType.Buy, 1000, 3, T0);
        Seed.AddTrade(c.Db.Context, c.U.Id, c.A.Id, TradeType.Sell, 2000, 3, T0.AddHours(1));
        var edited = new Trade { Id = buy.Id, UserId = c.U.Id, CardId = c.A.Id, Type = TradeType.Buy, Price = 1000, Quantity = 2, ExecutedAt = buy.ExecutedAt };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Svc.ValidateAfterChangeAsync(c.U.Id, [c.A.Id], removed: buy, added: edited));
    }

    [Fact]
    public async Task RN04_only_the_users_own_trades_are_considered()
    {
        using var c = new Ctx();
        var other = Seed.AddUser(c.Db.Context, "other@test.com");
        Seed.AddTrade(c.Db.Context, other.Id, c.A.Id, TradeType.Buy, 1000, 10, T0); // someone else's stock
        var sale = new Trade { Id = Guid.NewGuid(), UserId = c.U.Id, CardId = c.A.Id, Type = TradeType.Sell, Price = 2000, Quantity = 1, ExecutedAt = T0.AddHours(1) };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Svc.ValidateAfterChangeAsync(c.U.Id, [c.A.Id], removed: null, added: sale));
    }
}
