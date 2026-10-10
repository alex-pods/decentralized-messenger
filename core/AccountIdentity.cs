namespace Messenger.Core;

public sealed class AccountIdentity
{
    public string ServerBase { get; }
    public long UserId { get; }

    public AccountIdentity(string serverBase, long userId)
    {
        if (userId <= 0)
            throw new ArgumentOutOfRangeException(nameof(userId));

        if (!Uri.TryCreate(serverBase.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Нужен адрес HTTP/HTTPS сервера без query, fragment и учётных данных.",
                nameof(serverBase));
        }

        ServerBase = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        UserId = userId;
    }
}