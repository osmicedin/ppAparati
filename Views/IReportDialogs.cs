using System.Windows;
using PpEvidencija.Models;

namespace PpEvidencija.Views;

public interface IReportDialogs
{
    bool ConfirmStatusChange(Window owner, IzvjestajPregledRed selected, bool close);
    string? ChooseOutputPath(Window owner, string fileName);
}
