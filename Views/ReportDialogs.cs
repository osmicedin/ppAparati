using System.Windows;
using Microsoft.Win32;
using PpEvidencija.Models;

namespace PpEvidencija.Views;

public sealed class ReportDialogs : IReportDialogs
{
    public bool ConfirmClose(Window owner, IzvjestajPregledRed selected)
    {
        return MessageBox.Show(
            owner,
            $"Zaključiti {selected.BrojAparata} aparata ({selected.PeriodPrikaz}) za kupca:{Environment.NewLine}{selected.Kupac.Prikaz}?{Environment.NewLine}{Environment.NewLine}Zaključeni skup se ne može ponovo otvoriti. Novi unos započinje novi skup.",
            "Zaključivanje konta",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public void ShowError(Window owner, string message, string title) =>
        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public string? ChooseOutputPath(Window owner, string fileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Sačuvaj zapisnik",
            Filter = "Word dokument (*.docx)|*.docx",
            AddExtension = true,
            DefaultExt = ".docx",
            FileName = fileName
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}
