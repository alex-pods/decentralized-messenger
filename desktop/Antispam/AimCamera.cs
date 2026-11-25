using System.Numerics;

namespace MessengerDesktop.Antispam;

/// <summary>Scope 1×, sensitivity 9. Raw counts; 800 DPI is the reference hardware setting.</summary>
public sealed class AimCamera
{
    public const double Gain = 2 * Math.PI / 108117 * 9;
    public const float FieldOfView = 58.72f * MathF.PI / 180;
    public static readonly Vector3 Position = new(0,2,-4);
    public const double TargetDistance = 320; // 8 world units × 40 reference units.
    public double Yaw { get; private set; }
    public double Pitch { get; private set; }
    public void Move(int dx, int dy)
    {
        Yaw -= dx * Gain;
        Pitch = Math.Clamp(Pitch - dy * Gain, -1.45, 1.45);
    }
    public Vector3 Direction => new((float)(-Math.Sin(Yaw) * Math.Cos(Pitch)),
        (float)Math.Sin(Pitch), (float)(-Math.Cos(Yaw) * Math.Cos(Pitch)));
    public (double X, double Y) AimPoint()
    {
        var z = -Math.Cos(Yaw) * Math.Cos(Pitch);
        if (z >= -.02) return (100000, 100000);
        var d = -TargetDistance / z;
        return (480 - Math.Sin(Yaw) * Math.Cos(Pitch) * d, 270 - Math.Sin(Pitch) * d);
    }
    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Direction, Vector3.UnitY);
    public static Matrix4x4 Projection(float aspect)
    {
        const float near = .05f, far = 120;
        var f = 1 / MathF.Tan(FieldOfView / 2);
        return new(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0,
            (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0);
    }
    public static string Countdown(double remainingMs) => remainingMs > 3000 || remainingMs <= 0
        ? "" : Math.Ceiling(remainingMs / 1000).ToString("0");
}
