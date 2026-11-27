using FcTradeManager.Domain.Enums;

namespace FcTradeManager.Application.Interfaces;

public record PriceHistoryPoint(DateTime RecordedAt, int Price);

public record PriceRequest(
    Guid CardId,
    string ExternalRef,
    int Overall,
    IReadOnlyList<PriceHistoryPoint> RecentHistory,
    IReadOnlyList<DateTime> Slots);

public record PriceQuote(Guid CardId, DateTime RecordedAt, int Price);

// an actual external source would be a new implementation of this interface
public interface IPriceProvider
{
    PriceSource Source { get; }

    Task<IReadOnlyList<PriceQuote>> GetPricesAsync(PriceRequest request, CancellationToken ct = default);
}
