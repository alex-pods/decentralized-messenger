namespace Messenger.Api.Antispam;

public sealed record AntispamOptions(bool Enabled, int Burst = 5, int WindowSeconds = 5)
{
    public static AntispamOptions Read(IConfiguration config)
    {
        var enabled = config.GetValue<bool?>("MESSENGER_ANTISPAM") ?? config.GetValue<bool>("Antispam:Enabled");
        var burst = config.GetValue<int?>("Antispam:Burst") ?? 5;
        var seconds = config.GetValue<int?>("Antispam:WindowSeconds") ?? 5;
        if (burst is < 2 or > 100 || seconds is < 1 or > 60)
            throw new InvalidOperationException("Antispam:Burst must be 2..100 and WindowSeconds must be 1..60.");
        return new(enabled, burst, seconds);
    }
}
