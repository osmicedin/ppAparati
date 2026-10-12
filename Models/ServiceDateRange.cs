namespace PpEvidencija.Models;

public static class ServiceDateRange
{
    public static readonly DateTime Minimum = new(1900, 1, 1);
    public static readonly DateTime Maximum = new(9999, 6, 30);

    public static bool Contains(DateTime date) =>
        date.Date >= Minimum && date.Date <= Maximum;
}
