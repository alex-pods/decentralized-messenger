namespace MessengerBackend.Core;

/// <summary>Курсорная пагинация истории: limit 1..100 (default 50), before_id — положительный id.</summary>
public static class Paging
{
    public static (int? Limit, long? Before, IResult? Error) Parse(HttpContext ctx)
    {
        int? limit = null; long? before = null;
        var ls = ctx.Request.Query["limit"].ToString();
        if (ls.Length > 0)
        {
            if (!int.TryParse(ls, out var lv) || lv < 1 || lv > 100)
                return (null, null, Errors.Err(400, "limit должен быть целым от 1 до 100"));
            limit = lv;
        }
        var bs = ctx.Request.Query["before_id"].ToString();
        if (bs.Length > 0)
        {
            if (!long.TryParse(bs, out var bv) || bv < 1)
                return (null, null, Errors.Err(400, "before_id должен быть положительным id сообщения"));
            before = bv;
        }
        return (limit, before, null);
    }
}
