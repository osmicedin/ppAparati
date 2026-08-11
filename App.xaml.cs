using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PpEvidencija.Data;
using PpEvidencija.Models;
using PpEvidencija.Services;
using PpEvidencija.Views;

namespace PpEvidencija;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
#if DEBUG
            if (e.Args.Length == 2
                && string.Equals(e.Args[0], "--generate-sample-report", StringComparison.OrdinalIgnoreCase))
            {
                await GenerateSampleReportAsync(e.Args[1]);
                Shutdown();
                return;
            }
#endif

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _host = BuildHost();
            await _host.StartAsync();

            var prijava = _host.Services.GetRequiredService<LoginWindow>();
            if (prijava.ShowDialog() != true || prijava.PrijavljeniKorisnik is null)
            {
                Shutdown();
                return;
            }

            var glavniProzor = ActivatorUtilities.CreateInstance<MainWindow>(
                _host.Services,
                prijava.PrijavljeniKorisnik);

            MainWindow = glavniProzor;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            glavniProzor.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Aplikacija se ne može pokrenuti.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Greška pri pokretanju",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

#if DEBUG
    private static async Task GenerateSampleReportAsync(string outputPath)
    {
        var sampleRows = Enumerable.Range(1, 72)
            .Select(index =>
            {
                var serviceDate = new DateTime(2026, 7, 1).AddDays((index - 1) % 28);
                return new PpAparatRecord
                {
                    Id = index,
                    Konto = "211000001",
                    Tip = index % 5 == 0 ? "CO2-5" : index % 3 == 0 ? "S-3" : "S-6",
                    PunjenjeKg = index % 5 == 0 ? 5 : index % 3 == 0 ? 3 : 6,
                    SerijskiBroj = index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
                    GodinaProizvodnje = (short)(2015 + index % 12),
                    DatumServisa = serviceDate,
                    SljedeciServis = serviceDate.AddMonths(6),
                    KonstatacijaIspravnosti = "Ispravan",
                    Vozilo = index % 4 == 0 ? "N/A" : $"Vozilo {index:00}",
                    IspitivanjeIzvrsio = "Huskić Mehmedalija"
                };
            })
            .ToList();

        var request = new IzvjestajZahtjev(
            "006-TEST/26",
            new DateTime(2026, 7, 31),
            new Konto("211000001", "HIFA OIL DOO"),
            7,
            2026,
            sampleRows);

        await new DocxReportService().GenerateAsync(request, outputPath);
    }
#endif

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private static IHost BuildHost()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "Nedostaje appsettings.json. Kopirajte appsettings.example.json i upišite SQL Server konekciju.",
                configPath);
        }

        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config =>
            {
                config.Sources.Clear();
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
                config.AddEnvironmentVariables(prefix: "PPEVIDENCIJA_");
            })
            .ConfigureServices((context, services) =>
            {
                var connectionString = context.Configuration.GetConnectionString("DefaultConnection");
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException("ConnectionStrings:DefaultConnection nije postavljen.");
                }

                services.AddSingleton(new SqlConnectionFactory(connectionString));
                services.AddSingleton<AuthRepository>();
                services.AddSingleton<KontoRepository>();
                services.AddSingleton<PpAparatRepository>();
                services.AddSingleton<PpAparatValidator>();
                services.AddSingleton<IDocxReportService, DocxReportService>();
                services.AddTransient<LoginWindow>();
            })
            .Build();
    }
}
