using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using static Avalonia.OpenGL.GlConsts;

namespace MessengerDesktop.Antispam;

/// <summary>Four samples per pixel and a 24-bit depth buffer for smooth spheres and stable seams.</summary>
internal sealed class SceneFramebuffer
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Storage(int target, int samples, int format, int width, int height);
    Storage? _storage;
    int _frame, _color, _depth, _width, _height;
    bool _unsupported;
    public void Begin(GlInterface gl, int destination, int width, int height)
    {
        if (_unsupported) { gl.BindFramebuffer(GL_FRAMEBUFFER,destination); return; }
        if(_frame != 0 && _width == width && _height == height) { gl.BindFramebuffer(GL_FRAMEBUFFER,_frame); return; }
        Dispose(gl);
        var address = gl.GetProcAddress("glRenderbufferStorageMultisample");
        if (address == IntPtr.Zero || !gl.IsBlitFramebufferAvailable) { _unsupported=true; gl.BindFramebuffer(GL_FRAMEBUFFER,destination); return; }
        _storage = Marshal.GetDelegateForFunctionPointer<Storage>(address);
        _frame=gl.GenFramebuffer(); gl.BindFramebuffer(GL_FRAMEBUFFER,_frame);
        _color=gl.GenRenderbuffer(); gl.BindRenderbuffer(GL_RENDERBUFFER,_color);
        _storage(GL_RENDERBUFFER,4,0x8058,width,height); // RGBA8
        gl.FramebufferRenderbuffer(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_RENDERBUFFER,_color);
        _depth=gl.GenRenderbuffer(); gl.BindRenderbuffer(GL_RENDERBUFFER,_depth);
        _storage(GL_RENDERBUFFER,4,0x81A6,width,height); // DEPTH_COMPONENT24
        gl.FramebufferRenderbuffer(GL_FRAMEBUFFER,GL_DEPTH_ATTACHMENT,GL_RENDERBUFFER,_depth);
        if(gl.CheckFramebufferStatus(GL_FRAMEBUFFER)!=GL_FRAMEBUFFER_COMPLETE)
        {
            Dispose(gl); _unsupported=true; gl.BindFramebuffer(GL_FRAMEBUFFER,destination); return;
        }
        _width=width; _height=height;
    }
    public void Resolve(GlInterface gl,int destination)
    {
        if(_frame==0)return;
        gl.BindFramebuffer(0x8CA8,_frame); // READ_FRAMEBUFFER
        gl.BindFramebuffer(0x8CA9,destination); // DRAW_FRAMEBUFFER
        gl.BlitFramebuffer(0,0,_width,_height,0,0,_width,_height,GL_COLOR_BUFFER_BIT,GL_NEAREST);
        gl.BindFramebuffer(GL_FRAMEBUFFER,destination);
    }
    public void Dispose(GlInterface gl)
    {
        if(_frame!=0)gl.DeleteFramebuffer(_frame);
        if(_color!=0)gl.DeleteRenderbuffer(_color);
        if(_depth!=0)gl.DeleteRenderbuffer(_depth);
        _frame=_color=_depth=0;
    }
}
