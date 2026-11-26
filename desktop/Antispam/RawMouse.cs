using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MessengerDesktop.Antispam;

// A message-only window receives relative hardware mouse counts.
// No mouse acceleration, cursor warping, hooks or system DPI changes.
internal sealed class RawMouse : IDisposable
{
    readonly SubclassProc _callback;
    readonly Action<int, int, bool> _input;
    readonly IntPtr _window;
    readonly IntPtr _owner;
    Rect _previousClip;
    Point _previousPosition;
    bool _captured;
    int _hideCalls;
    bool _disposed;

    public RawMouse(IntPtr owner, Action<int, int, bool> input)
    {
        _input = input; _callback = WindowProc; _owner = owner;
        _window = CreateWindowEx(0, "STATIC", "Messenger aim input", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_window == IntPtr.Zero) throw new Win32Exception();
        if (!SetWindowSubclass(_window, _callback, 1, 0)) { DestroyWindow(_window); throw new Win32Exception(); }
        var device = new RawDevice { UsagePage = 1, Usage = 2, Flags = 0x100, Target = _window };
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawDevice>())) { Dispose(); throw new Win32Exception(); }
        if (!GetWindowRect(owner, out var rect) || !GetClipCursor(out _previousClip) || !GetCursorPos(out _previousPosition)) { Dispose(); throw new Win32Exception(); }
        var center = new Rect { Left = (rect.Left + rect.Right) / 2, Top = (rect.Top + rect.Bottom) / 2 };
        center.Right = center.Left + 1; center.Bottom = center.Top + 1;
        if (!SetWindowSubclass(owner, _callback, 2, 0)) { Dispose(); throw new Win32Exception(); }
        _captured = true;
        if (!ClipCursor(ref center)) { Dispose(); throw new Win32Exception(); }
        int count;
        do { count = ShowCursor(false); _hideCalls++; } while (count >= 0 && _hideCalls < 32);
        SetCursor(IntPtr.Zero);
    }

    IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (!_disposed && hwnd == _owner && message == 0x20) { SetCursor(IntPtr.Zero); return new IntPtr(1); }
        if (message == 0xff && !_disposed)
        {
            uint size = 0;
            var headerSize = (uint)(8 + IntPtr.Size * 2);
            GetRawInputData(lParam, 0x10000003, IntPtr.Zero, ref size, headerSize);
            if (size is > 0 and <= 1024)
            {
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetRawInputData(lParam, 0x10000003, buffer, ref size, headerSize) == size && Marshal.ReadInt32(buffer) == 0 && size >= headerSize + 24)
                    {
                        var mouse = buffer + (int)headerSize;
                        var absolute = (Marshal.ReadInt16(mouse) & 1) != 0;
                        _input(absolute ? 0 : Marshal.ReadInt32(mouse, 12), absolute ? 0 : Marshal.ReadInt32(mouse, 16), (Marshal.ReadInt16(mouse, 4) & 1) != 0);
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void KeepCursorHidden()
    {
        if(_disposed||!_captured||!IsForeground(_owner))return;
        // Framework hover updates can restore an arrow after the start overlay disappears.
        // Restore the capture cursor on the owning UI thread; balance every ShowCursor call on exit.
        if(CursorVisible)
        {
            var count=ShowCursor(false);_hideCalls++;
            while(count>=0&&_hideCalls<128){count=ShowCursor(false);_hideCalls++;}
        }
        SetCursor(IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_captured)
        {
            RemoveWindowSubclass(_owner, _callback, 2);
            ClipCursor(ref _previousClip);
            SetCursorPos(_previousPosition.X, _previousPosition.Y);
        }
        while (_hideCalls > 0) { ShowCursor(true); _hideCalls--; }
        RegisterRawInputDevices([new RawDevice { UsagePage = 1, Usage = 2, Flags = 1 }], 1, (uint)Marshal.SizeOf<RawDevice>());
        if (_window != IntPtr.Zero) { RemoveWindowSubclass(_window, _callback, 1); DestroyWindow(_window); }
    }

    [StructLayout(LayoutKind.Sequential)] struct RawDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct CursorInfo { public uint Size, Flags; public IntPtr Handle; public Point Position; }
    internal static bool CursorVisible { get { var info = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() }; return GetCursorInfo(ref info) && (info.Flags & 1) != 0 && info.Handle != IntPtr.Zero; } }
    internal static bool IsForeground(IntPtr window)=>window!=IntPtr.Zero&&GetForegroundWindow()==window;
    internal string Diagnostics
    {
        get
        {
            var info=new CursorInfo { Size=(uint)Marshal.SizeOf<CursorInfo>() };GetCursorInfo(ref info);
            return $"owner={_owner}, foreground={GetForegroundWindow()}, at={WindowFromPoint(info.Position)}, cursor={info.Handle}, flags={info.Flags}, hiddenCalls={_hideCalls}, position={info.Position.X},{info.Position.Y}";
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, nuint id, nuint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("comctl32.dll", SetLastError = true)] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr handle, uint command, IntPtr buffer, ref uint size, uint headerSize);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool ClipCursor(ref Rect rect);
    [DllImport("user32.dll")] static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] static extern bool GetClipCursor(out Rect rect);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern int ShowCursor(bool show);
    [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
}
