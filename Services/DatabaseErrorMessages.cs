using Microsoft.Data.SqlClient;

namespace PpEvidencija.Services;

public sealed class ErpConfigurationException(string message) : Exception(message);
public sealed class DatabaseUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public static class DatabaseErrorMessages
{
    public static string ForUser(Exception exception, string fallback) => exception switch
    {
        ErpConfigurationException or DatabaseUnavailableException => exception.Message,
        OperationCanceledException => "Radnja je otkazana.",
        SqlException { Number: 50102 } => "Postavke je u međuvremenu izmijenio drugi korisnik. Ponovo otvorite konfiguraciju.",
        SqlException { Number: 50103 } => "ERP postavke nije moguće učitati ili sačuvati jer zaštita SQL lozinke u bazi evidencija nije dostupna.",
        SqlException { Number: 50104 } => "Podaci ERP konekcije nisu ispravni. Provjerite obavezna polja.",
        SqlException { Number: 2812 or 208 } => "Nedostaje SQL nadogradnja za ERP konfiguraciju. Obratite se SQL administratoru.",
        _ => fallback
    };
}
