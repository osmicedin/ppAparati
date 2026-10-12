using System.Globalization;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PpEvidencija.Models;

namespace PpEvidencija.Services;

public sealed class DocxReportService : IDocxReportService
{
    private static readonly CultureInfo BosnianCulture = CultureInfo.GetCultureInfo("bs-Latn-BA");
    private readonly string _templatePath;

    public DocxReportService()
        : this(Path.Combine(AppContext.BaseDirectory, "Templates", "ZapisnikTemplate.docx"))
    {
    }

    internal DocxReportService(string templatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);
        _templatePath = Path.GetFullPath(templatePath);
    }

    public Task GenerateAsync(
        IzvjestajZahtjev request,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(request.Aparati);

        var snapshot = request with { Aparati = request.Aparati.ToArray() };
        return Task.Run(() => Generate(snapshot, outputPath, cancellationToken), cancellationToken);
    }

    private void Generate(
        IzvjestajZahtjev request,
        string outputPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        if (!File.Exists(_templatePath))
        {
            throw new FileNotFoundException("Nedostaje DOCX predložak izvještaja.", _templatePath);
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        if (string.Equals(fullOutputPath, _templatePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Izvještaj ne može zamijeniti DOCX predložak aplikacije.");
        }

        var outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException("Odredišni folder dokumenta nije ispravan.");
        }

        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(outputDirectory, $".ppEvidencija-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(_templatePath, temporaryPath);
            PopulateDocument(request, temporaryPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // The temporary file is in the destination directory, so publication is one rename.
            // A validation, generation, cancellation or sharing error leaves the old report intact.
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateRequest(IzvjestajZahtjev request)
    {
        ArgumentNullException.ThrowIfNull(request.Kupac);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kupac.Sifra);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kupac.Naziv);
        ArgumentNullException.ThrowIfNull(request.BrojZapisnika);
        if (request.GrupaId <= 0)
        {
            throw new InvalidOperationException("Grupa izvještaja nije ispravna.");
        }

        if (request.DatumZakljucivanja.Year < 1900)
        {
            throw new InvalidOperationException("Datum dokumenta mora biti od 01.01.1900. godine.");
        }

        if (request.Aparati.Count == 0)
        {
            throw new InvalidOperationException("Izvještaj nema nijedan PP aparat.");
        }

        foreach (var item in request.Aparati)
        {
            if (item is null || !string.Equals(item.Konto?.Trim(), request.Kupac.Sifra.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Izvještaj sadrži aparat koji ne pripada odabranom kupcu.");
            }

            if (item.GrupaId != request.GrupaId)
            {
                throw new InvalidOperationException("Izvještaj sadrži aparat koji ne pripada odabranoj grupi.");
            }

            if (item.DatumServisa.Year < 1900)
            {
                throw new InvalidOperationException("Datum servisa u izvještaju mora biti od 01.01.1900. godine.");
            }
        }

        if (request.DatumZakljucivanja.Date < request.Aparati.Max(item => item.DatumServisa).Date)
        {
            throw new InvalidOperationException(
                "Datum dokumenta ne može biti prije posljednjeg datuma servisa u izvještaju.");
        }
    }

    private static void PopulateDocument(
        IzvjestajZahtjev request,
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = WordprocessingDocument.Open(path, true);
        var mainPart = document.MainDocumentPart
            ?? throw new InvalidDataException("DOCX predložak nema glavni dio dokumenta.");
        NormalizeLegacyTemplate(mainPart);

        var from = request.Aparati.Min(item => item.DatumServisa).Date;
        var to = request.DatumZakljucivanja.Date;
        SetContentControlText(mainPart, "ReportNumber", request.BrojZapisnika.Trim());
        SetContentControlText(mainPart, "ConclusionDate", FormatDate(request.DatumZakljucivanja, includeSuffix: true));
        SetContentControlText(mainPart, "CustomerTitle", request.Kupac.Naziv.Trim().ToUpper(BosnianCulture));
        SetContentControlText(mainPart, "CustomerOrderer", request.Kupac.Naziv.Trim());
        SetContentControlText(mainPart, "CustomerOwner", request.Kupac.Naziv.Trim());
        SetContentControlText(mainPart, "LocationMonthYear", BuildLocationMonthYear(to.Month, to.Year));
        SetContentControlText(mainPart, "PeriodFrom", FormatDate(from, includeSuffix: true));
        SetContentControlText(mainPart, "PeriodTo", FormatDate(to, includeSuffix: false));
        SetContentControlText(mainPart, "ConclusionCount", request.Aparati.Count.ToString(CultureInfo.InvariantCulture));

        ReplaceReportRows(mainPart, request.Aparati, cancellationToken);

        var settingsPart = mainPart.DocumentSettingsPart ?? mainPart.AddNewPart<DocumentSettingsPart>();
        settingsPart.Settings ??= new Settings();
        settingsPart.Settings.RemoveAllChildren<UpdateFieldsOnOpen>();
        settingsPart.Settings.AddChild(new UpdateFieldsOnOpen { Val = true }, throwOnError: true);
        settingsPart.Settings.Save();

        var documentRoot = mainPart.Document
            ?? throw new InvalidDataException("DOCX predložak nema glavni XML dokument.");
        documentRoot.Save();

        var properties = document.PackageProperties;
        properties.Title = $"Zapisnik PP aparata - {request.Kupac.Naziv}";
        properties.Subject = $"Konto {request.Kupac.Sifra.Trim()}, grupa {request.GrupaId}, {FormatDate(from)} - {FormatDate(to)}";
        properties.Creator = "ppEvidencija";
        properties.Modified = DateTime.UtcNow;
    }

    private static void NormalizeLegacyTemplate(MainDocumentPart mainPart)
    {
        var documentRoot = mainPart.Document
            ?? throw new InvalidDataException("DOCX predložak nema glavni XML dokument.");
        // Existing installations can retain the original template. Its two header paragraphs
        // contain spacing after alignment; reinsert spacing using the SDK's schema order.
        foreach (var properties in documentRoot.Descendants<ParagraphProperties>())
        {
            var spacing = properties.GetFirstChild<SpacingBetweenLines>();
            if (spacing is not null)
            {
                spacing.Remove();
                properties.AddChild(spacing, throwOnError: true);
            }
        }

        var settings = mainPart.DocumentSettingsPart?.Settings;
        if (settings is not null)
        {
            foreach (var legacySetting in settings.Elements()
                         .Where(item => item.LocalName == "doNotEmbedSmartTags"
                             && item.NamespaceUri == "http://schemas.openxmlformats.org/wordprocessingml/2006/main")
                         .ToList())
            {
                legacySetting.Remove();
            }
        }
    }

    private static void ReplaceReportRows(
        MainDocumentPart mainPart,
        IReadOnlyList<PpAparatRecord> apparatus,
        CancellationToken cancellationToken)
    {
        var documentRoot = mainPart.Document
            ?? throw new InvalidDataException("DOCX predložak nema glavni XML dokument.");

        var rowControl = documentRoot
            .Descendants<SdtRow>()
            .FirstOrDefault(control => GetTag(control) == "ReportRows");

        if (rowControl is not null)
        {
            for (var index = 0; index < apparatus.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowControl.InsertBeforeSelf(CreateDataRow(index + 1, apparatus[index]));
            }

            rowControl.Remove();
            return;
        }

        var markerRow = documentRoot
            .Descendants<TableRow>()
            .FirstOrDefault(row => row.InnerText.Contains("{{REPORT_ROWS}}", StringComparison.Ordinal))
            ?? throw new InvalidDataException("Predložak nema red 'ReportRows'.");

        for (var index = 0; index < apparatus.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            markerRow.InsertBeforeSelf(CreateDataRow(index + 1, apparatus[index]));
        }

        markerRow.Remove();
    }

    private static TableRow CreateDataRow(int rowNumber, PpAparatRecord item)
    {
        var row = new TableRow();
        row.AppendChild(new TableRowProperties(new CantSplit()));

        row.Append(
            CreateCell(rowNumber.ToString(CultureInfo.InvariantCulture), 500, JustificationValues.Center),
            CreateCell(item.Tip, 650, JustificationValues.Center),
            CreateCell(FormatWeight(item.PunjenjeKg), 800, JustificationValues.Center),
            CreateCell(item.SerijskiBroj, 1350, JustificationValues.Center),
            CreateCell(item.GodinaProizvodnje.ToString(CultureInfo.InvariantCulture), 850, JustificationValues.Center),
            CreateCell(FormatDate(item.DatumServisa), 1100, JustificationValues.Center),
            CreateCell(FormatDate(item.SljedeciServis), 1100, JustificationValues.Center),
            CreateCell(item.KonstatacijaIspravnosti, 1450, JustificationValues.Left),
            CreateCell(item.Vozilo, 1400, JustificationValues.Left),
            CreateCell(item.IspitivanjeIzvrsio, 2000, JustificationValues.Left));

        return row;
    }

    private static TableCell CreateCell(string value, int width, JustificationValues alignment)
    {
        var cellProperties = new TableCellProperties(
            new TableCellWidth
            {
                Type = TableWidthUnitValues.Dxa,
                Width = width.ToString(CultureInfo.InvariantCulture)
            },
            new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });

        var paragraphProperties = new ParagraphProperties(
            new SpacingBetweenLines { Before = "0", After = "20", Line = "220", LineRule = LineSpacingRuleValues.Auto },
            new Justification { Val = alignment });

        var runProperties = new RunProperties(
            new RunFonts { Ascii = "Arial", HighAnsi = "Arial", EastAsia = "Arial" },
            new FontSize { Val = "16" },
            new FontSizeComplexScript { Val = "16" });

        var text = new Text(value) { Space = SpaceProcessingModeValues.Preserve };
        return new TableCell(cellProperties, new Paragraph(paragraphProperties, new Run(runProperties, text)));
    }

    private static void SetContentControlText(MainDocumentPart mainPart, string tag, string value)
    {
        var documentRoot = mainPart.Document
            ?? throw new InvalidDataException("DOCX predložak nema glavni XML dokument.");

        var control = documentRoot
            .Descendants<SdtElement>()
            .FirstOrDefault(item => GetTag(item) == tag)
            ?? throw new InvalidDataException($"Predložak nema content control '{tag}'.");

        var texts = control.Descendants<Text>().ToList();
        if (texts.Count == 0)
        {
            throw new InvalidDataException($"Content control '{tag}' nema tekstualni sadržaj.");
        }

        texts[0].Text = value;
        texts[0].Space = SpaceProcessingModeValues.Preserve;
        foreach (var text in texts.Skip(1))
        {
            text.Text = string.Empty;
        }

    }

    private static string? GetTag(SdtElement control) =>
        control.GetFirstChild<SdtProperties>()
            ?.GetFirstChild<Tag>()
            ?.Val
            ?.Value;

    private static string FormatWeight(decimal value) =>
        $"{value.ToString("0.##", BosnianCulture)} kg";

    private static string FormatDate(DateTime value, bool includeSuffix = false) =>
        value.ToString(includeSuffix ? "dd.MM.yyyy.'g.'" : "dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string BuildLocationMonthYear(int month, int year)
    {
        var monthNames = new[]
        {
            "januar", "februar", "mart", "april", "maj", "juni",
            "juli", "august", "septembar", "oktobar", "novembar", "decembar"
        };

        return $"Doboj Jug, {monthNames[month - 1]} {year}.g.";
    }
}
