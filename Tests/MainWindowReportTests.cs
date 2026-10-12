using System.Windows.Controls;
using PpEvidencija.Models;

namespace PpEvidencija.Tests;

[Collection("WPF")]
public sealed class MainWindowReportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StatusChangeUsesConfirmedCustomerAndPeriodEvenIfSelectionChanges(bool initiallyClosed) => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var confirmed = WindowTestContext.Row(closed: initiallyClosed);
        context.Select(confirmed);
        context.Dialogs.Confirm = (row, close) =>
        {
            Assert.Same(confirmed, row);
            Assert.Equal(!initiallyClosed, close);
            context.Select(WindowTestContext.Row("B", 9));
            Assert.False(context.Control<Button>("btnPromijeniStatus").IsEnabled);
            return true;
        };

        await context.Window.PromijeniStatusAsync();

        Assert.Equal(new StatusChange("A", 8, 2026, initiallyClosed, !initiallyClosed, "tester"), Assert.Single(context.Reports.Changes));
    });

    [Fact]
    public Task CancelledConfirmationDoesNotChangeStatusAndRestoresButtons() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        context.Select(WindowTestContext.Row());
        context.Dialogs.Confirm = (_, _) => false;
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
    public Task ExportKeepsOriginalRowsWhenSelectionChangesDuringReadAndSaveDialog() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        context.Select(WindowTestContext.Row());
        var pending = new TaskCompletionSource<IReadOnlyList<PpAparatRecord>>();
        context.Aparati.Read = (konto, month, _, _) => konto == "A" ? pending.Task : Task.FromResult(WindowTestContext.Records(konto, month));
        context.Dialogs.Choose = () =>
        {
            context.Select(WindowTestContext.Row("C", 9));
            return Path.Combine(Path.GetTempPath(), "report-A.docx");
        };

        var export = context.Window.GenerisiDocxAsync();
        context.Select(WindowTestContext.Row("B", 9));
        Assert.False(context.Control<Button>("btnGenerisiDocx").IsEnabled);
        await context.Window.GenerisiDocxAsync(); // Reentrant click must not start a second export.
        pending.SetResult(WindowTestContext.Records("A"));
        await export;

        var request = Assert.Single(context.Docx.Requests);
        Assert.Equal("A", request.Kupac.Sifra);
        Assert.Equal(8, request.Mjesec);
        Assert.Equal("A-001", Assert.Single(request.Aparati).SerijskiBroj);
        Assert.Equal("C-001", Assert.Single(context.Control<DataGrid>("dgIzvjestaj").Items.Cast<IzvjestajRed>()).SerijskiBroj);
        Assert.True(context.Control<Button>("btnGenerisiDocx").IsEnabled);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LateDetailResultCannotReplaceNewCustomersRows(bool cancelRequest) => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var first = WindowTestContext.Row();
        context.Select(first);
        var pending = new TaskCompletionSource<IReadOnlyList<PpAparatRecord>>();
        context.Aparati.Read = (konto, month, _, _) => konto == "A" ? pending.Task : Task.FromResult(WindowTestContext.Records(konto, month));
        using var cancellation = new CancellationTokenSource();
        var oldLoad = context.Window.UcitajIzvjestajAsync(first, cancellation.Token);
        context.Select(WindowTestContext.Row("B", 9));
        if (cancelRequest) cancellation.Cancel();
        pending.SetResult(WindowTestContext.Records("A"));
        if (cancelRequest) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldLoad);
        else await oldLoad;

        Assert.Equal("B-001", Assert.Single(context.Control<DataGrid>("dgIzvjestaj").Items.Cast<IzvjestajRed>()).SerijskiBroj);
    });

    [Fact]
    public Task CancellingSaveDoesNotGenerateDocumentAndRestoresActions() => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        context.Select(WindowTestContext.Row());
        context.Dialogs.Choose = () => null;
        await context.Window.GenerisiDocxAsync();
        Assert.Empty(context.Docx.Requests);
        Assert.True(context.Control<Button>("btnGenerisiDocx").IsEnabled);
    });
}
