using System.Security.Cryptography;
using System.Text;
using FcTradeManager.Application.Interfaces;
using FcTradeManager.Domain.Enums;
using FcTradeManager.Domain.Rules;
using Microsoft.Extensions.Options;

namespace FcTradeManager.Infrastructure.Services;

public class PriceSimulationOptions
{
    /// <summary>Turns the live generation off (the seed script and manual prices keep working).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Friday rise of 86+ cards, as in the seed script. Turn off for a flat weekly pattern.</summary>
    public bool FridayBoost { get; set; } = true;
}

/// <summary>
/// Simulated prices: continues the history of a card with the same model as the seed script.
/// Deterministic: the same card, history and slot always give the same price (no hidden random state),
/// so generating a slot twice can never produce two different values.
/// </summary>
public class SimulatedPriceProvider : IPriceProvider
{
    private readonly bool _fridayBoost;

    public SimulatedPriceProvider(IOptions<PriceSimulationOptions> options)
    {
        _fridayBoost = options.Value.FridayBoost;
    }

    public PriceSource Source => PriceSource.Simulated;

    public Task<IReadOnlyList<PriceQuote>> GetPricesAsync(PriceRequest request, CancellationToken ct = default)
    {
        IReadOnlyList<PriceQuote> result = Array.Empty<PriceQuote>();

        // without a starting point there is nothing to continue from (the seed script creates the first history)
        if (request.RecentHistory.Count > 0 && request.Slots.Count > 0)
        {
            var history = request.RecentHistory
                .OrderBy(h => h.RecordedAt)
                .Select(h => (h.RecordedAt, h.Price))
                .ToList();

            var (anchor, noise) = PriceSimulationRules.EstimateState(history, request.Overall, _fridayBoost);

            var quotes = new List<PriceQuote>(request.Slots.Count);
            foreach (var slot in request.Slots.OrderBy(s => s))
            {
                noise = PriceSimulationRules.NextNoise(noise, StandardNormal(request.ExternalRef, slot));
                var price = PriceSimulationRules.PriceAt(anchor, noise, slot, request.Overall, _fridayBoost);
                quotes.Add(new PriceQuote(request.CardId, slot, price));
            }
            result = quotes;
        }

        return Task.FromResult(result);
    }

    // random number that depends only on (card, slot): stable across runs and machines (unlike string.GetHashCode)
    private static double StandardNormal(string externalRef, DateTime slot)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{externalRef}|{slot:yyyy-MM-ddTHH}"));
        var rng = new Random(BitConverter.ToInt32(hash, 0));

        // Box-Muller
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
