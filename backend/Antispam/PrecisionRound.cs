using System.Security.Cryptography;

namespace Messenger.Api.Antispam;

public sealed record AimPoint(double X, double Y);

// Port of the reference trainer's precision geometry and score. The server owns spawning.
public sealed class PrecisionRound
{
    public const int RequiredScore = 6000;
    public const int CountdownMs = 4000;
    public const int DurationMs = 30000;
    public const double TargetDistance = 320; // Camera z=-4, targets z=-12: 8 × 40.
    const double Radius = 17.6;
    static readonly AimPoint[] Grid = Enumerable.Range(0, 35).Select(i => new AimPoint(
        480 + (i % 7 - 3) * (.8 * 480 * Math.Tan(Math.PI / 8)) / 3,
        270 + (i / 7 - 2) * (.8 * 480 * Math.Tan(12 * Math.PI / 180)) / 2)).ToArray();
    readonly List<AimPoint> _targets = [];
    public IReadOnlyList<AimPoint> Targets => _targets;
    public int Hits { get; private set; }
    public int Misses { get; private set; }
    // Preserve the reference site's operation order: Math.round(hits * accuracy * 100).
    public int Score => Hits + Misses == 0 ? 0 : (int)Math.Round(Hits * (Hits / (double)(Hits + Misses)) * 100, MidpointRounding.AwayFromZero);

    public PrecisionRound() { for (var i = 0; i < 4; i++) Spawn(null); }
    void Spawn(AimPoint? removed)
    {
        var available = Grid.Where(p => p != removed && !_targets.Contains(p)).ToArray();
        _targets.Add(available[RandomNumberGenerator.GetInt32(available.Length)]);
    }

    public bool Click(double x, double y)
    {
        var index = _targets.FindIndex(q => Inside(x, y, q));
        if (index < 0) { Misses++; return false; }
        Hits++;
        var removed = _targets[index]; _targets.RemoveAt(index); Spawn(removed);
        return true;
    }

    public static bool Inside(double x, double y, AimPoint target)
    {
        var rx = x - 480; var ry = 270 - y; const double rz = -TargetDistance;
        var cx = target.X - 480; var cy = 270 - target.Y; const double cz = -TargetDistance;
        var dot = rx * cx + ry * cy + rz * cz;
        return dot > 0 && cx * cx + cy * cy + cz * cz - dot * dot / (rx * rx + ry * ry + rz * rz) <= Radius * Radius;
    }
}
