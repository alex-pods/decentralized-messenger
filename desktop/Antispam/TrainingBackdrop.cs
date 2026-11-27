using System.Numerics;

namespace MessengerDesktop.Antispam;

internal static class TrainingBackdrop
{
    public const float Width=100,Height=56.25f,Floor=-4,WallZ=-44;
    public const float CenterY=2;
    // A distant, full-height backdrop centered behind the targets and the neutral camera.
    // Its 16:9 shape keeps the source composition intact instead of cropping it into a banner.
    public static Matrix4x4 Matrix(float offset=0)=>Matrix4x4.CreateScale(Width,Height,1)*
        Matrix4x4.CreateTranslation(0,CenterY,WallZ+offset);
    public static Vector2 TextureScale
    {
        get
        {
            var source=ConcentrationVideo.Width/(float)ConcentrationVideo.Height;
            var destination=Width/Height;
            return destination>source?new(1,source/destination):new(destination/source,1);
        }
    }
}
