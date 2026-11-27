using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// Each accepted message has one premultiplied texture. DWM evaluates the horizontal
// animation after Commit. The scheduler still owns admission, position and expiry.
internal sealed class DanmakuComposition : IDisposable
{
    private sealed class Sprite
    {
        internal readonly IntPtr[] Visuals = new IntPtr[4];
        internal IntPtr Surface, Animation;
        internal int Revision, TextOpacity, Height, Seen;
        internal float X, Y, Speed;
    }

    private readonly Dictionary<FlyingDanmaku, Sprite> _sprites = new Dictionary<FlyingDanmaku, Sprite>();
    private readonly List<FlyingDanmaku> _expired = new List<FlyingDanmaku>();
    // Four disjoint rectangles describe the player area minus the SC reservation.
    // Crossing rows may use several clipped visuals, all sharing one surface and clock.
    private readonly RectangleF[] _clips = new RectangleF[4];
    private readonly IntPtr[] _branches = new IntPtr[4];
    private IntPtr _window, _device, _context, _composition, _target, _root, _opacity;
    private Rectangle _bounds;
    private RectangleF _reservation;
    private bool _visible, _dirty, _rebase = true;
    private int _alpha = -1, _generation, _lastDeviceCheck;

    internal IntPtr Window { get { return _window; } }

    internal static DanmakuComposition Create(IntPtr owner)
    {
        var presenter = new DanmakuComposition();
        try { presenter.Initialize(owner); return presenter; }
        catch { presenter.Dispose(); throw; }
    }

    private const string CompositionWindowClass = "LiveHub.DanmakuVisual";
    private const uint VisualWindowStyles = 0x082800a0; // NOREDIRECTIONBITMAP | LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE
    private const uint VisualWindowFlags = 0x88000000; // WS_POPUP | WS_DISABLED
    private static readonly WindowProc VisualWindowProc = DefWindowProcW;
    private static readonly object WindowClassLock = new object();
    private static bool _windowClassRegistered;

    private static void RegisterVisualWindowClass()
    {
        lock (WindowClassLock)
        {
            if (_windowClassRegistered) return;
            var wc = new WindowClass { Procedure = VisualWindowProc, Instance = GetModuleHandleW(null), ClassName = CompositionWindowClass };
            if (RegisterClassW(ref wc) == 0) throw new System.ComponentModel.Win32Exception();
            _windowClassRegistered = true;
        }
    }

    private void Initialize(IntPtr owner)
    {
        // Use a background-free window class so the native control paint path cannot
        // mask the composition content. The visual HWND is disabled and transparent.
        RegisterVisualWindowClass();
        _window = CreateWindowExW(VisualWindowStyles, CompositionWindowClass, "LiveHub Danmaku Composition", VisualWindowFlags,
            -10000, -10000, 1, 1, owner, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_window == IntPtr.Zero || !SetLayeredWindowAttributes(_window, 0, 255, 2))
            throw new System.ComponentModel.Win32Exception();

        int featureLevel;
        Check(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
            out _device, out featureLevel, out _context));
        IntPtr dxgi = IntPtr.Zero;
        try
        {
            Guid iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Check(Vtable<Query>(_device, 0)(_device, ref iid, out dxgi));
            iid = new Guid("c37ea93a-e7aa-450d-b16f-9746cb0407f3");
            Check(DCompositionCreateDevice(dxgi, ref iid, out _composition));
        }
        finally { Release(ref dxgi); }

        Check(Vtable<CreateTarget>(_composition, 6)(_composition, _window, 1, out _target));
        _root = CreateObject(7); // IDCompositionDevice::CreateVisual
        _opacity = CreateObject(23); // CreateEffectGroup
        SetObject(_root, 10, _opacity); // SetEffect
        SetObject(_target, 3, _root); // SetRoot
        for (int index = 0; index < _branches.Length; index++)
        {
            _branches[index] = CreateObject(7);
            AddVisual(_root, _branches[index]);
        }
        _dirty = true;
    }

    internal void Present(DanmakuRenderer renderer, Rectangle bounds, int alpha, long stamp, bool clockReset)
    {
        if (!IsWindow(_window)) throw new InvalidOperationException("DirectComposition window lost");
        int tick = Environment.TickCount;
        if (_lastDeviceCheck == 0 || unchecked(tick - _lastDeviceCheck) >= 1000)
        {
            int valid;
            Check(Vtable<CheckState>(_composition, 26)(_composition, out valid));
            if (valid == 0) throw new InvalidOperationException("DirectComposition device lost");
            _lastDeviceCheck = tick;
        }
        if (_alpha != alpha)
        {
            SetFloat(_opacity, 4, alpha / 255f); // IDCompositionEffectGroup::SetOpacity(float)
            _alpha = alpha;
            _dirty = true;
        }
        RectangleF reservation = renderer.CompositionScReservation;
        bool resized = bounds.Size != _bounds.Size;
        if (resized || reservation != _reservation)
        {
            SetClipRegions(bounds.Size, reservation, _clips);
            for (int index = 0; index < _branches.Length; index++)
            {
                RectangleF clip = _clips[index];
                var edges = new EdgesF { Left = clip.Left, Top = clip.Top, Right = clip.Right, Bottom = clip.Bottom };
                Check(Vtable<SetRect>(_branches[index], 14)(_branches[index], ref edges)); // SetClip(D2D_RECT_F)
            }
            _reservation = reservation;
            _dirty = true;
        }
        SyncMessages(renderer, bounds.Width, stamp, resized || clockReset || _rebase);
        if (_dirty)
        {
            Check(Vtable<Call>(_composition, 3)(_composition)); // Commit
            _dirty = false;
        }
        if (bounds != _bounds)
        {
            const uint NoZOrderNoActivate = 0x0014;
            if (!SetWindowPos(_window, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, NoZOrderNoActivate))
                throw new System.ComponentModel.Win32Exception();
            _bounds = bounds;
        }
        _rebase = false;
    }

    internal void Show()
    {
        if (_visible) return;
        ShowWindow(_window, 4); // SW_SHOWNOACTIVATE
        _visible = true;
    }

    internal void Hide()
    {
        if (!_visible) return;
        ShowWindow(_window, 0);
        _visible = false;
        _rebase = true;
        // Hidden messages are still scheduled on the CPU. Drop all submitted objects
        // here so they do not retain GPU allocations throughout a long hidden period.
        foreach (IntPtr branch in _branches) Check(Vtable<Call>(branch, 18)(branch)); // RemoveAllVisuals
        Check(Vtable<Call>(_composition, 3)(_composition));
        ReleaseSprites();
    }

    private void SyncMessages(DanmakuRenderer renderer, int width, long stamp, bool rebase)
    {
        unchecked { _generation++; }
        foreach (FlyingDanmaku message in renderer.CompositionMessages)
        {
            Sprite sprite;
            bool added = !_sprites.TryGetValue(message, out sprite);
            if (added)
            {
                sprite = new Sprite();
                _sprites.Add(message, sprite);
            }
            bool changed = added || sprite.Revision != message.VisualRevision
                || sprite.TextOpacity != renderer.CompositionTextOpacity;
            if (changed)
            {
                using (Bitmap bitmap = renderer.RenderCompositionMessage(message))
                {
                    IntPtr surface = Upload(bitmap);
                    Release(ref sprite.Surface);
                    sprite.Surface = surface;
                    sprite.Height = bitmap.Height;
                }
                sprite.Revision = message.VisualRevision;
                sprite.TextOpacity = renderer.CompositionTextOpacity;
                _dirty = true;
            }

            float y = message.Y - DanmakuRenderer.CompositionPadding;
            bool move = changed || rebase || Math.Abs(sprite.Speed - message.Speed) > 0.01f
                || (message.Fixed && sprite.X != DanmakuRenderer.MessageContentX(message) - DanmakuRenderer.CompositionPadding);
            bool moveY = move || sprite.Y != y;
            if (move) SetMotion(sprite, message, width, stamp);
            sprite.Y = y;
            sprite.Seen = _generation;
            for (int index = 0; index < _branches.Length; index++)
            {
                RectangleF clip = _clips[index];
                if (clip.IsEmpty || y >= clip.Bottom || y + sprite.Height <= clip.Top)
                {
                    Detach(sprite, index);
                    continue;
                }
                bool attach = sprite.Visuals[index] == IntPtr.Zero;
                if (attach) sprite.Visuals[index] = CreateObject(7);
                IntPtr visual = sprite.Visuals[index];
                if (changed || attach) SetObject(visual, 15, sprite.Surface); // SetContent
                if (move || attach)
                {
                    if (sprite.Animation != IntPtr.Zero) SetObject(visual, 3, sprite.Animation); // Animated X
                    else SetFloat(visual, 4, sprite.X);
                }
                if (moveY || attach) SetFloat(visual, 6, y);
                if (attach) AddVisual(_branches[index], visual);
                if (attach || move || moveY) _dirty = true;
            }
        }
        _expired.Clear();
        foreach (var entry in _sprites)
            if (entry.Value.Seen != _generation) _expired.Add(entry.Key);
        foreach (FlyingDanmaku message in _expired)
        {
            Sprite sprite = _sprites[message];
            for (int index = 0; index < _branches.Length; index++) Detach(sprite, index);
            ReleaseSprite(sprite);
            _sprites.Remove(message);
        }
        _expired.Clear();
    }

    private void SetMotion(Sprite sprite, FlyingDanmaku message, int width, long stamp)
    {
        Release(ref sprite.Animation);
        sprite.X = DanmakuRenderer.MessageContentX(message) - DanmakuRenderer.CompositionPadding;
        sprite.Speed = message.Speed;
        if (message.Fixed || message.Speed == 0f) return;
        float end = (message.Speed < 0f ? width : -message.Width) - DanmakuRenderer.CompositionPadding;
        double seconds = (sprite.X - end) / message.Speed;
        if (seconds <= 0) return;
        sprite.Animation = CreateObject(25); // CreateAnimation
        IntPtr animation = sprite.Animation;
        Check(Vtable<SetTime>(animation, 4)(animation, stamp)); // SetAbsoluteBeginTime (QPC)
        Check(Vtable<AddCubic>(animation, 5)(animation, 0, sprite.X, -message.Speed, 0, 0));
        Check(Vtable<EndAnimation>(animation, 8)(animation, seconds, end));
    }

    internal static void SetClipRegions(Size size, RectangleF reservation, RectangleF[] clips)
    {
        var player = new RectangleF(0, 0, size.Width, size.Height);
        RectangleF cutout = RectangleF.Intersect(player, reservation);
        if (cutout.Width <= 0 || cutout.Height <= 0)
        {
            clips[0] = player;
            for (int index = 1; index < 4; index++) clips[index] = RectangleF.Empty;
            return;
        }
        clips[0] = RectangleF.FromLTRB(0, 0, size.Width, cutout.Top);
        clips[1] = RectangleF.FromLTRB(0, cutout.Top, cutout.Left, cutout.Bottom);
        clips[2] = RectangleF.FromLTRB(cutout.Right, cutout.Top, size.Width, cutout.Bottom);
        clips[3] = RectangleF.FromLTRB(0, cutout.Bottom, size.Width, size.Height);
    }

    private IntPtr Upload(Bitmap bitmap)
    {
        IntPtr surface;
        // DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_ALPHA_MODE_PREMULTIPLIED
        Check(Vtable<CreateSurface>(_composition, 8)(_composition, (uint)bitmap.Width, (uint)bitmap.Height, 87, 1, out surface));
        try
        {
            IntPtr texture;
            PointI origin;
            Guid iidTexture = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            Check(Vtable<BeginDraw>(surface, 3)(surface, IntPtr.Zero, ref iidTexture, out texture, out origin));
            try
            {
                // BeginDraw may hand back a subregion of a larger atlas texture.
                var box = new Box { Left = (uint)origin.X, Top = (uint)origin.Y,
                    Right = (uint)(origin.X + bitmap.Width), Bottom = (uint)(origin.Y + bitmap.Height), Back = 1 };
                BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    Vtable<UpdateSubresource>(_context, 48)(_context, texture, 0, ref box, data.Scan0, checked((uint)data.Stride), 0);
                }
                finally { bitmap.UnlockBits(data); }
            }
            finally
            {
                Release(ref texture);
                Check(Vtable<Call>(surface, 4)(surface)); // EndDraw
            }
            return surface;
        }
        catch { Release(ref surface); throw; }
    }

    private void Detach(Sprite sprite, int index)
    {
        if (sprite.Visuals[index] == IntPtr.Zero) return;
        SetObject(_branches[index], 17, sprite.Visuals[index]); // RemoveVisual
        Release(ref sprite.Visuals[index]);
        _dirty = true;
    }

    private static void ReleaseSprite(Sprite sprite)
    {
        for (int index = 0; index < sprite.Visuals.Length; index++) Release(ref sprite.Visuals[index]);
        Release(ref sprite.Animation);
        Release(ref sprite.Surface);
    }

    private void ReleaseSprites()
    {
        foreach (Sprite sprite in _sprites.Values) ReleaseSprite(sprite);
        _sprites.Clear();
        _expired.Clear();
    }

    public void Dispose()
    {
        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
        ReleaseSprites();
        for (int index = 0; index < _branches.Length; index++) Release(ref _branches[index]);
        Release(ref _root);
        Release(ref _target);
        Release(ref _opacity);
        Release(ref _composition);
        Release(ref _context);
        Release(ref _device);
    }

    private IntPtr CreateObject(int slot)
    {
        IntPtr result;
        Check(Vtable<CreateComObject>(_composition, slot)(_composition, out result));
        return result;
    }

    private static void AddVisual(IntPtr parent, IntPtr child)
    {
        Check(Vtable<InsertVisual>(parent, 16)(parent, child, 1, IntPtr.Zero));
    }

    private static void SetObject(IntPtr instance, int slot, IntPtr value)
    {
        Check(Vtable<SetPointer>(instance, slot)(instance, value));
    }

    private static void SetFloat(IntPtr instance, int slot, float value)
    {
        Check(Vtable<SetScalar>(instance, slot)(instance, value));
    }

    private static void Release(ref IntPtr instance)
    {
        if (instance == IntPtr.Zero) return;
        Marshal.Release(instance);
        instance = IntPtr.Zero;
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
    }

    // Native SDK ABI. The MSVC vtable orders overloaded setters in reverse declaration
    // order (animation before float, clip object before literal D2D_RECT_F).
    private static T Vtable<T>(IntPtr instance, int slot) where T : class
    {
        IntPtr function = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer(function, typeof(T)) as T;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct EdgesF { public float Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Call(IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Query(IntPtr instance, ref Guid iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CheckState(IntPtr instance, out int valid);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateComObject(IntPtr instance, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTarget(IntPtr instance, IntPtr window, int topmost, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSurface(IntPtr instance, uint width, uint height, uint format, uint alpha, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int BeginDraw(IntPtr instance, IntPtr updateRect, ref Guid iid, out IntPtr texture, out PointI origin);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UpdateSubresource(IntPtr instance, IntPtr texture, uint subresource, ref Box region, IntPtr data, uint rowPitch, uint depthPitch);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetPointer(IntPtr instance, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetScalar(IntPtr instance, float value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetRect(IntPtr instance, ref EdgesF value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InsertVisual(IntPtr instance, IntPtr child, int above, IntPtr reference);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetTime(IntPtr instance, long timestamp);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddCubic(IntPtr instance, double seconds, float constant, float linear, float quadratic, float cubic);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EndAnimation(IntPtr instance, double seconds, float end);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driver, IntPtr software, uint flags, IntPtr featureLevels,
        uint levelCount, uint sdk, out IntPtr device, out int pickedLevel, out IntPtr context);
    [DllImport("dcomp.dll")]
    private static extern int DCompositionCreateDevice(IntPtr dxgiDevice, ref Guid iid, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background, Menu;
        public string ClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WindowClass windowClass);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint ex, string className, string title, uint style, int x, int y, int width,
        int height, IntPtr owner, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
}
