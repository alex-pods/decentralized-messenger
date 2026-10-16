using System.Data;
using Dapper;
using MessengerBackend.Core;
using MessengerBackend.Hubs;
using Microsoft.AspNetCore.Mvc;

namespace MessengerBackend.Endpoints;

public static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this WebApplication app)
    {
        var filesRoot = AppCtx.FilesRootFromEnv();
        var hub = app.Services.GetRequiredService<Hub>();

        app.MapGet("/users/me", (HttpContext ctx) =>
        {
            var me = Accounts.RequireUser(ctx);
            return Db.Read(c => Results.Json(Profile(UserById(c, me.Id))));
        })
        .WithTags("Profiles").WithName("getMe").WithSummary("Мой профиль")
        .WithDescription("tag, display_name, path_to_avatar_file, bio, join_date, last_seen_at " +
                         "(обновляется при запросах и WS-подключениях).")
        .Produces<ProfileOut>(200);

        app.MapPatch("/users/me", (HttpContext ctx, [FromBody] UpdateProfileIn r) =>
        {
            var me = Accounts.RequireUser(ctx);
            var displayName = r.DisplayName?.Trim();
            if (displayName is { Length: 0 } or { Length: > 32 })
                return Errors.Err(400, "display_name должен быть 1-32 символа");
            if (displayName is null && r.Bio is null)
                return Errors.Err(400, "нечего обновлять: передайте display_name и/или bio");
            if (r.Bio is { Length: > 500 })
                return Errors.Err(400, "bio слишком длинный (макс. 500)");
            return Db.Write<IResult>(c =>
            {
                // null/отсутствие = без изменений; bio:"" — очистить
                c.Execute("UPDATE users SET display_name=COALESCE(@d, display_name), bio=@b WHERE id=@u",
                    new { d = displayName, b = r.Bio ?? CurrentBio(c, me.Id), u = me.Id });
                return Results.NoContent();
            });
        })
        .WithTags("Profiles").WithName("updateMe").WithSummary("Частичное изменение профиля")
        .WithDescription("PATCH-семантика: отсутствующее или null поле НЕ меняется; bio=\"\" очищает bio; " +
                         "display_name пустым быть не может (400).")
        .Produces(204);

        app.MapGet("/users/search", (HttpContext ctx, [FromQuery] string query) =>
        {
            Accounts.RequireUser(ctx);
            var q = (query ?? "").Trim();
            if (q.Length < 1) return Errors.Err(400, "query обязателен");
            var like = $"%{q.Replace("@", "@@").Replace("%", "@%").Replace("_", "@_")}%";
            return Db.Read(c => Results.Json(c.Query<UserRow>(
                """
                SELECT * FROM users WHERE tag LIKE @q ESCAPE '@' OR display_name LIKE @q ESCAPE '@'
                ORDER BY tag LIMIT 20
                """, new { q = like }).Select(Card).ToList()));
        })
        .WithTags("Profiles").WithName("searchUsers").WithSummary("Поиск пользователей")
        .WithDescription("Подстрока по tag или display_name, до 20 карточек [{id, tag, display_name}].")
        .Produces<List<UserCard>>(200);

        app.MapGet("/users/{user_id:long}", (HttpContext ctx, long user_id) =>
        {
            Accounts.RequireUser(ctx);
            return Db.Read(c => Results.Json(Profile(UserById(c, user_id))));
        })
        .WithTags("Profiles").WithName("getUser").WithSummary("Профиль по id")
        .WithDescription("Публичный профиль. 404 — пользователь не найден.")
        .Produces<ProfileOut>(200);

        app.MapPost("/users/me/avatar", async (HttpContext ctx) =>
        {
            var me = Accounts.RequireUser(ctx);
            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(); }
            catch (InvalidOperationException)
            {
                return Errors.Err(400, "нужен multipart/form-data с полем file");
            }
            var file = form.Files.FirstOrDefault(f => f.Name == "file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Errors.Err(400, "поле file обязательно");
            if (file.Length > Files.MaxAvatarBytes)
                return Errors.Err(413, $"аватар слишком большой: {file.Length} байт (макс. 5 МБ)");
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var data = ms.ToArray();
            if (!Files.IsValidAvatarImage(data, out var reason))
                return Errors.Err(400, $"invalid_avatar: {reason}", "invalid_avatar");
            var rel = $"avatars/u{me.Id}_{Guid.NewGuid():N}{Files.SafeExtension(file.FileName)}";
            await File.WriteAllBytesAsync(Files.AvatarAbsPath(filesRoot, rel), data);
            var result = Db.Write<IResult>(c =>
            {
                c.Execute("UPDATE users SET path_to_avatar_file=@p WHERE id=@u", new { p = rel, u = me.Id });
                return Results.Json(new { path_to_avatar_file = rel }, statusCode: 201);
            });
            var peers = Db.Read(c => c.Query<long>(
                """
                SELECT DISTINCT peer.user_id
                FROM chat_members mine
                JOIN chat_members peer ON peer.chat_id=mine.chat_id
                WHERE mine.user_id=@u AND peer.user_id<>@u
                """, new { u = me.Id }).ToArray());
            await hub.PublishToUsers(peers,
                new WsEvent("avatar_changed") { AlwaysDeliver = true, UserId = me.Id });
            return result;
        })
        .WithTags("Avatars").WithName("uploadAvatar").WithSummary("Загрузка своего аватара")
        .WithDescription("multipart/form-data, поле file (один файл, до 5 МБ → 413). Содержимое проверяется " +
                         "по сигнатуре: PNG/JPEG/WebP (PNG — габариты до 4096x4096). Текст/HTML/SVG и " +
                         "повреждённые изображения → 400 invalid_avatar; прежний аватар при ошибке сохраняется.")
        .DisableAntiforgery()
        .Produces(201);

        app.MapGet("/users/{user_id:long}/avatar", (HttpContext ctx, long user_id) =>
        {
            Accounts.RequireUser(ctx);
            var path = Db.Read(c => Files.AvatarPathOf(c, user_id));
            if (string.IsNullOrWhiteSpace(path)) return Errors.Err(404, "аватар не установлен");
            var abs = Files.AvatarAbsPath(filesRoot, path);
            return File.Exists(abs) ? Results.File(abs) : Errors.Err(404, "файл аватара отсутствует");
        })
        .WithTags("Avatars").WithName("getAvatar").WithSummary("Файл аватара")
        .WithDescription("Бинарный ответ (фактический Content-Type). 404 — аватар не установлен.")
        .Produces(200);
    }

    static UserRow UserById(IDbConnection c, long id) =>
        c.QueryFirstOrDefault<UserRow>("SELECT * FROM users WHERE id=@id", new { id })
        ?? throw new HttpException(404, "пользователь не найден");

    static UserCard Card(UserRow u) => new(u.Id, u.Tag, u.Display_Name);
    static ProfileOut Profile(UserRow u) =>
        new(u.Id, u.Tag, u.Display_Name, u.Path_To_Avatar_File, u.Bio, u.Join_Date, u.Last_Seen_At);
    static string? CurrentBio(IDbConnection c, long userId) =>
        c.ExecuteScalar<string?>("SELECT bio FROM users WHERE id=@u", new { u = userId });
}
