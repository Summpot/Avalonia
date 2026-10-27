using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Logging;
using Avalonia.MicroCom;
using Avalonia.OpenGL.Egl;
using Avalonia.Rendering;
using Avalonia.Win32.Direct3D12;
using Avalonia.Win32.DirectX;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32.WinRT.Composition;

internal class WinUiCompositorConnection : IRenderTimer, ID3D12DrawingSurfaceFactory, IWindowsCompositionConnection
{
    private readonly WinUiCompositionShared _shared;
    private readonly AutoResetEvent _wakeEvent = new(false);
    private readonly WindowsCompositionThread _thread;
    private volatile IntPtr _loopWindow;
    private volatile bool _stopped = true;
    private volatile Action<TimeSpan>? _tick;

    public bool RunsInBackground => true;

    public Action<TimeSpan>? Tick
    {
        get => _tick;
        set
        {
            if (value != null)
            {
                _tick = value;
                _stopped = false;
                _wakeEvent.Set();
            }
            else
            {
                _stopped = true;
                _tick = null;
            }
        }
    }

    private WinUiCompositorConnection(WindowsCompositionThread thread)
    {
        _thread = thread;
        using var compositor = NativeWinRTMethods.CreateInstance<ICompositor>("Windows.UI.Composition.Compositor");
        /*
        var levels = new[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1 };
        DirectXUnmanagedMethods.D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero, 0, levels, (uint)levels.Length, 7, out var pD3dDevice, out var level, null);

        var d3dDevice = MicroComRuntime.CreateProxyFor<IUnknown>(pD3dDevice, true);

        var compositionDevice = compositor.QueryInterface<ICompositorInterop>().CreateGraphicsDevice(d3dDevice);
        var surf = compositionDevice.CreateDrawingSurface(new UnmanagedMethods.SIZE_F { X = 100, Y = 100 },
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        var surfInterop = surf.QueryInterface<ICompositionDrawingSurfaceInterop>();
        var IID_ID3D11Texture2D = Guid.Parse("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        void* texture = null;
        surfInterop.BeginDraw(null, &IID_ID3D11Texture2D, &texture);
        */

        _shared = new WinUiCompositionShared(compositor);
    }

    private static WinUiCompositorConnection CreateCore()
    {
        WinRTDispatcherQueue? dispatcher = null;
        return WindowsCompositionThread.Start(
            thread =>
            {
                dispatcher = new WinRTDispatcherQueue();
                return new WinUiCompositorConnection(thread);
            },
            connection => connection.RunLoop(),
            connection =>
            {
                try
                {
                    connection?._shared.Dispose();
                }
                finally
                {
                    try
                    {
                        dispatcher?.Dispose();
                    }
                    finally
                    {
                        connection?._wakeEvent.Dispose();
                    }
                }
            },
            connection => connection.Wake());
    }

    private class RunLoopHandler : CallbackBase, IAsyncActionCompletedHandler
    {
        private readonly WinUiCompositorConnection _parent;
        private readonly Stopwatch _st = Stopwatch.StartNew();
        private TimeSpan? _commitDueAt;
        private IAsyncAction? _currentCommit;
        private bool _commitCompleted;

        public RunLoopHandler(WinUiCompositorConnection parent)
        {
            _parent = parent;
        }

        public void Invoke(IAsyncAction? asyncInfo, AsyncStatus asyncStatus)
        {
            lock (_parent._shared.SyncRoot)
            {
                if (_parent._thread.CancellationToken.IsCancellationRequested
                    || _currentCommit == null || _currentCommit.GetNativeIntPtr() != asyncInfo.GetNativeIntPtr())
                    return;
                OnCommitCompleted();
            }
        }

        private void OnCommitCompleted()
        {
            Debug.Assert(Monitor.IsEntered(_parent._shared.SyncRoot), "Lock should be held");

            _currentCommit?.Dispose();
            _currentCommit = null;
            _parent._tick?.Invoke(_st.Elapsed);
            // Always schedule a commit so the current frame's work reaches DWM.
            ScheduleNextCommit();
            _commitCompleted = true;
        }

        // This method should be called outside the shared lock, as it might wait for a long time.
        public void OnAfterMessageWithoutLock()
        {
            Debug.Assert(!Monitor.IsEntered(_parent._shared.SyncRoot), "Lock should NOT be held");

            if (!_commitCompleted)
                return;

            _commitCompleted = false;

            if (_parent._stopped && !_parent._thread.CancellationToken.IsCancellationRequested)
            {
                WaitHandle.WaitAny([_parent._wakeEvent, _parent._thread.CancellationToken.WaitHandle]);
                // Reset the expected commit callback time since we've paused
                // the render loop due to app being idle
                _commitDueAt = _st.Elapsed + TimeSpan.FromSeconds(1);
            }
        }

        private void ScheduleNextCommit()
        {
            Debug.Assert(Monitor.IsEntered(_parent._shared.SyncRoot), "Lock should be held");

            _commitDueAt = _st.Elapsed + TimeSpan.FromSeconds(1);
            _currentCommit = _parent._shared.Compositor5.RequestCommitAsync();
            _currentCommit.SetCompleted(this);
        }

        public void WatchDog()
        {
            lock (_parent._shared.SyncRoot)
            {
                // This is a workaround for a nasty WinUI composition API bug that prevents
                // RequestCommitAsync to ever complete after D3D device loss event with some systems
                // (A notable example is after pause/resume in Parallels Desktop)
                // We check if we haven't got a commit completion callback for a second
                // And forcefully trigger the next one, which makes the entire thing to unstuck

                if (_st.Elapsed > _commitDueAt && _currentCommit != null)
                {
                    Logger.TryGet(LogEventLevel.Error, LogArea.Visual)?.Log(this,
                        "windows::UI::Composition::ICompositor5.RequestCommitAsync timed out, force-triggering next tick");
                    try
                    {
                        _currentCommit?.GetResults();
                    }
                    catch (Exception e)
                    {
                        Logger.TryGet(LogEventLevel.Error, LogArea.Visual)?.Log(this,
                            "ICompositor5::RequestCommitAsync failed: {HR}, {ERR}", e.HResult, e.ToString());
                    }

                    OnCommitCompleted();
                }
            }
        }

        public void Start()
        {
            lock (_parent._shared.SyncRoot)
                ScheduleNextCommit();
        }

        public void Stop()
        {
            lock (_parent._shared.SyncRoot)
            {
                _currentCommit?.Dispose();
                _currentCommit = null;
            }
        }
    }

    public void Publish()
    {
        AvaloniaLocator.CurrentMutable.Bind<IWindowsSurfaceFactory>().ToConstant(this);
        AvaloniaLocator.CurrentMutable.Bind<IRenderLoop>().ToConstant(RenderLoop.FromTimer(this));
    }

    public void Stop() => _thread.Stop();

    private void Wake()
    {
        _wakeEvent.Set();
        // Wakes the message loop immediately; the watchdog timer would otherwise wake it within a second.
        var window = _loopWindow;
        if (window != IntPtr.Zero)
            UnmanagedMethods.PostMessage(window, (uint)UnmanagedMethods.WindowsMessage.WM_NULL, IntPtr.Zero, IntPtr.Zero);
    }

    private void RunLoop()
    {
        var cancellation = _thread.CancellationToken;
        var handler = new RunLoopHandler(this);
        try
        {
            RunMessageLoop(handler, cancellation);
        }
        finally
        {
            _loopWindow = IntPtr.Zero;
            try
            {
                handler.Stop();
            }
            finally
            {
                handler.Dispose();
            }
        }
    }

    private void RunMessageLoop(RunLoopHandler handler, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return;
        handler.Start();

        const int watchDogIntervalInMs = 1000;

        using var dw = new SimpleWindow((hwnd, msg, w, l) =>
        {
            if (msg == (uint)UnmanagedMethods.WindowsMessage.WM_TIMER)
            {
                handler.WatchDog();
                UnmanagedMethods.SetTimer(hwnd, IntPtr.Zero, watchDogIntervalInMs, null);
            }
            return UnmanagedMethods.DefWindowProc(hwnd, msg, w, l);
        });
        UnmanagedMethods.SetTimer(dw.Handle, IntPtr.Zero, watchDogIntervalInMs, null);
        _loopWindow = dw.Handle;
        if (cancellation.IsCancellationRequested)
            UnmanagedMethods.PostMessage(dw.Handle, (uint)UnmanagedMethods.WindowsMessage.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        // Warning: the completion callback (RunLoopHandler.Invoke) from ICompositor5.RequestCommitAsync()
        // is called in DispatchMessage() on Windows 10, but in GetMessage() on Windows 11!
        // Be careful when changing the scope of the shared lock.

        var result = 0;
        while (!cancellation.IsCancellationRequested
               && (result = UnmanagedMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0)) > 0)
        {
            UnmanagedMethods.DispatchMessage(ref msg);
            handler.OnAfterMessageWithoutLock();
        }

        if (result < 0)
        {
            Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                ?.Log(this, "Unmanaged error in {0}. Error Code: {1}", nameof(RunLoop), Marshal.GetLastWin32Error());
        }
    }

    public static bool IsSupported()
    {
        return Win32Platform.WindowsVersion >= WinUiCompositionShared.MinWinCompositionVersion;
    }

    public static bool TryCreateAndRegister()
    {
        if (TryCreate() is not { } connection)
            return false;

        connection.Publish();
        return true;
    }

    public static WinUiCompositorConnection? TryCreate()
    {
        if (IsSupported())
        {
            try
            {
                return CreateCore();
            }
            catch (Exception e)
            {
                Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                    ?.Log(null, "Unable to initialize WinUI compositor: {0}", e);
            }
        }
        else
        {
            var osVersionNotice =
                $"Windows {WinUiCompositionShared.MinWinCompositionVersion} is required. Your machine has Windows {Win32Platform.WindowsVersion} installed.";

            Logger.TryGet(LogEventLevel.Warning, "WinUIComposition")?.Log(null,
                $"Unable to initialize WinUI compositor: {osVersionNotice}");
        }

        return null;
    }

    public bool RequiresNoRedirectionBitmap => true;

    public D3D12DrawingSurfaceCapabilities ProbeDrawingSurface(D3D12PlatformGraphics graphics) =>
        D3D12DrawingSurfaceProbe.ProbeWinUi(_shared.Compositor, graphics);

    public IPlatformRenderSurface CreateSurface(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info)
    {
        if (AvaloniaLocator.Current.GetService<IPlatformGraphics>() is D3D12PlatformGraphics d3d12)
        {
            var useSwapChain = AvaloniaLocator.Current.GetService<Win32PlatformOptions>()?.UseCompositionSwapChain ?? false;
            if (useSwapChain)
                return D3D12CompositionWindowSurface.CreateWinUi(_shared, info, d3d12);
            return D3D12DrawingSurfaceWindowSurface.CreateWinUi(_shared, info, d3d12);
        }

        return new WinUiCompositedWindowSurface(_shared, info);
    }
}
