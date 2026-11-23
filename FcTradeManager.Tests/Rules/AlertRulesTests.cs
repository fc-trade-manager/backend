using FcTradeManager.Domain.Rules;

namespace FcTradeManager.Tests.Rules;

public class AlertRulesTests
{
    private const int Buy = 1000;
    private const int Sell = 2000;

    private static AlertEvaluation Eval(int price, bool buyTriggered = false, bool sellTriggered = false)
        => AlertRules.Evaluate(price, Buy, Sell, buyTriggered, sellTriggered);

    [Fact]
    public void RN06_price_between_targets_fires_nothing()
    {
        var r = Eval(1500);

        Assert.False(r.FireBuy);
        Assert.False(r.FireSell);
        Assert.False(r.BuyTriggered);
        Assert.False(r.SellTriggered);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1000)] // boundary: equal to the target counts
    [InlineData(200)]
    public void RN06_buy_fires_when_price_is_at_or_below_the_buy_target(int price)
    {
        var r = Eval(price);

        Assert.True(r.FireBuy);
        Assert.True(r.BuyTriggered);
        Assert.False(r.FireSell);
    }

    [Theory]
    [InlineData(2000)] // boundary
    [InlineData(2001)]
    [InlineData(15_000_000)]
    public void RN06_sell_fires_when_price_is_at_or_above_the_sell_target(int price)
    {
        var r = Eval(price);

        Assert.True(r.FireSell);
        Assert.True(r.SellTriggered);
        Assert.False(r.FireBuy);
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(1999)]
    public void RN06_just_inside_the_targets_does_not_fire(int price)
    {
        var r = Eval(price);

        Assert.False(r.FireBuy);
        Assert.False(r.FireSell);
    }

    [Fact]
    public void RN07_buy_does_not_fire_again_while_the_condition_still_holds()
    {
        var r = Eval(900, buyTriggered: true);

        Assert.False(r.FireBuy);
        Assert.True(r.BuyTriggered); // stays "triggered"
    }

    [Fact]
    public void RN07_sell_does_not_fire_again_while_the_condition_still_holds()
    {
        var r = Eval(2500, sellTriggered: true);

        Assert.False(r.FireSell);
        Assert.True(r.SellTriggered);
    }

    [Fact]
    public void RN07_buy_flag_is_cleared_when_the_price_comes_back()
    {
        var r = Eval(1500, buyTriggered: true);

        Assert.False(r.FireBuy);
        Assert.False(r.BuyTriggered); // re-armed
    }

    [Fact]
    public void RN07_sell_flag_is_cleared_when_the_price_comes_back()
    {
        var r = Eval(1500, sellTriggered: true);

        Assert.False(r.FireSell);
        Assert.False(r.SellTriggered);
    }

    [Fact]
    public void RN07_after_re_arming_a_new_crossing_fires_again()
    {
        // 900 (fires) -> 1500 (re-arms) -> 900 (fires again)
        var first = Eval(900);
        var back = Eval(1500, buyTriggered: first.BuyTriggered);
        var second = Eval(900, buyTriggered: back.BuyTriggered);

        Assert.True(first.FireBuy);
        Assert.False(back.FireBuy);
        Assert.True(second.FireBuy);
    }

    [Fact]
    public void RN07_buy_and_sell_flags_are_independent()
    {
        var r = Eval(900, buyTriggered: false, sellTriggered: true);

        Assert.True(r.FireBuy);
        Assert.False(r.SellTriggered); // the sell flag is cleared because 900 < sell target
    }
}
