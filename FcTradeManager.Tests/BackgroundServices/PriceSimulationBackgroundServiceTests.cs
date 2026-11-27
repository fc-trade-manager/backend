using FcTradeManager.Domain.Entities;
using FcTradeManager.Domain.Enums;
using FcTradeManager.Infrastructure.BackgroundServices;
using FcTradeManager.Infrastructure.Services;
using FcTradeManager.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FcTradeManager.Tests.BackgroundServices;

// the live generation: fills the gap up to the current hour and then one price per hour (RF14, RN10)
public class PriceSimulationBackgroundServiceTests
{
    // Wednesday 25/11/2026 22:30 UTC -> current slot is 22:00
    private static readonly DateTime Now = new(2026, 11, 25, 22, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime CurrentSlot = new(2026, 11, 25, 22, 0, 0, DateTimeKind.Utc);

    private static PriceSimulationBackgroundService Service()
    {
        var options = Options.Create(new PriceSimulationOptions());
        return new PriceSimulationBackgroundService(
            Substitute.For<IServiceProvider>(),
            new SimulatedPriceProvider(options),
            options,
            NullLogger<PriceSimulationBackgroundService>.Instance);
    }

    // simulated history of `hours` hourly prices that ends exactly at `last`
    private static void SeedSimulated(TestDb db, Card card, DateTime last, int hours = 72, int price = 20_000)
    {
        for (var i = hours - 1; i >= 0; i--)
            db.Context.PriceSnapshots.Add(new PriceSnapshot
            {
                Id = Guid.NewGuid(),
                CardId = card.Id,
                Price = price,
                Source = PriceSource.Simulated,
                RecordedAt = last.AddHours(-i)
            });
        db.Context.SaveChanges();
    }

    private static List<PriceSnapshot> Simulated(TestDb db, Card card) =>
        db.NewContext().PriceSnapshots
            .Where(p => p.CardId == card.Id && p.Source == PriceSource.Simulated)
            .ToList()
            .OrderBy(p => p.RecordedAt)
            .ToList();

    [Fact]
    public async Task Fills_every_hour_between_the_last_simulated_price_and_now()
    {
        using var db = new TestDb();
        var card = Seed.AddCard(db.Context);
        SeedSimulated(db, card, last: CurrentSlot.AddHours(-5));

        var created = await Service().GenerateMissingPricesAsync(db.Context, Now, CancellationToken.None);

        Assert.Equal(5, created);
        var added = Simulated(db, card).Where(p => p.RecordedAt > CurrentSlot.AddHours(-5)).ToList();
        Assert.Equal(5, added.Count);
        Assert.Equal(Enumerable.Range(-4, 5).Select(h => CurrentSlot.AddHours(h)), added.Select(p => p.RecordedAt));
        Assert.All(added, p => Assert.Equal(PriceSource.Simulated, p.Source));
        Assert.All(added, p => Assert.InRange(p.Price, 200, 15_000_000));
    }

    [Fact]
    public async Task Never_creates_a_price_for_an_hour_that_has_not_started_yet()
    {
        using var db = new TestDb();
        var card = Seed.AddCard(db.Context);
        SeedSimulated(db, card, last: CurrentSlot.AddHours(-3));

        await Service().GenerateMissingPricesAsync(db.Context, Now, CancellationToken.None);

        Assert.Equal(CurrentSlot, Simulated(db, card).Max(p => p.RecordedAt));
    }

    [Fact]
    public async Task Does_nothing_when_the_history_is_already_up_to_date()
    {
        using var db = new TestDb();
        var card = Seed.AddCard(db.Context);
        SeedSimulated(db, card, last: CurrentSlot);

        var created = await Service().GenerateMissingPricesAsync(db.Context, Now, CancellationToken.None);

        Assert.Equal(0, created);
        Assert.Equal(72, Simulated(db, card).Count);
    }

    [Fact]
    public async Task Running_twice_in_the_same_hour_does_not_duplicate_prices()
    {
        using var db = new TestDb();
        var card = Seed.AddCard(db.Context);
        SeedSimulated(db, card, last: CurrentSlot.AddHours(-3));

        await Service().GenerateMissingPricesAsync(db.Context, Now, CancellationToken.None);
        var second = await Service().GenerateMissingPricesAsync(db.Context, Now.AddMinutes(20), CancellationToken.None);

        Assert.Equal(0, second);
        Assert.Equal(72 + 3, Simulated(db, card).Count);
    }

    [Fact]
    public async Task A_new_hour_adds_exactly_one_price()
    {
        using var db = new TestDb();
        var card = Seed.AddCard(db.Context);
        SeedSimulated(db, card, last: CurrentSlot);

        var created = await Service().GenerateMissingPricesAsync(db.Context, Now.AddHours(1), CancellationToken.None);

        Assert.Equal(1, created);
        Assert.Equal(CurrentSlot.AddHours(1), Simulated(db, card).Max(p => p.RecordedAt));
    }

    [Fact]
    public async Task Cards_without_any_simulated_history_are_left_alone()
    {
        using var db = new TestDb();
        var withHistory = Seed.AddCard(db.Context, "With");
        var withoutHistory = Seed.AddCard(db.Context, "Without");
        SeedSimulated(db, withHistory, last: CurrentSlot.AddHours(-2));

        var created = await Service().GenerateMissingPricesAsync(db.Context, Now, CancellationToken.None);

        Assert.Equal(2, created);
        Assert.Empty(db.NewContext().PriceSnapshots.Where(p => p.CardId == withoutHistory.Id));
    }

    [Fact]
    public async Task A_card_with_only_manual_prices_does_not_get_simulated_ones()
    {
        using var db = new TestDb();
        var card = Seed.AddCard(db.Context);
        Seed.AddSnapshot(db.Context, card.Id, 900, CurrentSlot.AddHours(-2), PriceSource.Manual);

        var created = await Service().GenerateMissingPricesAsync(db.Context, Now, CancellationToken.None);

        Assert.Equal(0, created);
    }

    [Fact]
    public async Task RN10_a_manual_price_at_the_same_minute_prevails_over_the_simulated_one()
    {
        using var db = new TestDb();
        var card = Seed.AddCard(db.Context);
        SeedSimulated(db, card, last: CurrentSlot.AddHours(-5));
        var slotWithManual = CurrentSlot.AddHours(-2); // 20:00
        Seed.AddSnapshot(db.Context, card.Id, 900, slotWithManual.AddSeconds(30), PriceSource.Manual);

        var created = await Service().GenerateMissingPricesAsync(db.Context, Now, CancellationToken.None);

        Assert.Equal(4, created); // 5 slots minus the one with a manual price
        Assert.DoesNotContain(Simulated(db, card), p => p.RecordedAt == slotWithManual);
        Assert.Single(db.NewContext().PriceSnapshots.Where(p => p.Source == PriceSource.Manual));
    }
}
