using System.Text.RegularExpressions;
using Messenger.Api.Common.Errors;
namespace Messenger.Api.Models;

public sealed record ThemeDocument(string Name, bool Dark, string Background, string Surface, string Text, string Accent)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 64 || Name.Any(char.IsControl))
            throw AppException.BadRequest("Theme name must contain 1–64 printable characters");
        foreach (var color in new[] { Background, Surface, Text, Accent })
            if (color == null || !Regex.IsMatch(color, @"\A#[0-9a-fA-F]{6}\z"))
                throw AppException.BadRequest("Theme colors must be #RRGGBB");
    }
}
public sealed record ThemeCreate(int SchemaVersion, ThemeDocument Theme);
public sealed record ThemePatch(string? Name, bool? Dark, string? Background, string? Surface, string? Text, string? Accent)
{
    public ThemeDocument Apply(ThemeDocument old) => new(Name ?? old.Name, Dark ?? old.Dark, Background ?? old.Background,
        Surface ?? old.Surface, Text ?? old.Text, Accent ?? old.Accent);
}
public sealed record ThemeUpdate(int Version, ThemePatch Theme);
public sealed record SharedTheme(string Id, string OwnerId, int Version, int SchemaVersion, ThemeDocument Theme);
