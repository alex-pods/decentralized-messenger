using System.Data;
using System.Diagnostics.CodeAnalysis;
using Dapper;

namespace Messenger.Api.Infrastructure;

public sealed record Secret(string Value)
{
    [return: NotNullIfNotNull(nameof(value))]
    public static implicit operator Secret?(string? value) => value is null ? null : new Secret(value);

    [return: NotNullIfNotNull(nameof(secret))]
    public static implicit operator string?(Secret? secret) => secret?.Value;
    public override string ToString() => "***";
}

public sealed class SecretTypeHandler(SecretCipher cipher) : SqlMapper.TypeHandler<Secret?>
{
  public override void SetValue(IDbDataParameter parameter, Secret? secret)
  {
    parameter.DbType = DbType.String;
    parameter.Value = secret is null ? DBNull.Value : cipher.Seal(secret.Value);
  }

  public override Secret? Parse(object value) =>
      value is null or DBNull ? null : new Secret(cipher.Open((string)value));
}
