using FcTradeManager.Domain.Enums;
using FcTradeManager.Infrastructure.Services;
using FcTradeManager.Tests.Support;

namespace FcTradeManager.Tests.Services;

public class AlertServiceTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task List_returns_only_the_users_alerts_newest_first()
    {
        using var db = new TestDb();
        var user = Seed.AddUser(db.Context);
        var other = Seed.AddUser(db.Context, "other@test.com");
        var card = Seed.AddCard(db.Context, "Pelé");
        Seed.AddAlert(db.Context, user.Id, card.Id, AlertKind.Buy, 900, Now.AddMinutes(-10));
        Seed.AddAlert(db.Context, user.Id, card.Id, AlertKind.Sell, 2100, Now);
        Seed.AddAlert(db.Context, other.Id, card.Id, AlertKind.Buy, 800, Now.AddMinutes(-5));

        var list = (await new AlertService(db.Context).GetUserAlertsAsync(user.Id)).ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal(AlertKind.Sell, list[0].Kind);
        Assert.Equal(2100, list[0].TriggeredPrice);
        Assert.Equal("Pelé", list[0].CardName);
        Assert.Equal(AlertKind.Buy, list[1].Kind);
    }

    [Fact]
    public async Task Mark_as_read_sets_the_read_date()
    {
        using var db = new TestDb();
        var user = Seed.AddUser(db.Context);
        var card = Seed.AddCard(db.Context);
        var alert = Seed.AddAlert(db.Context, user.Id, card.Id, AlertKind.Buy, 900, Now);

        await new AlertService(db.Context).MarkAlertAsReadAsync(user.Id, alert.Id);

        Assert.NotNull(db.NewContext().Alerts.Single().ReadAt);
    }

    [Fact]
    public async Task Mark_as_read_twice_keeps_the_first_read_date()
    {
        using var db = new TestDb();
        var user = Seed.AddUser(db.Context);
        var card = Seed.AddCard(db.Context);
        var firstRead = Now.AddHours(-1);
        var alert = Seed.AddAlert(db.Context, user.Id, card.Id, AlertKind.Buy, 900, Now.AddHours(-2), readAt: firstRead);

        await new AlertService(db.Context).MarkAlertAsReadAsync(user.Id, alert.Id);

        Assert.Equal(firstRead, db.NewContext().Alerts.Single().ReadAt);
    }

    [Fact]
    public async Task Mark_as_read_of_another_users_alert_is_not_found_and_leaves_it_unread()
    {
        using var db = new TestDb();
        var user = Seed.AddUser(db.Context);
        var other = Seed.AddUser(db.Context, "other@test.com");
        var card = Seed.AddCard(db.Context);
        var theirs = Seed.AddAlert(db.Context, other.Id, card.Id, AlertKind.Buy, 900, Now);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            new AlertService(db.Context).MarkAlertAsReadAsync(user.Id, theirs.Id));

        Assert.Null(db.NewContext().Alerts.Single().ReadAt);
    }

    [Fact]
    public async Task Mark_as_read_of_an_unknown_alert_is_not_found()
    {
        using var db = new TestDb();
        var user = Seed.AddUser(db.Context);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            new AlertService(db.Context).MarkAlertAsReadAsync(user.Id, Guid.NewGuid()));
    }
}
