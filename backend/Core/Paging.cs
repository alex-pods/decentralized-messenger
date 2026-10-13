namespace MessengerBackend.Core;

/// <summary>Курсорная пагинация истории: limit 1..100 (default 50), before_id — положительный id.</summary>
public static class Paging
{
    public static (int? Limit, long? Before, long? After, IResult? Error) Parse(HttpContext ctx)
    {
        int? limit = null; long? before = null; long? after = null;
        var ls = ctx.Request.Query["limit"].ToString();
        if (ls.Length > 0)
        {
            if (!int.TryParse(ls, out var lv) || lv < 1 || lv > 100)
                return (null, null, null, Errors.Err(400, "limit должен быть целым от 1 до 100"));
            limit = lv;
        }
        foreach (var (name, value) in new[] { ("before_id", 0L), ("after_id", 0L) })
        {
            var raw = ctx.Request.Query[name].ToString();
            if (raw.Length == 0) continue;
            if (!long.TryParse(raw, out var v) || v < 1)
                return (null, null, null, Errors.Err(400, $"{name} должен быть положительным id сообщения"));
            if (name == "before_id") before = v; else after = v;
        }
        return (limit, before, after, null);
    }
}
