using FcTradeManager.Infrastructure.Services;
using FcTradeManager.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FcTradeManager.Tests.Services;

public class CardSeederServiceTests : IDisposable
{
    private readonly string _csv = Path.Combine(Path.GetTempPath(), $"cards-{Guid.NewGuid():N}.csv");

    public void Dispose()
    {
        if (File.Exists(_csv)) File.Delete(_csv);
    }

    private static CardSeederService Svc(TestDb db) => new(db.Context, NullLogger<CardSeederService>.Instance);

    private void WriteCsv(params string[] lines) => File.WriteAllLines(_csv, lines);

    [Fact]
    public async Task Imports_the_cards_skipping_the_header()
    {
        using var db = new TestDb();
        WriteCsv(
            "Name,Overall,Position,Club,Rarity,ExternalRef",
            "Pelé,98,CAM,Icons,Icon,cards-001",
            " Kylian Mbappé ,91,ST,Real Madrid,Gold Rare,cards-030");

        await Svc(db).SeedCardsAsync(_csv);

        var cards = db.NewContext().Cards.OrderBy(c => c.ExternalRef).ToList();
        Assert.Equal(2, cards.Count);
        Assert.Equal("Pelé", cards[0].Name);
        Assert.Equal(98, cards[0].Overall);
        Assert.Equal("CAM", cards[0].Position);
        Assert.Equal("Icons", cards[0].Club);
        Assert.Equal("Icon", cards[0].Rarity);
        Assert.Equal("cards-001", cards[0].ExternalRef);
        Assert.Equal("Kylian Mbappé", cards[1].Name); // trimmed
    }

    [Fact]
    public async Task Skips_blank_lines_and_lines_with_fewer_than_6_columns()
    {
        using var db = new TestDb();
        WriteCsv(
            "Name,Overall,Position,Club,Rarity,ExternalRef",
            "Pelé,98,CAM,Icons,Icon,cards-001",
            "",
            "   ",
            "Broken,90,ST",
            "Maradona,97,CAM,Icons,Icon,cards-002");

        await Svc(db).SeedCardsAsync(_csv);

        Assert.Equal(2, db.NewContext().Cards.Count());
    }

    [Fact]
    public async Task Does_not_import_again_when_the_table_already_has_cards()
    {
        using var db = new TestDb();
        WriteCsv("Name,Overall,Position,Club,Rarity,ExternalRef", "Pelé,98,CAM,Icons,Icon,cards-001");

        await Svc(db).SeedCardsAsync(_csv);
        await Svc(db).SeedCardsAsync(_csv); // second start of the app

        Assert.Equal(1, db.NewContext().Cards.Count());
    }

    [Fact]
    public async Task Does_not_touch_a_table_that_already_has_cards()
    {
        using var db = new TestDb();
        Seed.AddCard(db.Context, "Existing");
        WriteCsv("Name,Overall,Position,Club,Rarity,ExternalRef", "Pelé,98,CAM,Icons,Icon,cards-001");

        await Svc(db).SeedCardsAsync(_csv);

        Assert.Equal("Existing", db.NewContext().Cards.Single().Name);
    }

    [Fact]
    public async Task A_missing_file_leaves_the_catalog_empty_without_failing()
    {
        using var db = new TestDb();

        await Svc(db).SeedCardsAsync(Path.Combine(Path.GetTempPath(), "does-not-exist.csv"));

        Assert.Empty(db.NewContext().Cards);
    }

    [Fact]
    public async Task The_real_catalog_file_is_valid_and_has_unique_external_refs()
    {
        // guards the data the demo ships with (and the price script relies on ExternalRef being unique)
        var path = FindCatalog();
        using var db = new TestDb();

        await Svc(db).SeedCardsAsync(path);

        var cards = db.NewContext().Cards.ToList();
        Assert.NotEmpty(cards);
        Assert.Equal(cards.Count, cards.Select(c => c.ExternalRef).Distinct().Count());
        Assert.All(cards, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.InRange(c.Overall, 1, 99);
            Assert.False(string.IsNullOrWhiteSpace(c.ExternalRef));
        });
    }

    private static string FindCatalog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "FcTradeManager.API", "Data", "cards.csv");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("FcTradeManager.API/Data/cards.csv not found above " + AppContext.BaseDirectory);
    }
}
