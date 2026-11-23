using FcTradeManager.Application.DTOs;
using FcTradeManager.Domain.Entities;
using FcTradeManager.Infrastructure.Services;
using FcTradeManager.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace FcTradeManager.Tests.Services;

public class WatchlistServiceTests
{
    private sealed class Ctx : IDisposable
    {
        public TestDb Db { get; } = new();
        public User U { get; }
        public WatchlistService Svc { get; }

        public Ctx()
        {
            U = Seed.AddUser(Db.Context);
            Svc = new WatchlistService(Db.Context);
        }

        public Card NewCard(string name = "Card") => Seed.AddCard(Db.Context, name);

        public void Price(Card card, int price, double hoursAgo) =>
            Seed.AddSnapshot(Db.Context, card.Id, price, DateTime.UtcNow.AddHours(-hoursAgo));

        public void Dispose() => Db.Dispose();
    }

    // 24h CHANGE

    [Theory]
    [InlineData(1100, 1000, 100, 10.00)]
    [InlineData(1000, 1100, -100, -9.09)]
    [InlineData(1000, 1000, 0, 0.00)]
    public void RN05_change_is_in_coins_and_percent_relative_to_the_baseline(int last, int baseline, int change, double percent)
    {
        var (c, p) = WatchlistService.ComputeChange(last, baseline);

        Assert.Equal(change, c);
        Assert.Equal((decimal)percent, p);
    }

    [Theory]
    [InlineData(null, 1000)]
    [InlineData(1000, null)]
    [InlineData(1000, 0)]
    [InlineData(null, null)]
    public void RN05_change_is_null_without_a_valid_baseline(int? last, int? baseline)
    {
        var (c, p) = WatchlistService.ComputeChange(last, baseline);

        Assert.Null(c);
        Assert.Null(p);
    }

    // LISTING

    [Fact]
    public async Task RN05_baseline_is_the_latest_price_from_24h_ago_or_older()
    {
        using var c = new Ctx();
        var card = c.NewCard();
        Seed.AddWatch(c.Db.Context, c.U.Id, card.Id, 900, 1500);
        c.Price(card, 1000, hoursAgo: 30);
        c.Price(card, 1100, hoursAgo: 1);

        var item = Assert.Single(await c.Svc.GetUserWatchlistAsync(c.U.Id));

        Assert.Equal(1100, item.LastPrice);
        Assert.Equal(100, item.PriceChange24h);
        Assert.Equal(10.00m, item.PriceChangePercent24h);
    }

    [Fact]
    public async Task RN05_without_an_old_price_the_oldest_of_the_window_is_used_when_there_are_two_or_more()
    {
        using var c = new Ctx();
        var card = c.NewCard();
        Seed.AddWatch(c.Db.Context, c.U.Id, card.Id, 900, 1500);
        c.Price(card, 1000, hoursAgo: 10);
        c.Price(card, 1200, hoursAgo: 1);

        var item = Assert.Single(await c.Svc.GetUserWatchlistAsync(c.U.Id));

        Assert.Equal(200, item.PriceChange24h);
        Assert.Equal(20.00m, item.PriceChangePercent24h);
    }

    [Fact]
    public async Task RN05_a_single_recent_price_has_no_change()
    {
        using var c = new Ctx();
        var card = c.NewCard();
        Seed.AddWatch(c.Db.Context, c.U.Id, card.Id, 900, 1500);
        c.Price(card, 1000, hoursAgo: 1);

        var item = Assert.Single(await c.Svc.GetUserWatchlistAsync(c.U.Id));

        Assert.Equal(1000, item.LastPrice);
        Assert.Null(item.PriceChange24h);
        Assert.Null(item.PriceChangePercent24h);
    }

    [Fact]
    public async Task Distances_are_relative_to_the_last_price()
    {
        using var c = new Ctx();
        var card = c.NewCard();
        Seed.AddWatch(c.Db.Context, c.U.Id, card.Id, buyTarget: 900, sellTarget: 1500);
        c.Price(card, 1100, hoursAgo: 1);

        var item = Assert.Single(await c.Svc.GetUserWatchlistAsync(c.U.Id));

        Assert.Equal(200, item.DistanceToBuy);   // last - buy target
        Assert.Equal(400, item.DistanceToSell);  // sell target - last
        Assert.Equal(900, item.TargetBuyPrice);
        Assert.Equal(1500, item.TargetSellPrice);
    }

    [Fact]
    public async Task Card_without_prices_has_null_price_fields()
    {
        using var c = new Ctx();
        Seed.AddWatch(c.Db.Context, c.U.Id, c.NewCard().Id, 900, 1500);

        var item = Assert.Single(await c.Svc.GetUserWatchlistAsync(c.U.Id));

        Assert.Null(item.LastPrice);
        Assert.Null(item.DistanceToBuy);
        Assert.Null(item.DistanceToSell);
        Assert.Null(item.PriceChange24h);
    }

    [Fact]
    public async Task Listing_returns_only_the_users_items()
    {
        using var c = new Ctx();
        var other = Seed.AddUser(c.Db.Context, "other@test.com");
        var card = c.NewCard();
        Seed.AddWatch(c.Db.Context, c.U.Id, card.Id, 900, 1500);
        Seed.AddWatch(c.Db.Context, other.Id, card.Id, 100, 200);

        var list = await c.Svc.GetUserWatchlistAsync(c.U.Id);

        Assert.Single(list);
    }

    [Fact]
    public async Task Listing_with_no_items_is_empty()
    {
        using var c = new Ctx();

        Assert.Empty(await c.Svc.GetUserWatchlistAsync(c.U.Id));
    }

    [Theory]
    [InlineData("name", new[] { "Alpha", "Beta", "Gamma" })]
    [InlineData("price", new[] { "Beta", "Gamma", "Alpha" })]
    [InlineData("change", new[] { "Alpha", "Beta", "Gamma" })] // Gamma has no change: last
    [InlineData(null, new[] { "Alpha", "Beta", "Gamma" })]
    [InlineData("unknown", new[] { "Alpha", "Beta", "Gamma" })]
    public async Task Sorting(string? sortBy, string[] expected)
    {
        using var c = new Ctx();
        var alpha = c.NewCard("Alpha");
        var beta = c.NewCard("Beta");
        var gamma = c.NewCard("Gamma");
        foreach (var card in new[] { gamma, alpha, beta }) // inserted out of order on purpose
            Seed.AddWatch(c.Db.Context, c.U.Id, card.Id, 100, 9_000_000);

        c.Price(alpha, 1000, hoursAgo: 30); c.Price(alpha, 1100, hoursAgo: 1); // +10%
        c.Price(beta, 3200, hoursAgo: 30); c.Price(beta, 3040, hoursAgo: 1);   // -5%
        c.Price(gamma, 2000, hoursAgo: 1);                                      // no change data

        var names = (await c.Svc.GetUserWatchlistAsync(c.U.Id, sortBy)).Select(i => i.CardName).ToArray();

        Assert.Equal(expected, names);
    }

    // ADD

    [Fact]
    public async Task RN09_add_saves_the_item_with_its_targets()
    {
        using var c = new Ctx();
        var card = c.NewCard("Mbappé");

        var result = await c.Svc.AddToWatchlistAsync(c.U.Id, new CreateWatchlistDto { CardId = card.Id, TargetBuyPrice = 1000, TargetSellPrice = 2000 });

        var saved = c.Db.NewContext().WatchlistItems.Single();
        Assert.Equal(1000, saved.BuyTarget);
        Assert.Equal(2000, saved.SellTarget);
        Assert.False(saved.BuyAlertTriggered);
        Assert.False(saved.SellAlertTriggered);
        Assert.Equal("Mbappé", result.CardName);
    }

    [Theory]
    [InlineData(2000, 1000)] // buy above sell
    [InlineData(1000, 1000)] // equal
    public async Task RN09_buy_target_must_be_lower_than_the_sell_target(int buy, int sell)
    {
        using var c = new Ctx();
        var card = c.NewCard();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            c.Svc.AddToWatchlistAsync(c.U.Id, new CreateWatchlistDto { CardId = card.Id, TargetBuyPrice = buy, TargetSellPrice = sell }));

        Assert.Empty(c.Db.NewContext().WatchlistItems);
    }

    [Fact]
    public async Task Add_for_an_unknown_card_is_not_found()
    {
        using var c = new Ctx();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            c.Svc.AddToWatchlistAsync(c.U.Id, new CreateWatchlistDto { CardId = Guid.NewGuid(), TargetBuyPrice = 1, TargetSellPrice = 2 }));
    }

    [Fact]
    public async Task RN09_the_same_card_cannot_be_added_twice()
    {
        using var c = new Ctx();
        var card = c.NewCard();
        var dto = new CreateWatchlistDto { CardId = card.Id, TargetBuyPrice = 1000, TargetSellPrice = 2000 };
        await c.Svc.AddToWatchlistAsync(c.U.Id, dto);

        // the controller turns this exception into 409
        await Assert.ThrowsAsync<DbUpdateException>(() => c.Svc.AddToWatchlistAsync(c.U.Id, dto));
    }

    [Fact]
    public async Task RN09_two_users_can_watch_the_same_card()
    {
        using var c = new Ctx();
        var other = Seed.AddUser(c.Db.Context, "other@test.com");
        var card = c.NewCard();
        var dto = new CreateWatchlistDto { CardId = card.Id, TargetBuyPrice = 1000, TargetSellPrice = 2000 };

        await c.Svc.AddToWatchlistAsync(c.U.Id, dto);
        await c.Svc.AddToWatchlistAsync(other.Id, dto);

        Assert.Equal(2, c.Db.NewContext().WatchlistItems.Count());
    }

    // UPDATE

    [Fact]
    public async Task RN07_changing_the_buy_target_resets_only_the_buy_flag()
    {
        using var c = new Ctx();
        var item = Seed.AddWatch(c.Db.Context, c.U.Id, c.NewCard().Id, 1000, 2000, buyTriggered: true, sellTriggered: true);

        await c.Svc.UpdateWatchlistAsync(c.U.Id, item.Id, new UpdateWatchlistDto { BuyTarget = 800, SellTarget = 2000 });

        var saved = c.Db.NewContext().WatchlistItems.Single();
        Assert.Equal(800, saved.BuyTarget);
        Assert.False(saved.BuyAlertTriggered);
        Assert.True(saved.SellAlertTriggered);
    }

    [Fact]
    public async Task RN07_changing_the_sell_target_resets_only_the_sell_flag()
    {
        using var c = new Ctx();
        var item = Seed.AddWatch(c.Db.Context, c.U.Id, c.NewCard().Id, 1000, 2000, buyTriggered: true, sellTriggered: true);

        await c.Svc.UpdateWatchlistAsync(c.U.Id, item.Id, new UpdateWatchlistDto { BuyTarget = 1000, SellTarget = 2500 });

        var saved = c.Db.NewContext().WatchlistItems.Single();
        Assert.True(saved.BuyAlertTriggered);
        Assert.False(saved.SellAlertTriggered);
    }

    [Fact]
    public async Task RN07_saving_the_same_targets_keeps_the_flags()
    {
        using var c = new Ctx();
        var item = Seed.AddWatch(c.Db.Context, c.U.Id, c.NewCard().Id, 1000, 2000, buyTriggered: true, sellTriggered: true);

        await c.Svc.UpdateWatchlistAsync(c.U.Id, item.Id, new UpdateWatchlistDto { BuyTarget = 1000, SellTarget = 2000 });

        var saved = c.Db.NewContext().WatchlistItems.Single();
        Assert.True(saved.BuyAlertTriggered);
        Assert.True(saved.SellAlertTriggered);
    }

    [Fact]
    public async Task RN09_update_with_buy_not_lower_than_sell_is_rejected()
    {
        using var c = new Ctx();
        var item = Seed.AddWatch(c.Db.Context, c.U.Id, c.NewCard().Id, 1000, 2000);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            c.Svc.UpdateWatchlistAsync(c.U.Id, item.Id, new UpdateWatchlistDto { BuyTarget = 2000, SellTarget = 2000 }));
    }

    [Fact]
    public async Task Update_of_another_users_item_is_not_found()
    {
        using var c = new Ctx();
        var other = Seed.AddUser(c.Db.Context, "other@test.com");
        var theirs = Seed.AddWatch(c.Db.Context, other.Id, c.NewCard().Id, 1000, 2000);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            c.Svc.UpdateWatchlistAsync(c.U.Id, theirs.Id, new UpdateWatchlistDto { BuyTarget = 1, SellTarget = 2 }));
    }

    // REMOVE

    [Fact]
    public async Task Remove_deletes_the_item()
    {
        using var c = new Ctx();
        var item = Seed.AddWatch(c.Db.Context, c.U.Id, c.NewCard().Id, 1000, 2000);

        await c.Svc.RemoveFromWatchlistAsync(c.U.Id, item.Id);

        Assert.Empty(c.Db.NewContext().WatchlistItems);
    }

    [Fact]
    public async Task Remove_of_another_users_item_is_not_found_and_keeps_it()
    {
        using var c = new Ctx();
        var other = Seed.AddUser(c.Db.Context, "other@test.com");
        var theirs = Seed.AddWatch(c.Db.Context, other.Id, c.NewCard().Id, 1000, 2000);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => c.Svc.RemoveFromWatchlistAsync(c.U.Id, theirs.Id));

        Assert.Single(c.Db.NewContext().WatchlistItems);
    }
}
