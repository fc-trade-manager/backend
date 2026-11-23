using FcTradeManager.Domain.Entities;
using FcTradeManager.Domain.Enums;
using FcTradeManager.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace FcTradeManager.Tests.Data;

// the model rules that live in AppDbContext
public class DbConstraintsTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Email_must_be_unique()
    {
        using var db = new TestDb();
        Seed.AddUser(db.Context, "same@test.com");

        Assert.Throws<DbUpdateException>(() => Seed.AddUser(db.Context, "same@test.com"));
    }

    [Fact]
    public void Username_must_be_unique()
    {
        using var db = new TestDb();
        Seed.AddUser(db.Context, "a@test.com", "same_name");

        Assert.Throws<DbUpdateException>(() => Seed.AddUser(db.Context, "b@test.com", "same_name"));
    }

    [Fact]
    public void A_card_can_only_be_in_a_users_watchlist_once()
    {
        using var db = new TestDb();
        var user = Seed.AddUser(db.Context);
        var card = Seed.AddCard(db.Context);
        Seed.AddWatch(db.Context, user.Id, card.Id, 1000, 2000);

        Assert.Throws<DbUpdateException>(() => Seed.AddWatch(db.Context, user.Id, card.Id, 1500, 2500));
    }

    [Fact]
    public void Refresh_token_hash_must_be_unique()
    {
        using var db = new TestDb();
        var user = Seed.AddUser(db.Context);
        db.Context.RefreshTokens.Add(new RefreshToken { Id = Guid.NewGuid(), UserId = user.Id, TokenHash = "abc", ExpiresAt = Now });
        db.Context.SaveChanges();
        db.Context.RefreshTokens.Add(new RefreshToken { Id = Guid.NewGuid(), UserId = user.Id, TokenHash = "abc", ExpiresAt = Now });

        Assert.Throws<DbUpdateException>(() => db.Context.SaveChanges());
    }

    [Fact]
    public void Enums_are_stored_as_lowercase_text()
    {
        using var db = new TestDb();
        var user = Seed.AddUser(db.Context);
        var card = Seed.AddCard(db.Context);
        Seed.AddTrade(db.Context, user.Id, card.Id, TradeType.Buy, 1000, 1, Now);
        Seed.AddTrade(db.Context, user.Id, card.Id, TradeType.Sell, 2000, 1, Now.AddHours(1));
        Seed.AddAlert(db.Context, user.Id, card.Id, AlertKind.Buy, 900, Now);
        Seed.AddSnapshot(db.Context, card.Id, 1000, Now, PriceSource.Simulated);

        var tradeTypes = db.Context.Database.SqlQueryRaw<string>("SELECT \"Type\" AS \"Value\" FROM \"Trades\" ORDER BY \"ExecutedAt\"").ToList();
        var alertKinds = db.Context.Database.SqlQueryRaw<string>("SELECT \"Kind\" AS \"Value\" FROM \"Alerts\"").ToList();
        var sources = db.Context.Database.SqlQueryRaw<string>("SELECT \"Source\" AS \"Value\" FROM \"PriceSnapshots\"").ToList();

        Assert.Equal(new[] { "buy", "sell" }, tradeTypes);
        Assert.Equal(new[] { "buy" }, alertKinds);
        Assert.Equal(new[] { "simulated" }, sources);
    }

    [Fact]
    public void Enums_are_read_back_from_the_lowercase_text()
    {
        using var db = new TestDb();
        var user = Seed.AddUser(db.Context);
        var card = Seed.AddCard(db.Context);
        Seed.AddTrade(db.Context, user.Id, card.Id, TradeType.Sell, 2000, 1, Now);

        var trade = db.NewContext().Trades.Single();

        Assert.Equal(TradeType.Sell, trade.Type);
    }
}
