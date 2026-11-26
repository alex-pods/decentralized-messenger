using System.Numerics;

namespace MessengerDesktop.Antispam;

internal static class TrainingStand
{
    public const float CenterY=2,Depth=-12.12f;
    public static readonly float GridHalfWidth=.8f*12*MathF.Tan(MathF.PI/8);
    public static readonly float GridHalfHeight=.8f*12*MathF.Tan(12*MathF.PI/180);
    public static readonly float HalfWidth=GridHalfWidth+.6f,HalfHeight=GridHalfHeight+.6f;
    public static Matrix4x4 Plane(float width,float height,float x=0,float y=CenterY)=>
        Matrix4x4.CreateScale(width,height,1)*Matrix4x4.CreateTranslation(x,y,Depth);
    public static Vector2[] ProjectCorners(AimCamera camera,int width,int height)
    {
        var vp=camera.View*AimCamera.Projection(width/(float)height);
        return new[]{new Vector3(-HalfWidth,CenterY+HalfHeight,Depth),new Vector3(HalfWidth,CenterY+HalfHeight,Depth),
            new Vector3(HalfWidth,CenterY-HalfHeight,Depth),new Vector3(-HalfWidth,CenterY-HalfHeight,Depth)}.Select(p=>
            {var clip=Vector4.Transform(new Vector4(p,1),vp);return new Vector2((clip.X/clip.W+1)*width/2,(1-clip.Y/clip.W)*height/2);}).ToArray();
    }
}
