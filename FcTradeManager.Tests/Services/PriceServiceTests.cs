using FcTradeManager.Application.DTOs;
using FcTradeManager.Domain.Enums;
using FcTradeManager.Infrastructure.Services;
using FcTradeManager.Tests.Support;

namespace FcTradeManager.Tests.Services;

public class PriceServiceTests
{
    [Fact]
    public async Task Add_price_is_saved_as_manual_by_default()
    {
        using var db = new TestDb();
        var card = Seed.AddCard(db.Context);

        var result = await new PriceService(db.Context).AddPriceSnapshotAsync(card.Id, new CreatePriceSnapshotDto { Price = 1500 });

        Assert.Equal(PriceSource.Manual, result.Source);
        Assert.Equal(1500, result.Price);
        var saved = db.NewContext().PriceSnapshots.Single();
        Assert.Equal(PriceSource.Manual, saved.Source);
        Assert.Equal(card.Id, saved.CardId);
    }

    [Fact]
    public async Task Add_price_accepts_another_source()
    {
        using var db = new TestDb();
        var card = Seed.AddCard(db.Context);

        var result = await new PriceService(db.Context).AddPriceSnapshotAsync(card.Id, new CreatePriceSnapshotDto { Price = 1500 }, PriceSource.Simulated);

        Assert.Equal(PriceSource.Simulated, result.Source);
    }

    [Fact]
    public async Task Add_price_for_an_unknown_card_is_not_found()
    {
        using var db = new TestDb();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            new PriceService(db.Context).AddPriceSnapshotAsync(Guid.NewGuid(), new CreatePriceSnapshotDto { Price = 1500 }));
    }

    // HISTORY

    private static async Task<List<PriceSnapshotResponseDto>> History(TestDb db, Guid cardId, string period) =>
        (await new PriceService(db.Context).GetPriceHistoryAsync(cardId, period)).ToList();

    private static Guid SeedHistory(TestDb db)
    {
        var card = Seed.AddCard(db.Context);
        var other = Seed.AddCard(db.Context, "Other");
        Seed.AddSnapshot(db.Context, card.Id, 1, DateTime.UtcNow.AddDays(-40));  // outside everything
        Seed.AddSnapshot(db.Context, card.Id, 2, DateTime.UtcNow.AddDays(-10));  // 30d only
        Seed.AddSnapshot(db.Context, card.Id, 3, DateTime.UtcNow.AddDays(-3));   // 7d and 30d
        Seed.AddSnapshot(db.Context, card.Id, 4, DateTime.UtcNow.AddHours(-25)); // 7d and 30d
        Seed.AddSnapshot(db.Context, card.Id, 5, DateTime.UtcNow.AddHours(-23)); // all periods
        Seed.AddSnapshot(db.Context, card.Id, 6, DateTime.UtcNow.AddMinutes(-5)); // all periods
        Seed.AddSnapshot(db.Context, other.Id, 99, DateTime.UtcNow.AddMinutes(-5)); // another card
        return card.Id;
    }

    [Theory]
    [InlineData("24h", new[] { 5, 6 })]
    [InlineData("7d", new[] { 3, 4, 5, 6 })]
    [InlineData("30d", new[] { 2, 3, 4, 5, 6 })]
    public async Task History_returns_the_period_in_ascending_order(string period, int[] expectedPrices)
    {
        using var db = new TestDb();
        var cardId = SeedHistory(db);

        var result = await History(db, cardId, period);

        Assert.Equal(expectedPrices, result.Select(r => r.Price).ToArray());
    }

    [Fact]
    public async Task History_period_is_case_insensitive()
    {
        using var db = new TestDb();
        var cardId = SeedHistory(db);

        Assert.Equal(2, (await History(db, cardId, "24H")).Count);
    }

    [Fact]
    public async Task History_with_an_invalid_period_is_rejected()
    {
        using var db = new TestDb();
        var cardId = SeedHistory(db);

        await Assert.ThrowsAsync<ArgumentException>(() => History(db, cardId, "1y"));
    }

    [Fact]
    public async Task History_for_an_unknown_card_is_not_found()
    {
        using var db = new TestDb();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => History(db, Guid.NewGuid(), "24h"));
    }

    [Fact]
    public async Task History_of_a_card_without_prices_is_empty()
    {
        using var db = new TestDb();
        var card = Seed.AddCard(db.Context);

        Assert.Empty(await History(db, card.Id, "30d"));
    }
}
