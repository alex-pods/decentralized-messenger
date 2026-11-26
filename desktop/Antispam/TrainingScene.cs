using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using static Avalonia.OpenGL.GlConsts;

namespace MessengerDesktop.Antispam;

/// <summary>Native GPU scene. Geometry, materials and camera are independently editable.</summary>
public sealed class TrainingScene : OpenGlControlBase
{
    const int GL_BLEND=0x0BE2;
    readonly AimCamera _camera;
    readonly SceneFramebuffer _framebuffer = new();
    readonly VideoPlane _videoPlane = new();
    internal ConcentrationVideo? Video { get; set; }
    internal TrainingPalette Palette { get; set; }=TrainingPalette.Neutral;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void BlendFunction(int source,int destination);
    BlendFunction? _blend;
    int _program, _vertexShader, _fragmentShader, _array, _sphere, _quad, _ring;
    int _modelLoc, _vpLoc, _redLoc, _greenLoc, _blueLoc, _litLoc, _roughnessLoc, _opacityLoc;
    Vector3[] _targets = [];
    bool _stopped;
    long _firstFrame;
    public int Frames { get; private set; }
    public string Renderer { get; private set; } = "";
    public double FramesPerSecond => Frames < 2 ? 0 : (Frames-1)/Stopwatch.GetElapsedTime(_firstFrame).TotalSeconds;
    public void Stop() => _stopped = true;
    readonly List<(Vector3 Point, long Start)> _hits = [];
    public event Action? Ready;
    public event Action? Failed;
    public TrainingScene(AimCamera camera) { _camera = camera; IsHitTestVisible = false; }
    public void Targets(Vector3[] targets) { _targets = targets; RequestNextFrameRendering(); }
    public void Hit(Vector3 point) { _hits.Add((point, Stopwatch.GetTimestamp())); RequestNextFrameRendering(); }
    public bool OnTarget => _targets.Any(p=>{var d=p-AimCamera.Position;var along=Vector3.Dot(d,_camera.Direction);return along>0 && d.LengthSquared()-along*along<=.44f*.44f;});

    protected override unsafe void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            Renderer = gl.GetString(GL_RENDERER) ?? "Unknown";
            _vertexShader = gl.CreateShader(GL_VERTEX_SHADER); _fragmentShader = gl.CreateShader(GL_FRAGMENT_SHADER);
            if (gl.CompileShaderAndGetError(_vertexShader, SceneMaterials.Source(GlVersion, false)) is { } ve) throw new InvalidOperationException(ve);
            if (gl.CompileShaderAndGetError(_fragmentShader, SceneMaterials.Source(GlVersion, true)) is { } fe) throw new InvalidOperationException(fe);
            _program = gl.CreateProgram(); gl.AttachShader(_program, _vertexShader); gl.AttachShader(_program, _fragmentShader);
            gl.BindAttribLocationString(_program, 0, "position"); gl.BindAttribLocationString(_program, 1, "normal");
            if (gl.LinkProgramAndGetError(_program) is { } error) throw new InvalidOperationException(error);
            _modelLoc = gl.GetUniformLocationString(_program,"model"); _vpLoc = gl.GetUniformLocationString(_program,"viewProjection");
            _redLoc=gl.GetUniformLocationString(_program,"red"); _greenLoc=gl.GetUniformLocationString(_program,"green");
            _blueLoc=gl.GetUniformLocationString(_program,"blue"); _litLoc=gl.GetUniformLocationString(_program,"lit");
            _roughnessLoc=gl.GetUniformLocationString(_program,"roughness");
            _opacityLoc=gl.GetUniformLocationString(_program,"opacity");
            _blend=Marshal.GetDelegateForFunctionPointer<BlendFunction>(gl.GetProcAddress("glBlendFunc"));
            _videoPlane.Init(gl,GlVersion);
            _array = gl.GenVertexArray(); gl.BindVertexArray(_array);
            _sphere = Upload(gl,SceneGeometry.Sphere()); _quad = Upload(gl,SceneGeometry.Quad); _ring = Upload(gl,SceneGeometry.Ring());
            Dispatcher.UIThread.Post(() => Ready?.Invoke());
        }
        catch { Dispatcher.UIThread.Post(() => Failed?.Invoke()); }
    }
    static unsafe int Upload(GlInterface gl, SceneVertex[] vertices)
    {
        var buffer = gl.GenBuffer(); gl.BindBuffer(GL_ARRAY_BUFFER,buffer);
        fixed(SceneVertex* ptr=vertices) gl.BufferData(GL_ARRAY_BUFFER,vertices.Length*sizeof(SceneVertex),(IntPtr)ptr,GL_STATIC_DRAW);
        return buffer;
    }
    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if(Frames++ == 0) _firstFrame=Stopwatch.GetTimestamp();
        var scale=TopLevel.GetTopLevel(this)?.RenderScaling??1;
        var width=Math.Max(1,(int)(Bounds.Width*scale)); var height=Math.Max(1,(int)(Bounds.Height*scale));
        _framebuffer.Begin(gl,fb,width,height);
        gl.Viewport(0,0,width,height); gl.Disable(GL_SCISSOR_TEST); gl.Disable(GL_CULL_FACE); gl.Disable(GL_BLEND);
        gl.ClearColor(24/255f,25/255f,27/255f,1); gl.ClearDepth(1); gl.DepthMask(1); gl.DepthFunc(GL_LESS);
        gl.Clear(GL_COLOR_BUFFER_BIT|GL_DEPTH_BUFFER_BIT); gl.Enable(GL_DEPTH_TEST);
        if(_sphere==0 || _quad==0) { _framebuffer.Resolve(gl,fb); return; }
        gl.UseProgram(_program); gl.BindVertexArray(_array);
        var vp=_camera.View*AimCamera.Projection((float)width/height); gl.UniformMatrix4fv(_vpLoc,1,false,&vp);
        Draw(gl,_quad,6,TrainingBackdrop.Matrix(),0x222426,true);
        // The concentration backdrop continues behind the complete stand. An invisible floor
        // must not write depth: that would still slice the lower half off the video.
        if(Video==null)
        {
            Draw(gl,_quad,6,Matrix4x4.CreateScale(100,100,1)*Matrix4x4.CreateRotationX(-MathF.PI/2)*Matrix4x4.CreateTranslation(0,TrainingBackdrop.Floor,-20),0x171819,true);
            foreach(var x in new[]{-50,50}) Draw(gl,_quad,6,Matrix4x4.CreateScale(.025f,TrainingBackdrop.Height,1)*Matrix4x4.CreateTranslation(x,TrainingBackdrop.CenterY,-43.98f),0x2c414f,true);
            Draw(gl,_quad,6,Matrix4x4.CreateScale(100,.07f,1)*Matrix4x4.CreateTranslation(0,-3.9f,-43.98f),0x293d4a,true);
        }
        var bx=TrainingStand.GridHalfWidth; var by=TrainingStand.GridHalfHeight;
        if(Video is {} video) { _videoPlane.Draw(gl,video,_quad,vp);gl.UseProgram(_program); }
        // 60% transparent panel: alpha=0.4. It is a freestanding object, not a wall decal.
        gl.Enable(GL_BLEND);_blend!(0x0302,0x0303);gl.DepthMask(0);
        Draw(gl,_quad,6,TrainingStand.Plane(TrainingStand.HalfWidth*2,TrainingStand.HalfHeight*2),0x161719,false,1,.4f);
        var lineWidth=2*(AimCamera.Position.Z-TrainingStand.Depth)*MathF.Tan(AimCamera.FieldOfView/2)/height;
        // All transparent pieces share a plane; inset the stroke so it follows the panel edge exactly.
        foreach(var x in new[]{-TrainingStand.HalfWidth+lineWidth/2,TrainingStand.HalfWidth-lineWidth/2})
            Draw(gl,_quad,6,TrainingStand.Plane(lineWidth,TrainingStand.HalfHeight*2,x),0xA0A0A0,false,1,.45f);
        foreach(var y in new[]{2-TrainingStand.HalfHeight+lineWidth/2,2+TrainingStand.HalfHeight-lineWidth/2})
            Draw(gl,_quad,6,TrainingStand.Plane(TrainingStand.HalfWidth*2-lineWidth*2,lineWidth,0,y),0xA0A0A0,false,1,.45f);
        for(var x=-3;x<=3;x++) Draw(gl,_quad,6,TrainingStand.Plane(lineWidth*.75f,by*2,x*bx/3),0xA0A0A0,false,1,.25f);
        for(var y=-2;y<=2;y++) Draw(gl,_quad,6,TrainingStand.Plane(bx*2,lineWidth*.75f,0,2+y*by/2),0xA0A0A0,false,1,.25f);
        gl.DepthMask(1);gl.Disable(GL_BLEND);
        var origin=AimCamera.Position; var direction=_camera.Direction;
        foreach(var p in _targets)
        {
            var d=p-origin;var along=Vector3.Dot(d,direction);
            var aimed=along>0&&d.LengthSquared()-along*along<=.44f*.44f;
            Draw(gl,_sphere,32*24*6,Matrix4x4.CreateScale(.44f)*Matrix4x4.CreateTranslation(p),aimed?Palette.Hover:Palette.Target,true,.72f);
        }
        for(var i=_hits.Count-1;i>=0;i--)
        {
            var (p,start)=_hits[i]; var t=(float)Stopwatch.GetElapsedTime(start).TotalMilliseconds/160;
            if(t>=1){_hits.RemoveAt(i);continue;}
            var color=Vector3.Lerp(new((Palette.Hover>>16)&255,(Palette.Hover>>8)&255,Palette.Hover&255),new(24,25,27),t);
            Draw(gl,_ring,64*6,Matrix4x4.CreateScale(1+.7f*t)*Matrix4x4.CreateTranslation(p.X,p.Y,-11.95f),((int)color.X<<16)|((int)color.Y<<8)|(int)color.Z);
        }
        gl.BindVertexArray(0); gl.BindBuffer(GL_ARRAY_BUFFER,0); gl.UseProgram(0);
        _framebuffer.Resolve(gl,fb);
        // Drive the next frame from the compositor, not a 16ms input/heartbeat timer.
        if(!_stopped) RequestNextFrameRendering();
    }
    unsafe void Draw(GlInterface gl,int buffer,int count,Matrix4x4 model,int color,bool lit=false,float roughness=1,float opacity=1)
    {
        gl.BindBuffer(GL_ARRAY_BUFFER,buffer);
        gl.VertexAttribPointer(0,3,GL_FLOAT,0,24,IntPtr.Zero); gl.VertexAttribPointer(1,3,GL_FLOAT,0,24,12);
        gl.EnableVertexAttribArray(0); gl.EnableVertexAttribArray(1);
        gl.UniformMatrix4fv(_modelLoc,1,false,&model);
        gl.Uniform1f(_redLoc,((color>>16)&255)/255f); gl.Uniform1f(_greenLoc,((color>>8)&255)/255f); gl.Uniform1f(_blueLoc,(color&255)/255f); gl.Uniform1f(_litLoc,lit?1:0);
        gl.Uniform1f(_roughnessLoc,roughness);
        gl.Uniform1f(_opacityLoc,opacity);
        gl.DrawArrays(GL_TRIANGLES,0,count);
    }
    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _framebuffer.Dispose(gl);
        _videoPlane.Dispose(gl);
        foreach(var buffer in new[]{_sphere,_quad,_ring}) if(buffer!=0)gl.DeleteBuffer(buffer);
        if(_array!=0)gl.DeleteVertexArray(_array);
        if(_program!=0)gl.DeleteProgram(_program);
        if(_vertexShader!=0)gl.DeleteShader(_vertexShader); if(_fragmentShader!=0)gl.DeleteShader(_fragmentShader);
    }
}
