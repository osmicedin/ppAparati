using System.Globalization;
using System.Windows.Controls;
using PpEvidencija.Models;

namespace PpEvidencija.Tests;

[Collection("WPF")]
public sealed class MainWindowInputTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("bs-Latn-BA")]
    [InlineData("pl-PL")]
    public Task ServiceDateUsesBosnianDayMonthOrderInEveryOperatingSystemCulture(string culture) =>
        WpfTestHost.Run(() => WithCulture(culture, () =>
        {
            using var context = new WindowTestContext();
            var picker = context.Control<DatePicker>("dpDatumServisa");

            picker.Text = "09.08.2026";

            Assert.Equal(new DateTime(2026, 8, 9), picker.SelectedDate);
            Assert.Equal("09.02.2027", context.Control<TextBlock>("txtSljedeciServis").Text);
        }));

    [Fact]
    public Task BosnianDateWithDayAboveTwelveReplacesThePreviousDate() =>
        WpfTestHost.Run(() => WithCulture("en-US", () =>
        {
            using var context = new WindowTestContext();
            var picker = context.Control<DatePicker>("dpDatumServisa");
            picker.SelectedDate = new DateTime(2026, 9, 14);

            picker.Text = "14.08.2026";

            Assert.Equal(new DateTime(2026, 8, 14), picker.SelectedDate);
            Assert.Equal("14.02.2027", context.Control<TextBlock>("txtSljedeciServis").Text);
        }));

    [Theory]
    [InlineData("31.02.2026")]
    [InlineData("14.13.2026")]
    [InlineData("nije datum")]
    [InlineData("31.12.1899")]
    [InlineData("01.07.9999")]
    [InlineData("31.12.9999")]
    public Task InvalidServiceDateClearsTheOldDateAndShowsValidationInsteadOfThrowing(string text) =>
        WpfTestHost.Run(() => WithCulture("en-US", () =>
        {
            using var context = new WindowTestContext();
            var picker = context.Control<DatePicker>("dpDatumServisa");
            picker.SelectedDate = new DateTime(2026, 9, 14);

            picker.Text = text;

            Assert.Null(picker.SelectedDate);
            Assert.Empty(picker.Text);
            Assert.Empty(context.Control<TextBlock>("txtSljedeciServis").Text);
            Assert.Contains("ispravan datum servisa", context.Control<TextBlock>("txtUnosStatus").Text);
        }));

    [Theory]
    [InlineData("01.01.1900", 1900, 1, 1, "01.07.1900")]
    [InlineData("30.06.9999", 9999, 6, 30, "30.12.9999")]
    public Task ServiceDateBoundaryValuesAreAcceptedAndNextServiceCanBeCalculated(
        string text, int year, int month, int day, string nextService) =>
        WpfTestHost.Run(() => WithCulture("en-US", () =>
        {
            using var context = new WindowTestContext();
            var picker = context.Control<DatePicker>("dpDatumServisa");

            picker.Text = text;

            Assert.Equal(ServiceDateRange.Minimum, picker.DisplayDateStart);
            Assert.Equal(ServiceDateRange.Maximum, picker.DisplayDateEnd);
            Assert.Equal(new DateTime(year, month, day), picker.SelectedDate);
            Assert.Equal(nextService, context.Control<TextBlock>("txtSljedeciServis").Text);
        }));

    [Theory]
    [InlineData("31.02.2026")]
    [InlineData("31.12.1899")]
    public Task InvalidReportDateDoesNotLeaveThePreviousDateSelected(string text) =>
        WpfTestHost.Run(() => WithCulture("en-US", () =>
        {
            using var context = new WindowTestContext();
            var picker = context.Control<DatePicker>("dpDatumZakljucivanja");
            picker.SelectedDate = new DateTime(2026, 9, 30);

            picker.Text = text;

            Assert.Null(picker.SelectedDate);
            Assert.Empty(picker.Text);
            Assert.Contains("ispravan datum dokumenta", context.Control<TextBlock>("txtIzvjestajStatus").Text);
        }));

    private static void WithCulture(string name, Action action)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
