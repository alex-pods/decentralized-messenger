using System.Globalization;
using System.Runtime.CompilerServices;
using Dapper;

namespace Messenger.Api.Infrastructure;

internal static class SqliteDapper
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }
}

internal static class SqliteTime
{
    private const DateTimeStyles Styles =
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    public static DateTimeOffset Parse(string text)
        => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, Styles);

    public static DateTimeOffset? ParseOrNull(string? text)
        => text is null ? null : Parse(text);
}
