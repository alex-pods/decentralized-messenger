using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using static Avalonia.OpenGL.GlConsts;

namespace MessengerDesktop.Antispam;

internal sealed class VideoPlane
{
    const int GL_TEXTURE_WRAP_S=0x2802,GL_TEXTURE_WRAP_T=0x2803,GL_CLAMP_TO_EDGE=0x812F;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void UniformInt(int location,int value);
    int _program,_vertex,_fragment,_texture,_matrix,_opacity,_uploaded,_cropX,_cropY;
    public void Init(GlInterface gl,GlVersion version)
    {
        var modern=version.Type==GlProfileType.OpenGL&&version.Major>=3;
        var prefix=modern?"#version 150\n":version.Type==GlProfileType.OpenGLES?"#version 100\nprecision mediump float;\n":"#version 120\n";
        var vertex="attribute vec3 position; varying vec2 uv; uniform mat4 matrix; uniform float cropX; uniform float cropY; void main(){uv=vec2(.5+position.x*cropX,.5-position.y*cropY);gl_Position=matrix*vec4(position,1.0);}";
        var fragment="varying vec2 uv; uniform sampler2D video; uniform float opacity; //OUTPUT\nvoid main(){vec3 color=texture2D(video,uv).bgr;gl_FragColor=vec4(color*opacity,1.0);}";
        if(modern){vertex=vertex.Replace("attribute","in").Replace("varying","out");fragment=fragment.Replace("varying","in").Replace("//OUTPUT","out vec4 outputColor;").Replace("gl_FragColor","outputColor").Replace("texture2D","texture");}
        _vertex=gl.CreateShader(GL_VERTEX_SHADER);_fragment=gl.CreateShader(GL_FRAGMENT_SHADER);
        if(gl.CompileShaderAndGetError(_vertex,prefix+vertex) is {} ve)throw new InvalidOperationException(ve);
        if(gl.CompileShaderAndGetError(_fragment,prefix+fragment) is {} fe)throw new InvalidOperationException(fe);
        _program=gl.CreateProgram();gl.AttachShader(_program,_vertex);gl.AttachShader(_program,_fragment);gl.BindAttribLocationString(_program,0,"position");
        if(gl.LinkProgramAndGetError(_program) is {} error)throw new InvalidOperationException(error);
        _matrix=gl.GetUniformLocationString(_program,"matrix");_opacity=gl.GetUniformLocationString(_program,"opacity");
        _cropX=gl.GetUniformLocationString(_program,"cropX");_cropY=gl.GetUniformLocationString(_program,"cropY");
        gl.UseProgram(_program);
        Marshal.GetDelegateForFunctionPointer<UniformInt>(gl.GetProcAddress("glUniform1i"))(gl.GetUniformLocationString(_program,"video"),0);
        _texture=gl.GenTexture();gl.BindTexture(GL_TEXTURE_2D,_texture);
        gl.TexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MIN_FILTER,GL_LINEAR);gl.TexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAG_FILTER,GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_S,GL_CLAMP_TO_EDGE);gl.TexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_T,GL_CLAMP_TO_EDGE);
        gl.TexImage2D(GL_TEXTURE_2D,0,GL_RGBA,ConcentrationVideo.Width,ConcentrationVideo.Height,0,GL_RGBA,GL_UNSIGNED_BYTE,IntPtr.Zero);
    }
    public unsafe void Draw(GlInterface gl,ConcentrationVideo video,int quad,Matrix4x4 viewProjection)
    {
        gl.ActiveTexture(GL_TEXTURE0);gl.BindTexture(GL_TEXTURE_2D,_texture);
        video.ReadFrame(ref _uploaded,pixels=>gl.TexImage2D(GL_TEXTURE_2D,0,GL_RGBA,ConcentrationVideo.Width,ConcentrationVideo.Height,0,GL_RGBA,GL_UNSIGNED_BYTE,pixels));
        if(_uploaded==0)return;
        gl.UseProgram(_program);gl.BindBuffer(GL_ARRAY_BUFFER,quad);
        gl.VertexAttribPointer(0,3,GL_FLOAT,0,24,IntPtr.Zero);gl.EnableVertexAttribArray(0);
        // Project onto the far wall, behind the freestanding target panel.
        var matrix=TrainingBackdrop.Matrix(.05f)*viewProjection;
        var crop=TrainingBackdrop.TextureScale;gl.Uniform1f(_cropX,crop.X);gl.Uniform1f(_cropY,crop.Y);
        gl.UniformMatrix4fv(_matrix,1,false,&matrix);gl.Uniform1f(_opacity,video.Opacity);
        gl.DrawArrays(GL_TRIANGLES,0,6);
    }
    public void Dispose(GlInterface gl)
    {
        if(_texture!=0)gl.DeleteTexture(_texture);if(_program!=0)gl.DeleteProgram(_program);
        if(_vertex!=0)gl.DeleteShader(_vertex);if(_fragment!=0)gl.DeleteShader(_fragment);
    }
}
