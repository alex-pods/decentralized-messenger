using System.Numerics;
using System.Runtime.InteropServices;

namespace MessengerDesktop.Antispam;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly record struct SceneVertex(Vector3 Position, Vector3 Normal);
internal static class SceneGeometry
{
    public static SceneVertex[] Sphere(int columns = 32, int rows = 24)
    {
        var result = new List<SceneVertex>();
        Vector3 Point(int x, int y)
        {
            var theta = x * MathF.Tau / columns; var phi = y * MathF.PI / rows;
            return new(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
        }
        void Add(Vector3 p) => result.Add(new(p, p));
        for (var y = 0; y < rows; y++) for (var x = 0; x < columns; x++)
        {
            var a = Point(x,y); var b = Point(x+1,y); var c = Point(x+1,y+1); var d = Point(x,y+1);
            Add(a); Add(b); Add(c); Add(a); Add(c); Add(d);
        }
        return result.ToArray();
    }
    public static SceneVertex[] Quad => new[] { new Vector3(-.5f,-.5f,0), new Vector3(.5f,-.5f,0),
        new Vector3(.5f,.5f,0), new Vector3(-.5f,-.5f,0), new Vector3(.5f,.5f,0), new Vector3(-.5f,.5f,0) }
        .Select(p => new SceneVertex(p,Vector3.UnitZ)).ToArray();
    public static SceneVertex[] Ring()
    {
        var points = new List<SceneVertex>();
        for(var i=0;i<64;i++)
        {
            var a=i*MathF.Tau/64; var b=(i+1)*MathF.Tau/64;
            Vector3 P(float r,float t)=>new(r*MathF.Cos(t),r*MathF.Sin(t),0);
            foreach(var p in new[]{P(.5f,a),P(.54f,a),P(.54f,b),P(.5f,a),P(.54f,b),P(.5f,b)}) points.Add(new(p,Vector3.UnitZ));
        }
        return points.ToArray();
    }
}
