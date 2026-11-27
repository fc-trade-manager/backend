using FcTradeManager.Application.Interfaces;
using FcTradeManager.Domain.Enums;
using FcTradeManager.Domain.Rules;
using FcTradeManager.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace FcTradeManager.Tests.Services;

// RF14 / RF15: the simulated source behind IPriceProvider
public class SimulatedPriceProviderTests
{
    private static readonly DateTime Monday = new(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);

    private static SimulatedPriceProvider Provider(bool fridayBoost = true) =>
        new(Options.Create(new PriceSimulationOptions { FridayBoost = fridayBoost }));

    private static List<PriceHistoryPoint> FlatHistory(DateTime start, int hours, int price = 20_000) =>
        Enumerable.Range(0, hours).Select(i => new PriceHistoryPoint(start.AddHours(i), price)).ToList();

    private static PriceRequest Request(
        List<PriceHistoryPoint> history, IReadOnlyList<DateTime> slots,
        string externalRef = "cards-001", int overall = 80, Guid? cardId = null) =>
        new(cardId ?? Guid.NewGuid(), externalRef, overall, history, slots);

    private static List<DateTime> Slots(DateTime first, int count) =>
        Enumerable.Range(0, count).Select(i => first.AddHours(i)).ToList();

    [Fact]
    public void The_source_is_simulated()
    {
        IPriceProvider provider = Provider();

        Assert.Equal(PriceSource.Simulated, provider.Source);
    }

    [Fact]
    public async Task Returns_one_price_per_slot_for_that_card()
    {
        var cardId = Guid.NewGuid();
        var history = FlatHistory(Monday, 48);
        var slots = Slots(Monday.AddHours(48), 6);

        var quotes = await Provider().GetPricesAsync(Request(history, slots, cardId: cardId));

        Assert.Equal(6, quotes.Count);
        Assert.Equal(slots, quotes.Select(q => q.RecordedAt).ToList());
        Assert.All(quotes, q => Assert.Equal(cardId, q.CardId));
        Assert.All(quotes, q => Assert.InRange(q.Price, PriceSimulationRules.MinPrice, PriceSimulationRules.MaxPrice));
    }

    [Fact]
    public async Task Without_history_there_is_nothing_to_continue_from()
    {
        var quotes = await Provider().GetPricesAsync(Request(new List<PriceHistoryPoint>(), Slots(Monday, 3)));

        Assert.Empty(quotes);
    }

    [Fact]
    public async Task Without_slots_it_returns_nothing()
    {
        var quotes = await Provider().GetPricesAsync(Request(FlatHistory(Monday, 48), new List<DateTime>()));

        Assert.Empty(quotes);
    }

    [Fact]
    public async Task The_same_request_always_gives_the_same_prices()
    {
        var history = FlatHistory(Monday, 48);
        var slots = Slots(Monday.AddHours(48), 24);
        var cardId = Guid.NewGuid();

        var first = await Provider().GetPricesAsync(Request(history, slots, cardId: cardId));
        var second = await Provider().GetPricesAsync(Request(history, slots, cardId: cardId));

        Assert.Equal(first.Select(q => q.Price), second.Select(q => q.Price));
    }

    [Fact]
    public async Task Different_cards_do_not_move_together()
    {
        var history = FlatHistory(Monday, 48);
        var slots = Slots(Monday.AddHours(48), 24);

        var a = await Provider().GetPricesAsync(Request(history, slots, externalRef: "cards-001"));
        var b = await Provider().GetPricesAsync(Request(history, slots, externalRef: "cards-002"));

        Assert.NotEqual(a.Select(q => q.Price), b.Select(q => q.Price));
    }

    [Fact]
    public async Task The_series_continues_from_where_the_history_stopped_without_jumping()
    {
        var history = FlatHistory(Monday, 72, price: 20_000);

        var quotes = await Provider().GetPricesAsync(Request(history, Slots(Monday.AddHours(72), 24)));

        Assert.All(quotes, q => Assert.InRange(q.Price, 17_000, 23_000));
        Assert.InRange(quotes[0].Price, 19_500, 20_500);
    }

    [Fact]
    public async Task The_history_can_come_in_any_order()
    {
        var history = FlatHistory(Monday, 48);
        var shuffled = history.AsEnumerable().Reverse().ToList();
        var slots = Slots(Monday.AddHours(48), 6);
        var cardId = Guid.NewGuid();

        var a = await Provider().GetPricesAsync(Request(history, slots, cardId: cardId));
        var b = await Provider().GetPricesAsync(Request(shuffled, slots, cardId: cardId));

        Assert.Equal(a.Select(q => q.Price), b.Select(q => q.Price));
    }

    [Fact]
    public async Task Friday_boost_raises_the_price_of_a_86_plus_card_and_can_be_turned_off()
    {
        var friday = new DateTime(2026, 11, 27, 0, 0, 0, DateTimeKind.Utc);
        var history = FlatHistory(friday.AddHours(-48), 48); // Wednesday-Thursday: no boost in the history
        var request = Request(history, new List<DateTime> { friday }, overall: 95);

        var withBoost = (await Provider(fridayBoost: true).GetPricesAsync(request))[0].Price;
        var withoutBoost = (await Provider(fridayBoost: false).GetPricesAsync(request))[0].Price;

        var ratio = (double)withBoost / withoutBoost;
        Assert.InRange(ratio, 1.06, 1.10); // about +8%
    }

    [Fact]
    public async Task Friday_boost_does_not_apply_to_a_card_below_overall_86()
    {
        var friday = new DateTime(2026, 11, 27, 0, 0, 0, DateTimeKind.Utc);
        var history = FlatHistory(friday.AddHours(-48), 48);
        var request = Request(history, new List<DateTime> { friday }, overall: 85);

        var withBoost = (await Provider(fridayBoost: true).GetPricesAsync(request))[0].Price;
        var withoutBoost = (await Provider(fridayBoost: false).GetPricesAsync(request))[0].Price;

        Assert.Equal(withoutBoost, withBoost);
    }

    [Fact]
    public async Task Prices_never_leave_the_allowed_range_even_for_extreme_levels()
    {
        var cheap = await Provider().GetPricesAsync(Request(FlatHistory(Monday, 48, price: 200), Slots(Monday.AddHours(48), 48)));
        var expensive = await Provider().GetPricesAsync(Request(FlatHistory(Monday, 48, price: 15_000_000), Slots(Monday.AddHours(48), 48)));

        Assert.All(cheap, q => Assert.InRange(q.Price, 200, 15_000_000));
        Assert.All(expensive, q => Assert.InRange(q.Price, 200, 15_000_000));
    }
}
