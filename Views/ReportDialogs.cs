using System.Windows;
using Microsoft.Win32;
using PpEvidencija.Models;

namespace PpEvidencija.Views;

public sealed class ReportDialogs : IReportDialogs
{
    public bool ConfirmStatusChange(Window owner, IzvjestajPregledRed selected, bool close)
    {
        var action = close ? "zaključiti" : "ponovo otvoriti";
        return MessageBox.Show(
            owner,
            $"Da li želite {action} period {selected.Mjesec:00}/{selected.Godina} za kupca:{Environment.NewLine}{selected.Kupac.Prikaz}?",
            close ? "Zaključivanje perioda" : "Ponovno otvaranje perioda",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

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
