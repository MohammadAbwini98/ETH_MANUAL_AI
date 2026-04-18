using EthSignal.Domain.Models;
using EthSignal.Domain.Validation;
using FluentAssertions;

namespace EthSignal.Tests.Domain;

/// <summary>P1-T3: OHLC validity.</summary>
public class CandleValidatorTests
{
    private static RichCandle MakeValid() => new()
    {
        OpenTime = DateTimeOffset.UtcNow,
        BidOpen = 100m, BidHigh = 105m, BidLow = 95m, BidClose = 102m,
        AskOpen = 101m, AskHigh = 106m, AskLow = 96m, AskClose = 103m,
        Volume = 500m
    };

    [Fact]
    public void Valid_Candle_PassesAllChecks()
    {
        var result = CandleValidator.Validate(MakeValid());
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void High_LessThan_Low_Fails()
    {
        var c = MakeValid() with { BidHigh = 90m }; // BidHigh < BidLow (95)
        var result = CandleValidator.Validate(c);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Bid") && e.Contains("High"));
    }

    [Fact]
    public void High_LessThan_Open_Fails()
    {
        var c = MakeValid() with { AskHigh = 99m }; // AskHigh (99) < AskOpen (101)
        var result = CandleValidator.Validate(c);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Ask") && e.Contains("High"));
    }

    [Fact]
    public void Low_GreaterThan_Close_Fails()
    {
        var c = MakeValid() with { BidLow = 110m }; // BidLow (110) > BidClose (102)
        var result = CandleValidator.Validate(c);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Negative_Volume_Fails()
    {
        var c = MakeValid() with { Volume = -1m };
        var result = CandleValidator.Validate(c);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Volume"));
    }

    [Fact]
    public void Negative_Spread_Fails()
    {
        var c = MakeValid() with { AskOpen = 99m }; // Ask (99) < Bid (100) = negative spread
        var result = CandleValidator.Validate(c);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("spread"));
    }

    [Fact]
    public void AllOhlcEqual_IsValid()
    {
        var c = new RichCandle
        {
            OpenTime = DateTimeOffset.UtcNow,
            BidOpen = 100m, BidHigh = 100m, BidLow = 100m, BidClose = 100m,
            AskOpen = 101m, AskHigh = 101m, AskLow = 101m, AskClose = 101m,
            Volume = 0m
        };
        CandleValidator.Validate(c).IsValid.Should().BeTrue();
    }
}
