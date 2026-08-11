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

    public Task GenerateAsync(
        IzvjestajZahtjev request,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return Task.Run(() => Generate(request, outputPath, cancellationToken), cancellationToken);
    }

    private static void Generate(
        IzvjestajZahtjev request,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (request.Aparati.Count == 0)
        {
            throw new InvalidOperationException("Izvještaj nema nijedan PP aparat.");
        }

        var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "ZapisnikTemplate.docx");
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("Nedostaje DOCX predložak izvještaja.", templatePath);
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException("Odredišni folder dokumenta nije ispravan.");
        }

        Directory.CreateDirectory(outputDirectory);
        File.Copy(templatePath, fullOutputPath, overwrite: true);

        using var document = WordprocessingDocument.Open(fullOutputPath, true);
        var mainPart = document.MainDocumentPart
            ?? throw new InvalidDataException("DOCX predložak nema glavni dio dokumenta.");

        var from = request.Aparati.Min(item => item.DatumServisa).Date;
        var to = request.DatumZakljucivanja.Date;
        if (to < from)
        {
            throw new InvalidOperationException(
                "Datum zaključivanja ne može biti prije prvog datuma servisa u izvještaju.");
        }

        SetContentControlText(mainPart, "ReportNumber", request.BrojZapisnika.Trim());
        SetContentControlText(mainPart, "ConclusionDate", FormatDate(request.DatumZakljucivanja, includeSuffix: true));
        SetContentControlText(mainPart, "CustomerTitle", request.Kupac.Naziv.Trim().ToUpper(BosnianCulture));
        SetContentControlText(mainPart, "CustomerOrderer", request.Kupac.Naziv.Trim());
        SetContentControlText(mainPart, "CustomerOwner", request.Kupac.Naziv.Trim());
        SetContentControlText(mainPart, "LocationMonthYear", BuildLocationMonthYear(request.Mjesec, request.Godina));
        SetContentControlText(mainPart, "PeriodFrom", FormatDate(from, includeSuffix: true));
        SetContentControlText(mainPart, "PeriodTo", FormatDate(to, includeSuffix: false));
        SetContentControlText(mainPart, "ConclusionCount", request.Aparati.Count.ToString(CultureInfo.InvariantCulture));

        ReplaceReportRows(mainPart, request.Aparati, cancellationToken);

        var settingsPart = mainPart.DocumentSettingsPart ?? mainPart.AddNewPart<DocumentSettingsPart>();
        settingsPart.Settings ??= new Settings();
        settingsPart.Settings.RemoveAllChildren<UpdateFieldsOnOpen>();
        settingsPart.Settings.AppendChild(new UpdateFieldsOnOpen { Val = true });
        settingsPart.Settings.Save();

        var documentRoot = mainPart.Document
            ?? throw new InvalidDataException("DOCX predložak nema glavni XML dokument.");
        documentRoot.Save();

        var properties = document.PackageProperties;
        properties.Title = $"Zapisnik PP aparata - {request.Kupac.Naziv}";
        properties.Subject = $"{request.Mjesec:00}/{request.Godina}";
        properties.Creator = "ppEvidencija";
        properties.Modified = DateTime.UtcNow;
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
            new Justification { Val = alignment },
            new SpacingBetweenLines { Before = "0", After = "20", Line = "220", LineRule = LineSpacingRuleValues.Auto });

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
