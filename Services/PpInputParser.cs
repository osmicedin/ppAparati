using System.Globalization;

namespace PpEvidencija.Services;

public static class PpInputParser
{
    public static bool TryParsePositiveDecimal(string? text, out decimal number)
    {
        number = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        var separatorIndex = -1;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is ',' or '.')
            {
                if (separatorIndex >= 0 || index == 0 || index == value.Length - 1)
                {
                    return false;
                }

                separatorIndex = index;
            }
            else if (character is < '0' or > '9')
            {
                return false;
            }
        }

        // Inspect the text before parsing: decimal.TryParse can round very long fractions.
        if (separatorIndex >= 0
            && value.AsSpan(separatorIndex + 1).TrimEnd('0').Length > 2)
        {
            return false;
        }

        // A comma or dot is always a decimal separator, never a thousands separator.
        if (!decimal.TryParse(
                value.Replace(',', '.'),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed <= 0m)
        {
            return false;
        }

        number = parsed;
        return true;
    }
}
