using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PpEvidencija.Tests;

[Collection("WPF")]
public sealed class MainWindowLayoutTests
{
    [Theory]
    [InlineData(1000, 720)]
    [InlineData(1380, 850)]
    public Task ReportHistoryAndActionsFitAtSupportedWindowSizes(int width, int height) => WpfTestHost.RunAsync(async () =>
    {
        using var context = new WindowTestContext();
        var row = WindowTestContext.Row();
        var historical = WindowTestContext.Row(grupaId: 5, closed: true);
        context.Reports.History = (_, _) => Task.FromResult<IReadOnlyList<PpEvidencija.Models.IzvjestajPregledRed>>([row, historical]);
        context.Register(row);
        context.Reports.Overview = () => Task.FromResult<IReadOnlyList<PpEvidencija.Models.IzvjestajPregledRed>>([row]);
        await context.Window.UcitajPregledKontaAsync(row);
        var root = (Grid)context.Window.Content;
        root.Background = context.Window.Background;
        root.Children.OfType<TabControl>().Single().SelectedIndex = 1;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();

        var card = context.Control<Border>("pnlOdabraniKupac");
        foreach (var name in new[] { "cmbHistorijaIzvjestaja", "btnPromijeniStatus", "btnGenerisiDocx", "btnOtvoriWord", "dpDatumZakljucivanja" })
        {
            var control = context.Control<FrameworkElement>(name);
            var bounds = control.TransformToAncestor(card).TransformBounds(new Rect(control.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= card.ActualWidth, $"{name} exceeds card width at {width}px: {bounds}");
            Assert.True(bounds.Top >= 0 && bounds.Bottom <= card.ActualHeight, $"{name} exceeds card height at {height}px: {bounds}");
        }
        Assert.True(context.Control<DataGrid>("dgIzvjestaj").ActualHeight >= 90);
        Assert.True(context.Control<DataGrid>("dgKontaIzvjestaji").Columns.Single(column => Equals(column.Header, "Kupac")).ActualWidth >= 200);

        var directory = Environment.GetEnvironmentVariable("PPEVIDENCIJA_UI_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, $"izvjestaji-{width}.png"));
            encoder.Save(output);
        }
    });
}
