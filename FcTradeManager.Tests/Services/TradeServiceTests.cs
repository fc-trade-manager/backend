using FcTradeManager.Application.DTOs;
using FcTradeManager.Domain.Entities;
using FcTradeManager.Domain.Enums;
using FcTradeManager.Infrastructure.Services;
using FcTradeManager.Tests.Support;

namespace FcTradeManager.Tests.Services;

public class TradeServiceTests
{
    private sealed class Ctx : IDisposable
    {
        public TestDb Db { get; } = new();
        public User U { get; }
        public Card A { get; }
        public Card B { get; }
        public TradeService Svc { get; }

        public Ctx()
        {
            U = Seed.AddUser(Db.Context);
            A = Seed.AddCard(Db.Context, "Card A");
            B = Seed.AddCard(Db.Context, "Card B");
            Svc = new TradeService(Db.Context, new TradeCalculationService(Db.Context));
        }

        public Trade Buy(Card card, int price, int qty, int hoursAgo, User? user = null) =>
            Seed.AddTrade(Db.Context, (user ?? U).Id, card.Id, TradeType.Buy, price, qty, DateTime.UtcNow.AddHours(-hoursAgo));

        public Trade Sell(Card card, int price, int qty, int hoursAgo, User? user = null) =>
            Seed.AddTrade(Db.Context, (user ?? U).Id, card.Id, TradeType.Sell, price, qty, DateTime.UtcNow.AddHours(-hoursAgo));

        public void Dispose() => Db.Dispose();
    }

    private static CreateTradeDto Dto(Guid cardId, TradeType type, int price, int qty) =>
        new() { CardId = cardId, Type = type, Price = price, Quantity = qty };

    // ADD

    [Fact]
    public async Task Add_buy_is_saved_with_zero_profit_and_a_server_side_date()
    {
        using var c = new Ctx();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var result = await c.Svc.AddTradeAsync(c.U.Id, Dto(c.A.Id, TradeType.Buy, 1000, 2));

        Assert.Equal(0m, result.CalculatedProfit);
        Assert.Equal("Card A", result.CardName);
        Assert.True(result.ExecutedAt >= before);
        Assert.Single(c.Db.NewContext().Trades);
    }

    [Fact]
    public async Task RN02_add_sell_returns_the_profit_after_the_fee()
    {
        using var c = new Ctx();
        c.Buy(c.A, 1000, 2, hoursAgo: 3);
        c.Buy(c.A, 1300, 1, hoursAgo: 2);

        var result = await c.Svc.AddTradeAsync(c.U.Id, Dto(c.A.Id, TradeType.Sell, 2000, 1));

        Assert.Equal(800m, result.CalculatedProfit);
    }

    [Fact]
    public async Task RN04_add_sell_without_position_is_rejected_and_nothing_is_saved()
    {
        using var c = new Ctx();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Svc.AddTradeAsync(c.U.Id, Dto(c.A.Id, TradeType.Sell, 2000, 1)));

        Assert.Empty(c.Db.NewContext().Trades);
    }

    [Fact]
    public async Task RN04_add_sell_larger_than_the_position_is_rejected()
    {
        using var c = new Ctx();
        c.Buy(c.A, 1000, 2, hoursAgo: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Svc.AddTradeAsync(c.U.Id, Dto(c.A.Id, TradeType.Sell, 2000, 3)));
    }

    [Fact]
    public async Task Add_trade_for_an_unknown_card_is_not_found()
    {
        using var c = new Ctx();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            c.Svc.AddTradeAsync(c.U.Id, Dto(Guid.NewGuid(), TradeType.Buy, 1000, 1)));
    }

    [Fact]
    public async Task RN04_positions_are_per_card()
    {
        using var c = new Ctx();
        c.Buy(c.A, 1000, 5, hoursAgo: 1);

        // having stock of A does not allow selling B
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Svc.AddTradeAsync(c.U.Id, Dto(c.B.Id, TradeType.Sell, 2000, 1)));
    }

    // UPDATE

    [Fact]
    public async Task Update_trade_changes_the_values_and_keeps_the_date()
    {
        using var c = new Ctx();
        var buy = c.Buy(c.A, 1000, 2, hoursAgo: 2);

        var result = await c.Svc.UpdateTradeAsync(c.U.Id, buy.Id, Dto(c.A.Id, TradeType.Buy, 1200, 4));

        var saved = c.Db.NewContext().Trades.Single();
        Assert.Equal(1200, saved.Price);
        Assert.Equal(4, saved.Quantity);
        Assert.Equal(buy.ExecutedAt, saved.ExecutedAt);
        Assert.Equal(1200, result.Price);
    }

    [Fact]
    public async Task RN04_update_sale_to_a_quantity_above_the_position_is_rejected()
    {
        using var c = new Ctx();
        c.Buy(c.A, 1000, 2, hoursAgo: 3);
        var sale = c.Sell(c.A, 2000, 1, hoursAgo: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Svc.UpdateTradeAsync(c.U.Id, sale.Id, Dto(c.A.Id, TradeType.Sell, 2000, 3)));

        Assert.Equal(1, c.Db.NewContext().Trades.Single(t => t.Id == sale.Id).Quantity); // unchanged
    }

    [Fact]
    public async Task RN04_update_sale_up_to_the_position_is_accepted()
    {
        using var c = new Ctx();
        c.Buy(c.A, 1000, 2, hoursAgo: 3);
        var sale = c.Sell(c.A, 2000, 1, hoursAgo: 2);

        var result = await c.Svc.UpdateTradeAsync(c.U.Id, sale.Id, Dto(c.A.Id, TradeType.Sell, 2000, 2));

        Assert.Equal(1800m, result.CalculatedProfit); // (1900 - 1000) * 2
    }

    [Fact]
    public async Task RN04_moving_a_buy_to_another_card_is_rejected_when_a_sale_depends_on_it()
    {
        using var c = new Ctx();
        var buy = c.Buy(c.A, 1000, 2, hoursAgo: 3);
        c.Sell(c.A, 2000, 1, hoursAgo: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Svc.UpdateTradeAsync(c.U.Id, buy.Id, Dto(c.B.Id, TradeType.Buy, 1000, 2)));
    }

    [Fact]
    public async Task Update_trade_of_another_user_is_not_found()
    {
        using var c = new Ctx();
        var other = Seed.AddUser(c.Db.Context, "other@test.com");
        var theirs = c.Buy(c.A, 1000, 1, hoursAgo: 1, user: other);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            c.Svc.UpdateTradeAsync(c.U.Id, theirs.Id, Dto(c.A.Id, TradeType.Buy, 1, 1)));
    }

    [Fact]
    public async Task Update_with_an_unknown_card_is_not_found()
    {
        using var c = new Ctx();
        var buy = c.Buy(c.A, 1000, 1, hoursAgo: 1);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            c.Svc.UpdateTradeAsync(c.U.Id, buy.Id, Dto(Guid.NewGuid(), TradeType.Buy, 1000, 1)));
    }

    // DELETE

    [Fact]
    public async Task Delete_removes_the_trade()
    {
        using var c = new Ctx();
        var buy = c.Buy(c.A, 1000, 1, hoursAgo: 1);

        await c.Svc.DeleteTradeAsync(c.U.Id, buy.Id);

        Assert.Empty(c.Db.NewContext().Trades);
    }

    [Fact]
    public async Task RN04_delete_a_buy_that_a_later_sale_depends_on_is_rejected()
    {
        using var c = new Ctx();
        var buy = c.Buy(c.A, 1000, 2, hoursAgo: 3);
        c.Sell(c.A, 2000, 2, hoursAgo: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Svc.DeleteTradeAsync(c.U.Id, buy.Id));

        Assert.Equal(2, c.Db.NewContext().Trades.Count()); // nothing was removed
    }

    [Fact]
    public async Task Delete_trade_of_another_user_is_not_found_and_keeps_it()
    {
        using var c = new Ctx();
        var other = Seed.AddUser(c.Db.Context, "other@test.com");
        var theirs = c.Buy(c.A, 1000, 1, hoursAgo: 1, user: other);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => c.Svc.DeleteTradeAsync(c.U.Id, theirs.Id));

        Assert.Single(c.Db.NewContext().Trades);
    }

    // LIST

    [Fact]
    public async Task List_returns_only_the_users_trades_newest_first_with_profit_on_sales()
    {
        using var c = new Ctx();
        var other = Seed.AddUser(c.Db.Context, "other@test.com");
        c.Buy(c.A, 1000, 2, hoursAgo: 3);
        c.Sell(c.A, 2000, 1, hoursAgo: 2);
        c.Buy(c.B, 5000, 1, hoursAgo: 1, user: other);

        var list = (await c.Svc.GetUserTradesAsync(c.U.Id)).ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal(TradeType.Sell, list[0].Type); // newest first
        Assert.Equal(900m, list[0].CalculatedProfit);
        Assert.Equal(0m, list[1].CalculatedProfit);
    }

    // DASHBOARD

    [Fact]
    public async Task Dashboard_without_sales_has_zero_profit_and_null_best_and_worst()
    {
        using var c = new Ctx();
        c.Buy(c.A, 1000, 2, hoursAgo: 1);

        var d = await c.Svc.GetDashboardAsync(c.U.Id);

        Assert.Equal(0m, d.TotalProfit);
        Assert.Null(d.BestTradeProfit);
        Assert.Null(d.WorstTradeProfit);
        Assert.Equal(1, d.OpenPositionsCount);
    }

    [Fact]
    public async Task Dashboard_sums_profit_and_reports_best_worst_and_open_positions()
    {
        using var c = new Ctx();
        c.Buy(c.A, 1000, 2, hoursAgo: 5);
        c.Buy(c.A, 1300, 1, hoursAgo: 4);
        c.Sell(c.A, 2000, 1, hoursAgo: 3); // +800
        c.Sell(c.A, 1000, 1, hoursAgo: 2); // -150 (average stays 1100)

        var d = await c.Svc.GetDashboardAsync(c.U.Id);

        Assert.Equal(650m, d.TotalProfit);
        Assert.Equal(800m, d.BestTradeProfit);
        Assert.Equal(-150m, d.WorstTradeProfit);
        Assert.Equal(1, d.OpenPositionsCount);
        var position = Assert.Single(d.OpenPositions);
        Assert.Equal(c.A.Id, position.CardId);
        Assert.Equal(1, position.Quantity); // 3 bought - 2 sold
    }

    [Fact]
    public async Task Dashboard_does_not_list_fully_sold_cards_as_open_positions()
    {
        using var c = new Ctx();
        c.Buy(c.A, 1000, 1, hoursAgo: 3);
        c.Sell(c.A, 2000, 1, hoursAgo: 2);

        var d = await c.Svc.GetDashboardAsync(c.U.Id);

        Assert.Equal(0, d.OpenPositionsCount);
        Assert.Empty(d.OpenPositions);
    }

    [Fact]
    public async Task Dashboard_ignores_other_users()
    {
        using var c = new Ctx();
        var other = Seed.AddUser(c.Db.Context, "other@test.com");
        c.Buy(c.A, 1000, 1, hoursAgo: 3, user: other);
        c.Sell(c.A, 2000, 1, hoursAgo: 2, user: other);

        var d = await c.Svc.GetDashboardAsync(c.U.Id);

        Assert.Equal(0m, d.TotalProfit);
        Assert.Equal(0, d.OpenPositionsCount);
    }
}
