using FcTradeManager.Application.Interfaces;
using FcTradeManager.Domain.Entities;
using FcTradeManager.Domain.Enums;
using FcTradeManager.Domain.Rules;
using FcTradeManager.Infrastructure.Data;
using FcTradeManager.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FcTradeManager.Infrastructure.BackgroundServices;

// the seed script is still the way to create the first history, this keeps going (might unify it later)
// one per hour per card
public class PriceSimulationBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IPriceProvider _provider;
    private readonly IOptions<PriceSimulationOptions> _options;
    private readonly ILogger<PriceSimulationBackgroundService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    private DateTime? _lastProcessedSlot;

    public PriceSimulationBackgroundService(
        IServiceProvider serviceProvider,
        IPriceProvider provider,
        IOptions<PriceSimulationOptions> options,
        ILogger<PriceSimulationBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _provider = provider;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("PriceSimulationBackgroundService is disabled (PriceSimulation:Enabled = false).");
            return;
        }

        _logger.LogInformation("PriceSimulationBackgroundService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var slot = PriceSimulationRules.FloorToSlot(now);

                // runs at startup and then once per new hour
                // if it fails = again on next tick
                if (_lastProcessedSlot != slot)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var created = await GenerateMissingPricesAsync(context, now, stoppingToken);
                    _lastProcessedSlot = slot;

                    if (created > 0)
                        _logger.LogInformation("{Count} simulated prices created up to {Slot:u}.", created, slot);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate simulated prices in background.");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // for each card, get the last 7 days of history and generate simulated prices for each missing hour up to now
    // returns the number of simulated prices created
    internal async Task<int> GenerateMissingPricesAsync(AppDbContext context, DateTime nowUtc, CancellationToken ct) // internal so Tests can call it
    {
        var lastSlot = PriceSimulationRules.FloorToSlot(nowUtc);
        var earliestSlot = lastSlot.AddDays(-PriceSimulationRules.MaxBackfillDays);

        var cards = await context.Cards
            .AsNoTracking()
            .Select(c => new { c.Id, c.ExternalRef, c.Overall })
            .ToListAsync(ct);

        var source = _provider.Source; // plain value so EF can turn it into a query parameter
        var total = 0;
        var withoutHistory = 0;

        foreach (var card in cards)
        {
            var recent = await context.PriceSnapshots
                .AsNoTracking()
                .Where(p => p.CardId == card.Id && p.Source == source)
                .OrderByDescending(p => p.RecordedAt)
                .Take(PriceSimulationRules.AnchorWindowHours)
                .Select(p => new { p.RecordedAt, p.Price })
                .ToListAsync(ct);

            if (recent.Count == 0)
            {
                withoutHistory++;
                continue;
            }

            var firstSlot = PriceSimulationRules.NextSlot(recent[0].RecordedAt);
            if (firstSlot < earliestSlot) firstSlot = earliestSlot;

            var slots = PriceSimulationRules.SlotsBetween(firstSlot, lastSlot);
            if (slots.Count == 0) continue;

            // RN10: a manual price at the same minute prevails, so no simulated price is created for that minute
            var manualMinutes = (await context.PriceSnapshots
                    .AsNoTracking()
                    .Where(p => p.CardId == card.Id && p.Source == PriceSource.Manual && p.RecordedAt >= firstSlot)
                    .Select(p => p.RecordedAt)
                    .ToListAsync(ct))
                .Select(PriceSimulationRules.MinuteKey)
                .ToHashSet();

            var history = recent.Select(r => new PriceHistoryPoint(r.RecordedAt, r.Price)).ToList();
            var quotes = await _provider.GetPricesAsync(
                new PriceRequest(card.Id, card.ExternalRef, card.Overall, history, slots), ct);

            var snapshots = quotes
                .Where(q => !manualMinutes.Contains(PriceSimulationRules.MinuteKey(q.RecordedAt)))
                .Select(q => new PriceSnapshot
                {
                    Id = Guid.NewGuid(),
                    CardId = q.CardId,
                    Price = q.Price,
                    Source = source,
                    RecordedAt = DateTime.SpecifyKind(q.RecordedAt, DateTimeKind.Utc)
                })
                .ToList();

            if (snapshots.Count == 0) continue;

            context.PriceSnapshots.AddRange(snapshots);
            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear(); // a long backfill must not keep thousands of entities in memory
            total += snapshots.Count;
        }

        if (withoutHistory > 0)
            _logger.LogDebug("{Count} cards have no simulated history yet; run the seed script to create it.", withoutHistory);

        return total;
    }
}
