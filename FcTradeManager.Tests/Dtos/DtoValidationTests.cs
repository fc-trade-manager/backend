using System.ComponentModel.DataAnnotations;
using FcTradeManager.Application.DTOs;
using FcTradeManager.Domain.Enums;

namespace FcTradeManager.Tests.Dtos;

// data annotation 
public class DtoValidationTests
{
    private static bool IsValid(object dto) =>
        Validator.TryValidateObject(dto, new ValidationContext(dto), new List<ValidationResult>(), validateAllProperties: true);

    // PRICE LIMITATION

    [Theory]
    [InlineData(199, false)]
    [InlineData(200, true)]
    [InlineData(15_000_000, true)]
    [InlineData(15_000_001, false)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    public void RN08_price_snapshot_price_range(int price, bool valid) =>
        Assert.Equal(valid, IsValid(new CreatePriceSnapshotDto { Price = price }));

    [Theory]
    [InlineData(199, false)]
    [InlineData(200, true)]
    [InlineData(15_000_000, true)]
    [InlineData(15_000_001, false)]
    public void RN08_trade_price_range(int price, bool valid) =>
        Assert.Equal(valid, IsValid(Trade(price: price)));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    [InlineData(-1, false)]
    public void RN08_trade_quantity_range(int quantity, bool valid) =>
        Assert.Equal(valid, IsValid(Trade(quantity: quantity)));

    [Fact]
    public void A_trade_needs_a_defined_type()
    {
        var dto = Trade();
        dto.Type = (TradeType)99;

        Assert.False(IsValid(dto));
    }

    [Theory]
    [InlineData(199, 300, false)]
    [InlineData(200, 300, true)]
    [InlineData(100, 15_000_001, false)]
    public void RN08_watchlist_create_target_ranges(int buy, int sell, bool valid) =>
        Assert.Equal(valid, IsValid(new CreateWatchlistDto { CardId = Guid.NewGuid(), TargetBuyPrice = buy, TargetSellPrice = sell }));

    [Theory]
    [InlineData(199, 300, false)]
    [InlineData(200, 15_000_000, true)]
    [InlineData(300, 15_000_001, false)]
    public void RN08_watchlist_update_target_ranges(int buy, int sell, bool valid) =>
        Assert.Equal(valid, IsValid(new UpdateWatchlistDto { BuyTarget = buy, SellTarget = sell }));

    // REGISTER

    private static RegisterDto Register(string email = "me@test.com", string password = "12345678", string username = "trader_01", string display = "Trader") =>
        new() { Email = email, Password = password, Username = username, DisplayName = display };

    [Fact]
    public void A_valid_registration_passes() => Assert.True(IsValid(Register()));

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a@")]
    public void Registration_needs_a_valid_email(string email) => Assert.False(IsValid(Register(email: email)));

    [Theory]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    public void Registration_password_needs_8_characters(string password, bool valid) =>
        Assert.Equal(valid, IsValid(Register(password: password)));

    // CHANGE WHEN: bug #24 is fixed
    [Theory]
    [InlineData("ab", false)]
    [InlineData("abc", true)]
    [InlineData("a_b_c_d_e_f_g_h_i_", true)]   // 18
    [InlineData("a_b_c_d_e_f_g_h_i_j", false)] // 19
    [InlineData("with space", false)]
    [InlineData("acentuação", false)]
    [InlineData("dash-name", false)]
    [InlineData("UPPER_ok_1", true)]
    public void Registration_username_rules(string username, bool valid) =>
        Assert.Equal(valid, IsValid(Register(username: username)));

    [Theory]
    [InlineData("ab", false)]
    [InlineData("abc", true)]
    [InlineData("A display name with 25 ch", true)] // 25
    [InlineData("A display name with 26 chr", false)] // 26
    public void Registration_display_name_needs_3_to_25_characters(string display, bool valid) =>
        Assert.Equal(valid, IsValid(Register(display: display)));

    [Fact]
    public void Login_needs_email_and_password()
    {
        Assert.True(IsValid(new LoginDto { Email = "me@test.com", Password = "x" }));
        Assert.False(IsValid(new LoginDto { Email = "me@test.com", Password = "" }));
        Assert.False(IsValid(new LoginDto { Email = "bad", Password = "x" }));
    }

    private static CreateTradeDto Trade(int price = 1000, int quantity = 1) =>
        new() { CardId = Guid.NewGuid(), Type = TradeType.Buy, Price = price, Quantity = quantity };
}
