using System.Globalization;
using PpEvidencija.Models;
using PpEvidencija.Services;
using Xunit;

namespace PpEvidencija.Tests;

public sealed class InputValidationTests
{
    [Theory]
    [InlineData("1,5", "1.5")]
    [InlineData("1.5", "1.5")]
    [InlineData("6,5", "6.5")]
    [InlineData("6.5", "6.5")]
    [InlineData("0,01", "0.01")]
    [InlineData("0.01", "0.01")]
    [InlineData(" 999999,99 ", "999999.99")]
    [InlineData("1.2300", "1.23")]
    [InlineData("1,000", "1")]
    [InlineData("6", "6")]
    public void DecimalInputHasTheSameValueInEverySupportedCulture(string text, string expectedText)
    {
        var expected = decimal.Parse(expectedText, CultureInfo.InvariantCulture);
        WithCultures(() =>
        {
            Assert.True(PpInputParser.TryParsePositiveDecimal(text, out var actual));
            Assert.Equal(expected, actual);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("-1.5")]
    [InlineData("+1.5")]
    [InlineData("1,2,3")]
    [InlineData("1.2.3")]
    [InlineData("1,000.50")]
    [InlineData("1.000,50")]
    [InlineData("1 000")]
    [InlineData("1\u00A0000")]
    [InlineData("1e2")]
    [InlineData("1.")]
    [InlineData(",5")]
    [InlineData("1.234")]
    [InlineData("0,005")]
    [InlineData("1.000000000000000000000000000001")]
    [InlineData("79228162514264337593543950336")]
    public void InvalidOrInexactDecimalInputIsRejectedWithoutReturningAPartialValue(string? text)
    {
        WithCultures(() =>
        {
            Assert.False(PpInputParser.TryParsePositiveDecimal(text, out var actual));
            Assert.Equal(0m, actual);
        });
    }

    [Theory]
    [InlineData("0.001")]
    [InlineData("1.234")]
    [InlineData("999999.991")]
    [InlineData("1000000")]
    [InlineData("0")]
    [InlineData("-1")]
    public void ValidatorRejectsQuantitiesThatCannotBeStoredExactly(string quantity)
    {
        var input = ValidInput() with
        {
            PunjenjeKg = decimal.Parse(quantity, CultureInfo.InvariantCulture)
        };

        Assert.Contains(new PpAparatValidator().Validate(input), error => error.StartsWith("Punjenje"));
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("1.2300")]
    [InlineData("999999.99")]
    public void ValidatorAcceptsExactlyRepresentableQuantities(string quantity)
    {
        var input = ValidInput() with
        {
            PunjenjeKg = decimal.Parse(quantity, CultureInfo.InvariantCulture)
        };

        Assert.Empty(new PpAparatValidator().Validate(input));
    }

    [Theory]
    [InlineData(1899, 12, 31)]
    [InlineData(9999, 7, 1)]
    [InlineData(9999, 12, 31)]
    public void ValidatorRejectsDatesOutsideTheReportAndNextServiceRange(int year, int month, int day)
    {
        var date = new DateTime(year, month, day);
        var input = ValidInput() with { DatumServisa = date };

        Assert.False(ServiceDateRange.Contains(date));
        Assert.Contains(new PpAparatValidator().Validate(input), error => error.StartsWith("Datum servisa"));
    }

    [Theory]
    [InlineData(1900, 1, 1, 1900, 7, 1)]
    [InlineData(2024, 8, 31, 2025, 2, 28)]
    [InlineData(9999, 6, 30, 9999, 12, 30)]
    public void AllowedBoundaryDatesCanCalculateTheNextService(
        int year, int month, int day, int nextYear, int nextMonth, int nextDay)
    {
        var date = new DateTime(year, month, day, 23, 59, 59);
        var input = ValidInput() with { DatumServisa = date };

        Assert.True(ServiceDateRange.Contains(date));
        Assert.Empty(new PpAparatValidator().Validate(input));
        Assert.Equal(new DateTime(nextYear, nextMonth, nextDay), input.SljedeciServis);
    }

    [Fact]
    public void MissingServiceDateIsRejected()
    {
        var input = ValidInput() with { DatumServisa = default };

        Assert.False(ServiceDateRange.Contains(input.DatumServisa));
        Assert.Contains("Datum servisa je obavezan.", new PpAparatValidator().Validate(input));
    }

    private static PpAparatInput ValidInput() => new(
        "211000001", "S 6", 6m, "SERIAL-001", 2020,
        new DateTime(2026, 9, 14), "Ispravan", "N/A", "Tester");

    private static void WithCultures(Action assertion)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "bs-Latn-BA", "hr-HR", "pl-PL" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                assertion();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
