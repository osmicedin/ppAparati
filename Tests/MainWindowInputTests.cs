using System.Globalization;
using System.Windows.Controls;
using PpEvidencija.Models;

namespace PpEvidencija.Tests;

[Collection("WPF")]
public sealed class MainWindowInputTests
{
    [Fact]
    public Task StartupUsesLastSavedInspectorForDifferentLoggedInUsers() => WpfTestHost.RunAsync(async () =>
    {
        var aparati = new FakeAparatRepository { LastInspector = "  Sačuvani Ispitivač  " };
        using var first = new WindowTestContext(new("first", "first", "Prvi", "Korisnik"), aparati);
        using var second = new WindowTestContext(new("second", "second", "Drugi", "Korisnik"), aparati);

        Assert.Empty(first.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
        Assert.Empty(second.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
        await first.Window.UcitajPocetnePodatkeAsync();
        await second.Window.UcitajPocetnePodatkeAsync();

        Assert.Equal("Sačuvani Ispitivač", first.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
        Assert.Equal("Sačuvani Ispitivač", second.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
        Assert.Equal("Korisnik: first", first.Control<TextBlock>("txtPrijavljeniKorisnik").Text);
        Assert.Equal("Korisnik: second", second.Control<TextBlock>("txtPrijavljeniKorisnik").Text);
    });

    [Fact]
    public Task SuccessfulSaveRetainsInspectorThroughClearAndNextLogin() => WpfTestHost.RunAsync(async () =>
    {
        var aparati = new FakeAparatRepository { LastInspector = "Prethodni Ispitivač" };
        using (var context = new WindowTestContext(aparati: aparati))
        {
            await context.Window.UcitajPocetnePodatkeAsync();
            FillValidInput(context);
            context.Control<TextBox>("txtIspitivanjeIzvrsio").Text = "  Novi Ispitivač  ";
            await context.Window.SnimiAparatAsync();

            var saved = Assert.Single(aparati.Inserts);
            Assert.Equal("Novi Ispitivač", saved.Input.IspitivanjeIzvrsio);
            Assert.Equal("tester", saved.User);
            Assert.Equal("Novi Ispitivač", context.Control<TextBox>("txtIspitivanjeIzvrsio").Text);

            context.Control<TextBox>("txtIspitivanjeIzvrsio").Text = "Nespremljena izmjena";
            context.Window.OcistiPoljaUnosa(zadrziKonto: true);
            Assert.Equal("Novi Ispitivač", context.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
        }

        using var nextLogin = new WindowTestContext(new("other", "other", "Drugi", "Korisnik"), aparati);
        await nextLogin.Window.UcitajPocetnePodatkeAsync();
        Assert.Equal("Novi Ispitivač", nextLogin.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
    });

    [Fact]
    public Task FailedSaveKeepsEnteredInspectorForRetryButClearAndRestartUseLastSuccess() => WpfTestHost.RunAsync(async () =>
    {
        var aparati = new FakeAparatRepository
        {
            LastInspector = "Sačuvani Ispitivač",
            Insert = () => Task.FromException<long>(new IOException("Simulirana greška snimanja."))
        };
        using (var context = new WindowTestContext(aparati: aparati))
        {
            await context.Window.UcitajPocetnePodatkeAsync();
            FillValidInput(context);
            context.Control<TextBox>("txtIspitivanjeIzvrsio").Text = "Nespremljeni Ispitivač";
            await context.Window.SnimiAparatAsync();

            Assert.Contains("Snimanje nije uspjelo", context.Control<TextBlock>("txtUnosStatus").Text);
            Assert.Equal("Nespremljeni Ispitivač", context.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
            Assert.Equal("Sačuvani Ispitivač", aparati.LastInspector);
            context.Window.OcistiPoljaUnosa(zadrziKonto: false);
            Assert.Equal("Sačuvani Ispitivač", context.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
        }

        using var nextLogin = new WindowTestContext(new("other", "other", "Drugi", "Korisnik"), aparati);
        await nextLogin.Window.UcitajPocetnePodatkeAsync();
        Assert.Equal("Sačuvani Ispitivač", nextLogin.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
    });

    [Fact]
    public Task ValidationFailureDoesNotReplaceLastSavedInspector() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext(aparati: new FakeAparatRepository { LastInspector = "Sačuvani Ispitivač" });
        await context.Window.UcitajPocetnePodatkeAsync();
        FillValidInput(context);
        context.Control<TextBox>("txtIspitivanjeIzvrsio").Text = "Nespremljeni Ispitivač";
        context.Control<TextBox>("txtSerijskiBroj").Clear();
        await context.Window.SnimiAparatAsync();

        Assert.Empty(context.Aparati.Inserts);
        Assert.Equal("Nespremljeni Ispitivač", context.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
        context.Window.OcistiPoljaUnosa(zadrziKonto: true);
        Assert.Equal("Sačuvani Ispitivač", context.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
    });

    [Fact]
    public Task EmptyDatabaseLeavesInspectorBlankAndRequiresExplicitEntry() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        await context.Window.UcitajPocetnePodatkeAsync();
        FillValidInput(context);
        Assert.Empty(context.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
        await context.Window.SnimiAparatAsync();

        Assert.Empty(context.Aparati.Inserts);
        Assert.Contains("Ispitivanje izvršio", context.Control<TextBlock>("txtUnosStatus").Text);
        context.Window.OcistiPoljaUnosa(zadrziKonto: true);
        Assert.Empty(context.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
    });

    [Fact]
    public Task FailedInitialInspectorReadDoesNotSubstituteLoggedInUser() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext(aparati: new FakeAparatRepository
        {
            ReadLastInspector = _ => Task.FromException<string?>(new IOException("Simulirana greška čitanja."))
        });
        await context.Window.UcitajPocetnePodatkeAsync();

        Assert.Empty(context.Control<TextBox>("txtIspitivanjeIzvrsio").Text);
        Assert.True(context.Window.IsEnabled);
        Assert.Single(context.Dialogs.Errors);
    });

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

    private static void FillValidInput(WindowTestContext context)
    {
        var account = new Konto("21100001", "Test kupac");
        var accountControl = context.Control<ComboBox>("cmbKontoUnos");
        accountControl.ItemsSource = new[] { account };
        accountControl.SelectedItem = account;
        accountControl.Text = account.Prikaz;
        context.Control<ComboBox>("cmbTip").SelectedItem = "S 6";
        context.Control<TextBox>("txtPunjenje").Text = "6";
        context.Control<TextBox>("txtSerijskiBroj").Text = "TEST-001";
        context.Control<TextBox>("txtGodinaProizvodnje").Text = "2020";
        context.Control<TextBox>("txtVozilo").Text = "N/A";
        context.Control<DatePicker>("dpDatumServisa").SelectedDate = new DateTime(2026, 10, 1);
    }

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
