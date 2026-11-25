using Avalonia.OpenGL;

namespace MessengerDesktop.Antispam;

internal static class SceneMaterials
{
    public static string Source(GlVersion version, bool fragment)
    {
        var modern = version.Type == GlProfileType.OpenGL && version.Major >= 3;
        var prefix = modern ? "#version 150\n" : version.Type == GlProfileType.OpenGLES
            ? "#version 100\nprecision mediump float;\n" : "#version 120\n";
        var shader = fragment ? Fragment : Vertex;
        if (modern) shader = shader.Replace("attribute", "in").Replace("varying", fragment ? "in" : "out")
            .Replace("//OUTPUT", "out vec4 outputColor;").Replace("gl_FragColor", "outputColor");
        return prefix + shader;
    }
    const string Vertex = """
        attribute vec3 position;
        attribute vec3 normal;
        uniform mat4 model;
        uniform mat4 viewProjection;
        varying vec3 world;
        varying vec3 surfaceNormal;
        varying float viewDepth;
        void main() {
            world = (model * vec4(position, 1.0)).xyz;
            surfaceNormal = normalize((model * vec4(normal, 0.0)).xyz);
            gl_Position = viewProjection * vec4(world, 1.0);
            viewDepth = gl_Position.w;
        }
        """;
    const string Fragment = """
        varying vec3 world;
        varying vec3 surfaceNormal;
        varying float viewDepth;
        uniform float red;
        uniform float green;
        uniform float blue;
        uniform float lit;
        uniform float roughness;
        uniform float opacity;
        //OUTPUT
        vec3 linearColor(vec3 c) {
            return mix(c/12.92, pow((c+.055)/1.055,vec3(2.4)), step(vec3(.04045),c));
        }
        vec3 srgb(vec3 c) {
            return mix(12.92*c,1.055*pow(max(c,vec3(0.0)),vec3(1.0/2.4))-.055,step(vec3(.0031308),c));
        }
        // Three.js ACES filmic transform (MIT), exposure 1, matching the reference renderer.
        vec3 filmic(vec3 c) {
            c = mat3(.59719,.076,.0284,.35458,.90834,.13383,.04823,.01566,.83777) * (c/.6);
            c = (c*(c+.0245786)-.000090537)/(c*(.983729*c+.432951)+.238081);
            return clamp(mat3(1.60475,-.10208,-.00327,-.53108,1.10813,-.07276,-.07367,-.00605,1.07602)*c,0.0,1.0);
        }
        vec3 directLight(vec3 n, vec3 v, vec3 l, vec3 diffuse, float intensity) {
            vec3 h = normalize(v+l);
            float nl=max(dot(n,l),0.0), nv=max(dot(n,v),.0001);
            float nh=max(dot(n,h),0.0), vh=max(dot(v,h),0.0);
            float a=roughness*roughness, a2=a*a;
            float d=a2/(3.14159265*pow(nh*nh*(a2-1.0)+1.0,2.0));
            float visibility=.5/max(nl*sqrt(a2+(1.0-a2)*nv*nv)+nv*sqrt(a2+(1.0-a2)*nl*nl),.00001);
            float fresnel=.04+.96*exp2((-5.55473*vh-6.98316)*vh);
            return vec3(1.0)*intensity*nl*(diffuse/3.14159265+vec3(fresnel*visibility*d));
        }
        void main() {
            vec3 n = normalize(surfaceNormal);
            vec3 base = linearColor(vec3(red,green,blue));
            vec3 color = base;
            if(lit > .5) {
                vec3 v=normalize(vec3(0.0,2.0,-4.0)-world);
                vec3 hemi=2.0*mix(linearColor(vec3(48.0)/255.0),linearColor(vec3(225.0)/255.0),n.y*.5+.5);
                color=base*(vec3(.8)+hemi)/3.14159265;
                color+=directLight(n,v,normalize(vec3(-6.0,9.0,5.0)),base,2.4);
                color+=directLight(n,v,normalize(vec3(7.0,3.0,1.0)),base,1.0);
            }
            color=srgb(filmic(color));
            float fog = smoothstep(35.0,85.0,viewDepth);
            gl_FragColor = vec4(mix(color,vec3(24.0,25.0,27.0)/255.0,fog),opacity);
        }
        """;
}
