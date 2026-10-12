using System.Windows;
using PpEvidencija.Models;

namespace PpEvidencija.Views;

public interface IReportDialogs
{
    bool ConfirmClose(Window owner, IzvjestajPregledRed selected);
    string? ChooseOutputPath(Window owner, string fileName);
    void ShowError(Window owner, string message, string title);
}
