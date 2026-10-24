using System.Globalization;

namespace Messenger.Api.Contracts.Responses;

internal static class ApiTimestamp
{
    public static string Format(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            throw new InvalidOperationException("Database contains an invalid timestamp");
        return timestamp.ToString("O", CultureInfo.InvariantCulture);
    }

    public static string? FormatNullable(string? value) => value is null ? null : Format(value);
}
