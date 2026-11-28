using System.Text.Json;
using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Common.Errors;
using Messenger.Api.Models;
namespace Messenger.Api.Repositories;

public sealed class ThemesRepository(DbConnectionFactory db)
{
    sealed class Row { public string Id { get; set; } = ""; public string OwnerId { get; set; } = ""; public int Version { get; set; } public string Document { get; set; } = ""; }
    public async Task<SharedTheme> Get(string id)
    {
        using var connection = db.Open();
        var row = await connection.QuerySingleOrDefaultAsync<Row>("SELECT id, owner_id AS OwnerId, version, document FROM shared_themes WHERE id=@id", new { id });
        return row == null ? throw AppException.NotFound("Theme not found") : new(row.Id, row.OwnerId, row.Version, 1, JsonSerializer.Deserialize<ThemeDocument>(row.Document)!);
    }
    public async Task<SharedTheme> Create(string owner, ThemeCreate request)
    {
        if (request.SchemaVersion != 1 || request.Theme == null) throw AppException.BadRequest("Unsupported theme schema");
        request.Theme.Validate();
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM shared_themes WHERE owner_id=@owner", new { owner }, transaction);
        if (count >= 100) throw AppException.Conflict("theme_limit", "Maximum 100 published themes per account");
        var id = Guid.NewGuid().ToString("N");
        await connection.ExecuteAsync("INSERT INTO shared_themes(id,owner_id,document) VALUES(@id,@owner,@document)", new { id, owner, document = JsonSerializer.Serialize(request.Theme) }, transaction);
        transaction.Commit();
        return new(id, owner, 1, 1, request.Theme);
    }
    public async Task<SharedTheme> Update(string owner, string id, ThemeUpdate request)
    {
        var old = await Get(id);
        if (old.OwnerId != owner) throw AppException.Forbidden("Only the theme owner can update it");
        if (old.Version != request.Version) throw AppException.Conflict("theme_version", "Theme changed; reload before updating");
        if (request.Theme == null) throw AppException.BadRequest("Theme patch is required");
        var theme = request.Theme.Apply(old.Theme); theme.Validate();
        using var connection = db.Open();
        var changed = await connection.ExecuteAsync("UPDATE shared_themes SET document=@document, version=version+1, updated_at=CURRENT_TIMESTAMP WHERE id=@id AND owner_id=@owner AND version=@version",
            new { document = JsonSerializer.Serialize(theme), id, owner, version = request.Version });
        if (changed == 0) throw AppException.Conflict("theme_version", "Theme changed; reload before updating");
        return new(id, owner, request.Version + 1, 1, theme);
    }
    public async Task Delete(string owner, string id)
    {
        var old = await Get(id);
        if (old.OwnerId != owner) throw AppException.Forbidden("Only the theme owner can delete it");
        using var connection = db.Open();
        await connection.ExecuteAsync("DELETE FROM shared_themes WHERE id=@id AND owner_id=@owner", new { id, owner });
    }
}
