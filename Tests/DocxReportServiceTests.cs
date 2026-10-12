using System.IO;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using PpEvidencija.Models;
using PpEvidencija.Services;
using Xunit;

namespace PpEvidencija.Tests;

public sealed class DocxReportServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ppEvidencija-docx-tests-{Guid.NewGuid():N}");
    private readonly string _templatePath;

    public DocxReportServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _templatePath = Path.Combine(_directory, "template.docx");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Templates", "ZapisnikTemplate.docx"), _templatePath);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("conclusion-before-last-service")]
    [InlineData("wrong-account")]
    [InlineData("wrong-group")]
    [InlineData("group-zero")]
    [InlineData("group-negative")]
    [InlineData("document-before-supported-range")]
    [InlineData("service-before-supported-range")]
    [InlineData("null-item")]
    public async Task InvalidRequestLeavesExistingReportUnchanged(string scenario)
    {
        var request = Request();
        request = scenario switch
        {
            "empty" => request with { Aparati = [] },
            "conclusion-before-last-service" => request with { DatumZakljucivanja = new DateTime(2026, 9, 10) },
            "wrong-account" => request with { Kupac = new Konto("B", "Drugi kupac") },
            "wrong-group" => request with { GrupaId = 43 },
            "group-zero" => request with { GrupaId = 0 },
            "group-negative" => request with { GrupaId = -1 },
            "document-before-supported-range" => request with { DatumZakljucivanja = new DateTime(1899, 12, 31) },
            "service-before-supported-range" => request with { Aparati = [Item(1, new DateTime(1899, 12, 31))] },
            "null-item" => request with { Aparati = [null!] },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var output = ExistingReport();
        var original = File.ReadAllBytes(output);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().GenerateAsync(request, output));

        Assert.Equal(original, File.ReadAllBytes(output));
        AssertNoTemporaryReports();
    }

    [Fact]
    public async Task MissingRowMarkerAfterHeaderPopulationLeavesExistingReportUnchanged()
    {
        using (var template = WordprocessingDocument.Open(_templatePath, true))
        {
            template.MainDocumentPart!.Document!.Descendants<SdtRow>()
                .Single(control => Tag(control) == "ReportRows").Remove();
            template.MainDocumentPart.Document.Save();
        }
        var output = ExistingReport();
        var original = File.ReadAllBytes(output);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => Service().GenerateAsync(Request(), output));

        Assert.Contains("ReportRows", exception.Message);
        Assert.Equal(original, File.ReadAllBytes(output));
        AssertNoTemporaryReports();
    }

    [Fact]
    public async Task SuccessfulGenerationReplacesOutputWithAllRowsAndSchemaValidDocument()
    {
        var output = ExistingReport();
        var original = File.ReadAllBytes(output);

        await Service().GenerateAsync(Request(), output);

        Assert.False(original.SequenceEqual(File.ReadAllBytes(output)));
        using var document = WordprocessingDocument.Open(output, false);
        Assert.Equal("KUPAC A", Text(document, "CustomerTitle"));
        Assert.Equal("01.09.2026.g.", Text(document, "PeriodFrom"));
        Assert.Equal("30.09.2026", Text(document, "PeriodTo"));
        Assert.Equal("Doboj Jug, septembar 2026.g.", Text(document, "LocationMonthYear"));
        Assert.Equal("2", Text(document, "ConclusionCount"));
        var dataRows = document.MainDocumentPart!.Document!.Descendants<TableRow>()
            .Where(row => row.Elements<TableCell>().FirstOrDefault()?.InnerText is "1" or "2")
            .ToArray();
        Assert.Equal(2, dataRows.Length);
        Assert.Equal("SERIAL-1", dataRows[0].Elements<TableCell>().ElementAt(3).InnerText);
        Assert.Equal("SERIAL-20", dataRows[1].Elements<TableCell>().ElementAt(3).InnerText);
        Assert.DoesNotContain("{{REPORT_ROWS}}", document.MainDocumentPart.Document.InnerText);
        var errors = new OpenXmlValidator().Validate(document).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.Description)));
        AssertNoTemporaryReports();
    }

    [Fact]
    public async Task ConclusionOnLastServiceDateIsAllowed()
    {
        var output = Path.Combine(_directory, "on-last-service.docx");

        await Service().GenerateAsync(Request() with { DatumZakljucivanja = new DateTime(2026, 9, 20) }, output);

        using var document = WordprocessingDocument.Open(output, false);
        Assert.Equal("20.09.2026", Text(document, "PeriodTo"));
    }

    [Fact]
    public async Task GroupSpanningMonthsAndYearsUsesChosenDocumentDateForHeaderAndPeriod()
    {
        var output = Path.Combine(_directory, "multiple-years.docx");
        var request = Request() with
        {
            DatumZakljucivanja = new DateTime(2027, 2, 15),
            Aparati =
            [
                Item(1, new DateTime(2025, 12, 31)),
                Item(2, new DateTime(2026, 9, 20)),
                Item(3, new DateTime(2027, 1, 5))
            ]
        };

        await Service().GenerateAsync(request, output);

        using var document = WordprocessingDocument.Open(output, false);
        Assert.Equal("31.12.2025.g.", Text(document, "PeriodFrom"));
        Assert.Equal("15.02.2027", Text(document, "PeriodTo"));
        Assert.Equal("15.02.2027.g.", Text(document, "ConclusionDate"));
        Assert.Equal("Doboj Jug, februar 2027.g.", Text(document, "LocationMonthYear"));
        Assert.Equal("3", Text(document, "ConclusionCount"));
        Assert.Equal("Konto A, grupa 42, 31.12.2025 - 15.02.2027", document.PackageProperties.Subject);
        var dataRows = document.MainDocumentPart!.Document!.Descendants<TableRow>()
            .Where(row => row.Elements<TableCell>().FirstOrDefault()?.InnerText is "1" or "2" or "3")
            .ToArray();
        Assert.Equal(3, dataRows.Length);
        Assert.Equal(["SERIAL-1", "SERIAL-2", "SERIAL-3"],
            dataRows.Select(row => row.Elements<TableCell>().ElementAt(3).InnerText).ToArray());
        var errors = new OpenXmlValidator().Validate(document).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.Description)));
    }

    [Fact]
    public async Task BackdatedApparatusBelongsToSelectedGroupAndCanBeExportedAgainWithANewDocumentDate()
    {
        var output = Path.Combine(_directory, "backdated.docx");
        var request = Request() with
        {
            Aparati = [Item(20), Item(1, new DateTime(2024, 3, 7))]
        };

        await Service().GenerateAsync(request, output);
        await Service().GenerateAsync(request with { DatumZakljucivanja = new DateTime(2026, 10, 12) }, output);

        using var document = WordprocessingDocument.Open(output, false);
        Assert.Equal("07.03.2024.g.", Text(document, "PeriodFrom"));
        Assert.Equal("12.10.2026", Text(document, "PeriodTo"));
        Assert.Equal("Doboj Jug, oktobar 2026.g.", Text(document, "LocationMonthYear"));
        Assert.Equal("2", Text(document, "ConclusionCount"));
        AssertNoTemporaryReports();
    }

    [Fact]
    public async Task FirstSupportedServiceAndDocumentDateIsAllowed()
    {
        var output = Path.Combine(_directory, "first-supported-date.docx");
        var firstDate = new DateTime(1900, 1, 1);
        var request = Request() with { DatumZakljucivanja = firstDate, Aparati = [Item(1, firstDate)] };

        await Service().GenerateAsync(request, output);

        using var document = WordprocessingDocument.Open(output, false);
        Assert.Equal("01.01.1900.g.", Text(document, "PeriodFrom"));
        Assert.Equal("01.01.1900", Text(document, "PeriodTo"));
    }

    [Fact]
    public async Task TemplateCannotBeOverwritten()
    {
        var original = File.ReadAllBytes(_templatePath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().GenerateAsync(Request(), _templatePath));

        Assert.Equal(original, File.ReadAllBytes(_templatePath));
        AssertNoTemporaryReports();
    }

    [Fact]
    public async Task AlreadyCancelledRequestLeavesExistingReportUnchanged()
    {
        var output = ExistingReport();
        var original = File.ReadAllBytes(output);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().GenerateAsync(Request(), output, cancellation.Token));

        Assert.Equal(original, File.ReadAllBytes(output));
        AssertNoTemporaryReports();
    }

    [Fact]
    public async Task CancellationAfterTemporaryFileCreationPreservesExistingReport()
    {
        var output = ExistingReport();
        var original = File.ReadAllBytes(output);
        using var cancellation = new CancellationTokenSource();
        using var watcher = new FileSystemWatcher(_directory, ".ppEvidencija-*.tmp");
        watcher.Created += (_, _) => cancellation.Cancel();
        watcher.EnableRaisingEvents = true;
        var request = Request() with { Aparati = Enumerable.Range(0, 10_000).Select(_ => Item(1)).ToArray() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().GenerateAsync(request, output, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(original, File.ReadAllBytes(output));
        AssertNoTemporaryReports();
    }

    [Fact]
    public async Task DestinationSharingErrorPreservesExistingReportAndRemovesTemporaryFile()
    {
        var output = ExistingReport();
        var original = File.ReadAllBytes(output);
        using var existingFile = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read);

        var exception = await Record.ExceptionAsync(() => Service().GenerateAsync(Request(), output));

        Assert.True(exception is IOException or UnauthorizedAccessException, $"Unexpected result: {exception}");

        Assert.Equal(original, File.ReadAllBytes(output));
        AssertNoTemporaryReports();
    }

    private DocxReportService Service() => new(_templatePath);

    private string ExistingReport()
    {
        var output = Path.Combine(_directory, "existing.docx");
        File.WriteAllText(output, "Prethodno sacuvani izvjestaj mora ostati netaknut.");
        return output;
    }

    private void AssertNoTemporaryReports() => Assert.Empty(Directory.GetFiles(_directory, ".ppEvidencija-*.tmp"));

    private static IzvjestajZahtjev Request() =>
        new("TEST-001", new DateTime(2026, 9, 30), new Konto("A", "Kupac A"), 42, [Item(1), Item(20)]);

    private static PpAparatRecord Item(int day, DateTime? serviceDate = null) => new()
    {
        Id = day,
        GrupaId = 42,
        Konto = "A",
        Tip = "S",
        PunjenjeKg = 6,
        SerijskiBroj = $"SERIAL-{day}",
        GodinaProizvodnje = 2020,
        DatumServisa = serviceDate ?? new DateTime(2026, 9, day),
        SljedeciServis = (serviceDate ?? new DateTime(2026, 9, day)).AddMonths(6),
        KonstatacijaIspravnosti = "Ispravan",
        Vozilo = "N/A",
        IspitivanjeIzvrsio = "Test osoba"
    };

    private static string? Tag(SdtElement control) => control.GetFirstChild<SdtProperties>()?.GetFirstChild<Tag>()?.Val?.Value;

    private static string Text(WordprocessingDocument document, string tag) => document.MainDocumentPart!.Document!
        .Descendants<SdtElement>().Single(control => Tag(control) == tag).InnerText;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
