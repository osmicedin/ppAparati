using System.Windows.Controls;
using System.Windows.Threading;
using PpEvidencija.Models;

namespace PpEvidencija.Tests;

[Collection("WPF")]
public sealed class MainWindowReportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DocumentDefaultsToLatestServiceDateRegardlessOfInsertionOrder(bool closed) => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        Assert.Null(context.Control<DatePicker>("dpDatumZakljucivanja").SelectedDate);
        var group = WindowTestContext.Row(closed: closed);
        var latestServiceDate = new DateTime(2026, 9, 20);
        var snapshot = new IzvjestajSnimak(group,
        [
            DatedRecord(group, 25, new DateTime(2025, 12, 31)),
            DatedRecord(group, 11, latestServiceDate)
        ]);
        context.Aparati.Read = (_, _, _) => Task.FromResult(snapshot);
        context.Select(group);

        Assert.Equal(latestServiceDate, context.Control<DatePicker>("dpDatumZakljucivanja").SelectedDate);
        await context.Window.GenerisiDocxAsync();

        Assert.Equal(latestServiceDate, Assert.Single(context.Docx.Requests).DatumZakljucivanja);
        Assert.Empty(context.Dialogs.Errors);
    });

    [Fact]
    public Task ManuallyChangedDocumentDateIsUsedForExport() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        context.Select(WindowTestContext.Row());
        var chosenDate = new DateTime(2026, 11, 30);
        context.Control<DatePicker>("dpDatumZakljucivanja").SelectedDate = chosenDate;

        await context.Window.GenerisiDocxAsync();

        Assert.Equal(chosenDate, Assert.Single(context.Docx.Requests).DatumZakljucivanja);
    });

    [Fact]
    public Task HistorySelectionGetsItsOwnLatestServiceDateAndClearingSelectionClearsDate() => WpfTestHost.Run(() =>
    {
        using var context = new WindowTestContext();
        var current = WindowTestContext.Row(grupaId: 10);
        var historical = WindowTestContext.Row(grupaId: 5, closed: true);
        var currentDate = new DateTime(2026, 9, 20);
        var historicalDate = new DateTime(2025, 12, 31);
        context.Reports.History = (_, _) => Task.FromResult<IReadOnlyList<IzvjestajPregledRed>>([current, historical]);
        context.Aparati.Read = (id, _, _) => Task.FromResult(id == current.GrupaId
            ? new IzvjestajSnimak(current, [DatedRecord(current, 30, currentDate)])
            : new IzvjestajSnimak(historical, [DatedRecord(historical, 20, historicalDate)]));
        context.Select(current);
        var date = context.Control<DatePicker>("dpDatumZakljucivanja");
        Assert.Equal(currentDate, date.SelectedDate);
        date.SelectedDate = new DateTime(2026, 10, 10);

        context.Control<ComboBox>("cmbHistorijaIzvjestaja").SelectedItem = historical;

        Assert.Equal(historicalDate, date.SelectedDate);
        context.Control<DataGrid>("dgKontaIzvjestaji").SelectedItem = null;
        Assert.Null(date.SelectedDate);
    });

    [Fact]
    public Task OverviewHasOnlyStatusFilterAndNoMonthlyColumn() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        Assert.Null(context.Window.FindName("cmbMjesec"));
        Assert.Null(context.Window.FindName("cmbGodina"));
        Assert.DoesNotContain(context.Control<DataGrid>("dgKontaIzvjestaji").Columns, column => Equals(column.Header, "Period"));
        await context.Window.UcitajPregledKontaAsync();
        Assert.Equal(IzvjestajStatusFilter.Nezakljuceni, Assert.Single(context.Reports.Filters));
    });

    [Fact]
    public Task CloseUsesConfirmedGroupAndVersionEvenIfSelectionChanges() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var confirmed = WindowTestContext.Row(version: 7);
        context.Select(confirmed);
        context.Dialogs.Confirm = row =>
        {
            Assert.Same(confirmed, row);
            context.Select(WindowTestContext.Row("B"));
            Assert.False(context.Control<Button>("btnPromijeniStatus").IsEnabled);
            return true;
        };
        await context.Window.PromijeniStatusAsync();
        Assert.Equal(new StatusChange(1, 7, "tester"), Assert.Single(context.Reports.Changes));
    });

    [Fact]
    public Task ClosedGroupCannotBeReopenedButCanBeExported() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        context.Select(WindowTestContext.Row(closed: true));
        Assert.False(context.Control<Button>("btnPromijeniStatus").IsEnabled);
        Assert.Equal("Zaključi", context.Control<Button>("btnPromijeniStatus").Content);
        Assert.True(context.Control<Button>("btnGenerisiDocx").IsEnabled);
        await context.Window.PromijeniStatusAsync();
        await context.Window.GenerisiDocxAsync();
        Assert.Empty(context.Reports.Changes);
        Assert.Single(context.Docx.Requests);
    });

    [Fact]
    public Task CancelledConfirmationDoesNotChangeStatusAndRestoresButtons() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        context.Select(WindowTestContext.Row());
        context.Dialogs.Confirm = _ => false;
        await context.Window.PromijeniStatusAsync();
        Assert.Empty(context.Reports.Changes);
        Assert.True(context.Control<Button>("btnPromijeniStatus").IsEnabled);
        Assert.True(context.Control<Button>("btnGenerisiDocx").IsEnabled);
    });

    [Fact]
    public Task RefreshImmediatelyRemovesStaleSelectionAndDisablesActions() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        context.Select(WindowTestContext.Row());
        var pending = new TaskCompletionSource<IReadOnlyList<IzvjestajPregledRed>>();
        context.Reports.Overview = () => pending.Task;
        var refresh = context.Window.UcitajPregledKontaAsync();
        Assert.Null(context.Control<DataGrid>("dgKontaIzvjestaji").SelectedItem);
        Assert.False(context.Control<Button>("btnPromijeniStatus").IsEnabled);
        await context.Window.PromijeniStatusAsync();
        Assert.Empty(context.Reports.Changes);
        pending.SetResult([]);
        await refresh;
        Assert.True(context.Control<Button>("btnOsvjeziPregledKonta").IsEnabled);
    });

    [Fact]
    public Task LatestRefreshWinsEvenIfOldRepositoryIgnoresCancellation() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var old = new TaskCompletionSource<IReadOnlyList<IzvjestajPregledRed>>();
        context.Reports.Overview = () => old.Task;
        var oldRefresh = context.Window.UcitajPregledKontaAsync();
        var current = WindowTestContext.Row("B");
        context.Reports.Overview = () => Task.FromResult<IReadOnlyList<IzvjestajPregledRed>>([current]);
        await context.Window.UcitajPregledKontaAsync();
        old.SetResult([WindowTestContext.Row()]);
        await oldRefresh;
        Assert.Same(current, Assert.Single(context.Control<DataGrid>("dgKontaIzvjestaji").Items.Cast<IzvjestajPregledRed>()));
    });

    [Fact]
    public Task ExportKeepsOriginalSnapshotWhenSelectionChangesDuringReadAndSaveDialog() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var first = WindowTestContext.Row();
        context.Select(first);
        var pending = new TaskCompletionSource<IzvjestajSnimak>();
        context.Aparati.Read = (id, _, _) => id == first.GrupaId ? pending.Task : Task.FromResult(context.Aparati.Snapshots[id]);
        context.Dialogs.Choose = () =>
        {
            context.Select(WindowTestContext.Row("C"));
            return Path.Combine(Path.GetTempPath(), "report-A.docx");
        };
        var export = context.Window.GenerisiDocxAsync();
        context.Select(WindowTestContext.Row("B"));
        Assert.False(context.Control<Button>("btnGenerisiDocx").IsEnabled);
        await context.Window.GenerisiDocxAsync();
        pending.SetResult(WindowTestContext.Snapshot(first));
        await export;
        var request = Assert.Single(context.Docx.Requests);
        Assert.Equal("A", request.Kupac.Sifra);
        Assert.Equal(first.GrupaId, request.GrupaId);
        Assert.Equal("A-1-001", Assert.Single(request.Aparati).SerijskiBroj);
        Assert.Equal("C-3-001", VisibleSerial(context));
        Assert.True(context.Control<Button>("btnGenerisiDocx").IsEnabled);
        Assert.Empty(context.Reports.Changes);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LateDetailResultCannotReplaceAnotherCustomersRows(bool cancelRequest) => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var first = WindowTestContext.Row();
        context.Select(first);
        var pending = new TaskCompletionSource<IzvjestajSnimak>();
        context.Aparati.Read = (id, _, _) => id == first.GrupaId ? pending.Task : Task.FromResult(context.Aparati.Snapshots[id]);
        using var cancellation = new CancellationTokenSource();
        var oldLoad = context.Window.UcitajIzvjestajAsync(first, cancellation.Token);
        context.Select(WindowTestContext.Row("B"));
        if (cancelRequest) cancellation.Cancel();
        pending.SetResult(WindowTestContext.Snapshot(first));
        if (cancelRequest) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldLoad);
        else await oldLoad;
        Assert.Equal("B-2-001", VisibleSerial(context));
    });

    [Fact]
    public Task LateHistoryCannotReplaceAnotherCustomersGroups() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var pending = new TaskCompletionSource<IReadOnlyList<IzvjestajPregledRed>>();
        context.Reports.History = (konto, _) => konto == "A" ? pending.Task : Task.FromResult(context.Reports.Groups[konto]);
        context.Select(WindowTestContext.Row());
        Assert.False(context.Control<Button>("btnGenerisiDocx").IsEnabled);
        context.Select(WindowTestContext.Row("B"));
        pending.SetResult([WindowTestContext.Row()]);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("B", ((IzvjestajPregledRed)context.Control<ComboBox>("cmbHistorijaIzvjestaja").SelectedItem).Konto);
        Assert.Equal("B-2-001", VisibleSerial(context));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReturningToSameAccountIgnoresItsPreviousHistoryCompletion(bool failPreviousLoad) => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var first = WindowTestContext.Row();
        var previousLoad = new TaskCompletionSource<IReadOnlyList<IzvjestajPregledRed>>();
        var currentLoad = new TaskCompletionSource<IReadOnlyList<IzvjestajPregledRed>>();
        var accountLoads = 0;
        context.Reports.History = (konto, _) => konto == first.Konto
            ? (++accountLoads == 1 ? previousLoad.Task : currentLoad.Task)
            : Task.FromResult(context.Reports.Groups[konto]);

        context.Select(first);
        context.Select(WindowTestContext.Row("B"));
        context.Select(first); // Reuse the exact object; account identity cannot distinguish the two loads.
        Assert.Equal(2, accountLoads);
        Assert.Same(first, context.Control<DataGrid>("dgKontaIzvjestaji").SelectedItem);
        var loadingStatus = context.Control<TextBlock>("txtIzvjestajStatus").Text;

        if (failPreviousLoad) previousLoad.SetException(new IOException("Previous history load failed"));
        else previousLoad.SetResult([first]);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Empty(context.Dialogs.Errors);
        Assert.Empty(context.Reports.Filters);
        Assert.Equal(loadingStatus, context.Control<TextBlock>("txtIzvjestajStatus").Text);
        Assert.Null(context.Control<ComboBox>("cmbHistorijaIzvjestaja").SelectedItem);
        Assert.False(context.Control<Button>("btnGenerisiDocx").IsEnabled);
        Assert.False(context.Control<Button>("btnPromijeniStatus").IsEnabled);

        currentLoad.SetResult([first]);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Same(first, context.Control<ComboBox>("cmbHistorijaIzvjestaja").SelectedItem);
        Assert.Equal("A-1-001", VisibleSerial(context));
        Assert.True(context.Control<Button>("btnGenerisiDocx").IsEnabled);
        Assert.True(context.Control<Button>("btnPromijeniStatus").IsEnabled);
    });

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("changed")]
    public Task ReturningToSameAccountIgnoresItsPreviousDetailCompletion(string previousOutcome) => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var first = WindowTestContext.Row();
        var previousLoad = new TaskCompletionSource<IzvjestajSnimak>();
        var currentLoad = new TaskCompletionSource<IzvjestajSnimak>();
        var groupLoads = 0;
        context.Aparati.Read = (id, _, _) => id == first.GrupaId
            ? (++groupLoads == 1 ? previousLoad.Task : currentLoad.Task)
            : Task.FromResult(context.Aparati.Snapshots[id]);

        context.Select(first);
        context.Select(WindowTestContext.Row("B"));
        context.Select(first);
        Assert.Equal(2, groupLoads);
        Assert.Same(first, context.Control<ComboBox>("cmbHistorijaIzvjestaja").SelectedItem);
        Assert.True(context.Control<ComboBox>("cmbHistorijaIzvjestaja").IsEnabled);
        var loadingStatus = context.Control<TextBlock>("txtIzvjestajStatus").Text;

        switch (previousOutcome)
        {
            case "success": previousLoad.SetResult(WindowTestContext.Snapshot(first)); break;
            case "failure": previousLoad.SetException(new IOException("Previous detail load failed")); break;
            case "changed": previousLoad.SetException(new IzvjestajPromijenjenException()); break;
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Empty(context.Dialogs.Errors);
        Assert.Empty(context.Reports.Filters);
        Assert.Same(first, context.Control<DataGrid>("dgKontaIzvjestaji").SelectedItem);
        Assert.Same(first, context.Control<ComboBox>("cmbHistorijaIzvjestaja").SelectedItem);
        Assert.Equal(loadingStatus, context.Control<TextBlock>("txtIzvjestajStatus").Text);
        Assert.Empty(context.Control<DataGrid>("dgIzvjestaj").Items.Cast<object>());
        Assert.False(context.Control<Button>("btnGenerisiDocx").IsEnabled);
        Assert.False(context.Control<Button>("btnPromijeniStatus").IsEnabled);

        currentLoad.SetResult(WindowTestContext.Snapshot(first));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Equal("A-1-001", VisibleSerial(context));
        Assert.True(context.Control<Button>("btnGenerisiDocx").IsEnabled);
        Assert.True(context.Control<Button>("btnPromijeniStatus").IsEnabled);
    });

    [Fact]
    public Task HistoricalSelectionExportsOnlyItsGroupAndDoesNotChangeAccountStatus() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var current = WindowTestContext.Row(grupaId: 10);
        var historical = WindowTestContext.Row(grupaId: 5, closed: true);
        context.Aparati.Snapshots[historical.GrupaId] = WindowTestContext.Snapshot(historical);
        context.Reports.History = (_, _) => Task.FromResult<IReadOnlyList<IzvjestajPregledRed>>([current, historical]);
        context.Select(current);
        var history = context.Control<ComboBox>("cmbHistorijaIzvjestaja");
        Assert.Same(current, history.SelectedItem);
        history.SelectedItem = historical;
        Assert.Equal("A-5-001", VisibleSerial(context));
        Assert.False(context.Control<Button>("btnPromijeniStatus").IsEnabled);
        await context.Window.GenerisiDocxAsync();
        Assert.Equal(5, Assert.Single(context.Docx.Requests).GrupaId);
        var today = DateTime.Today.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"aparati_A_{today}_5.docx", Assert.Single(context.Dialogs.FileNames));
        Assert.Empty(context.Reports.Changes);
        Assert.False(((IzvjestajPregledRed)context.Control<DataGrid>("dgKontaIzvjestaji").SelectedItem).Zakljucen);
    });

    [Fact]
    public Task LateDetailCannotReplaceNewHistorySelectionOfSameAccount() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var current = WindowTestContext.Row(grupaId: 10);
        var historical = WindowTestContext.Row(grupaId: 5, closed: true);
        context.Aparati.Snapshots[5] = WindowTestContext.Snapshot(historical);
        context.Reports.History = (_, _) => Task.FromResult<IReadOnlyList<IzvjestajPregledRed>>([current, historical]);
        var pending = new TaskCompletionSource<IzvjestajSnimak>();
        context.Aparati.Read = (id, _, _) => id == 10 ? pending.Task : Task.FromResult(context.Aparati.Snapshots[id]);
        context.Select(current);
        Assert.True(context.Control<ComboBox>("cmbHistorijaIzvjestaja").IsEnabled);
        context.Control<ComboBox>("cmbHistorijaIzvjestaja").SelectedItem = historical;
        pending.SetResult(WindowTestContext.Snapshot(current));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("A-5-001", VisibleSerial(context));
        Assert.True(context.Control<Button>("btnGenerisiDocx").IsEnabled);
        Assert.False(context.Control<Button>("btnPromijeniStatus").IsEnabled);
    });

    [Fact]
    public Task CancellingSaveDoesNotGenerateDocumentOrCloseGroupAndRestoresActions() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        context.Select(WindowTestContext.Row());
        context.Dialogs.Choose = () => null;
        await context.Window.GenerisiDocxAsync();
        Assert.Empty(context.Docx.Requests);
        Assert.Empty(context.Reports.Changes);
        Assert.True(context.Control<Button>("btnGenerisiDocx").IsEnabled);
    });

    [Fact]
    public Task FailedExportDoesNotCloseGroupAndRestoresActions() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        context.Select(WindowTestContext.Row());
        context.Docx.Generate = () => throw new IOException("Test export failure");
        await context.Window.GenerisiDocxAsync();
        Assert.Empty(context.Reports.Changes);
        Assert.Contains("Test export failure", Assert.Single(context.Dialogs.Errors));
        Assert.True(context.Control<Button>("btnGenerisiDocx").IsEnabled);
    });

    [Fact]
    public Task StaleCloseRefreshesInsteadOfClosingAnUnseenGroup() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var row = WindowTestContext.Row(version: 2);
        context.Select(row);
        context.Reports.Close = () => throw new IzvjestajPromijenjenException();
        await context.Window.PromijeniStatusAsync();
        Assert.Equal(new StatusChange(row.GrupaId, 2, "tester"), Assert.Single(context.Reports.Changes));
        Assert.Single(context.Reports.Filters);
        Assert.Contains("promijenjen", context.Control<TextBlock>("txtGlobalStatus").Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.Dialogs.Errors);
    });

    [Fact]
    public Task StaleExportRefreshesWithoutOpeningSaveDialog() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        context.Select(WindowTestContext.Row());
        context.Aparati.Read = (_, _, _) => throw new IzvjestajPromijenjenException();
        await context.Window.GenerisiDocxAsync();
        Assert.Empty(context.Dialogs.FileNames);
        Assert.Empty(context.Docx.Requests);
        Assert.Empty(context.Reports.Changes);
        Assert.Single(context.Reports.Filters);
    });

    [Fact]
    public Task InsertRefreshesUsingExistingStatusFilterAndSelectsNewCurrentGroup() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var previous = WindowTestContext.Row(grupaId: 5, closed: true);
        var current = WindowTestContext.Row(grupaId: 10);
        context.Select(previous);
        context.Control<ComboBox>("cmbStatusIzvjestaja").SelectedIndex = 2;
        var account = new Konto("A", "Kupac A");
        var inputAccount = context.Control<ComboBox>("cmbKontoUnos");
        inputAccount.ItemsSource = new[] { account };
        inputAccount.SelectedItem = account;
        inputAccount.Text = account.Prikaz;
        context.Control<ComboBox>("cmbTip").SelectedItem = "S 6";
        context.Control<TextBox>("txtPunjenje").Text = "6";
        context.Control<TextBox>("txtSerijskiBroj").Text = "A-NEW";
        context.Control<TextBox>("txtVozilo").Text = "N/A";
        context.Control<TextBox>("txtIspitivanjeIzvrsio").Text = "Test inspector";
        context.Control<DatePicker>("dpDatumServisa").SelectedDate = new DateTime(2025, 1, 1);
        context.Control<TextBox>("txtGodinaProizvodnje").Text = "2020";
        context.Reports.Overview = () => Task.FromResult<IReadOnlyList<IzvjestajPregledRed>>([current]);
        context.Aparati.Insert = () => { context.Register(current); return Task.FromResult(200L); };
        await context.Window.SnimiAparatAsync();
        Assert.Equal("tester", Assert.Single(context.Aparati.Inserts).User);
        Assert.Equal(IzvjestajStatusFilter.Svi, Assert.Single(context.Reports.Filters));
        Assert.Same(current, context.Control<DataGrid>("dgKontaIzvjestaji").SelectedItem);
        Assert.Same(current, context.Control<ComboBox>("cmbHistorijaIzvjestaja").SelectedItem);
        Assert.Equal("A-10-001", VisibleSerial(context));
    });

    [Fact]
    public Task ClosingRemovesSelectionWhenAccountNoLongerMatchesFilter() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        context.Select(WindowTestContext.Row());
        await context.Window.PromijeniStatusAsync();
        Assert.Null(context.Control<DataGrid>("dgKontaIzvjestaji").SelectedItem);
        Assert.Empty(context.Control<DataGrid>("dgIzvjestaj").Items.Cast<object>());
        Assert.False(context.Control<Button>("btnGenerisiDocx").IsEnabled);
    });

    private static PpAparatRecord DatedRecord(IzvjestajPregledRed group, long id, DateTime serviceDate) => new()
    {
        Id = id, GrupaId = group.GrupaId, Konto = group.Konto, DatumServisa = serviceDate,
        SljedeciServis = serviceDate.AddMonths(6), SerijskiBroj = $"SERIAL-{id}",
        Tip = "S 6", PunjenjeKg = 6, GodinaProizvodnje = 2020,
        KonstatacijaIspravnosti = "Ispravan", Vozilo = "N/A", IspitivanjeIzvrsio = "Test inspector"
    };

    private static string VisibleSerial(WindowTestContext context) =>
        Assert.Single(context.Control<DataGrid>("dgIzvjestaj").Items.Cast<IzvjestajRed>()).SerijskiBroj;
}
