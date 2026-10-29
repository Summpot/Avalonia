using System;
using System.ComponentModel;
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
using Avalonia.Win32.WinRT;
using Avalonia.Win32.WinRT.Composition;
using MicroCom.Runtime;

namespace Avalonia.Win32.DComposition;

internal class DirectCompositionConnection : IRenderTimer, ID3D12DrawingSurfaceFactory, IWindowsCompositionConnection
{
    private static readonly Guid IID_IDCompositionDesktopDevice = Guid.Parse("5f4633fe-1e08-4cb8-8c75-ce24333f5602");

    private volatile Action<TimeSpan>? _tick;
    public bool RunsInBackground => true;

    private readonly DirectCompositionShared _shared;
    private readonly AutoResetEvent _wakeEvent = new(false);
    private readonly WindowsCompositionThread _thread;
    private volatile bool _stopped = true;

    private DirectCompositionConnection(DirectCompositionShared shared, WindowsCompositionThread thread)
    {
        _shared = shared;
        _thread = thread;
    }

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
    
    private static DirectCompositionConnection CreateCore()
    {
        return WindowsCompositionThread.Start(
            thread =>
            {
                var result = NativeMethods.DCompositionCreateDevice2(default, IID_IDCompositionDesktopDevice, out var cDevice);
                if (result != UnmanagedMethods.HRESULT.S_OK)
                {
                    throw new Win32Exception((int)result);
                }

                using var device = MicroComRuntime.CreateProxyFor<IDCompositionDesktopDevice>(cDevice, true);
                return new DirectCompositionConnection(new DirectCompositionShared(device), thread);
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
                    connection?._wakeEvent.Dispose();
                }
            },
            connection => connection._wakeEvent.Set());
    }

    public void Publish()
    {
        AvaloniaLocator.CurrentMutable.Bind<IWindowsSurfaceFactory>().ToConstant(this);
        AvaloniaLocator.CurrentMutable.Bind<IRenderLoop>().ToConstant(RenderLoop.FromTimer(this));
    }

    public void Stop() => _thread.Stop();

    private void RunLoop()
    {
        var cancellation = _thread.CancellationToken;
        var stopwatch = Stopwatch.StartNew();
        using var device = _shared.Device.CloneReference();
        
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                if (_stopped)
                    WaitHandle.WaitAny([_wakeEvent, cancellation.WaitHandle]);

                if (cancellation.IsCancellationRequested)
                    break;

                device.WaitForCommitCompletion();
                _tick?.Invoke(stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                Logger.TryGet(LogEventLevel.Error, LogArea.Win32Platform)
                    ?.Log(this, $"Failed to wait for vblank, Exception: {ex.Message}, HRESULT = {ex.HResult}");
            }
        }
    }

    public static bool IsSupported()
    {
        return Win32Platform.WindowsVersion >= PlatformConstants.Windows8_1;
    }

    public static bool TryCreateAndRegister()
    {
        if (TryCreate() is not { } connection)
            return false;

        connection.Publish();
        return true;
    }

    public static DirectCompositionConnection? TryCreate()
    {
        if (IsSupported())
        {
            try
            {
                return CreateCore();
            }
            catch (Exception e)
            {
                Logger.TryGet(LogEventLevel.Error, LogArea.Win32Platform)
                    ?.Log(null, "Unable to initialize WinUI compositor: {0}", e);
            }
        }
        else
        {
            var osVersionNotice =
                $"Windows {PlatformConstants.Windows8_1} is required. Your machine has Windows {Win32Platform.WindowsVersion} installed.";

            Logger.TryGet(LogEventLevel.Warning, LogArea.Win32Platform)?.Log(null,
                $"Unable to initialize WinUI compositor: {osVersionNotice}");
        }

        return null;
    }

    public bool RequiresNoRedirectionBitmap => true;

    public D3D12DrawingSurfaceCapabilities ProbeDrawingSurface(D3D12PlatformGraphics graphics) =>
        D3D12DrawingSurfaceProbe.ProbeDirectComposition(_shared.Device, graphics);

    public IPlatformRenderSurface CreateSurface(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info)
    {
        if (AvaloniaLocator.Current.GetService<IPlatformGraphics>() is D3D12PlatformGraphics d3d12)
        {
            var useSwapChain = AvaloniaLocator.Current.GetService<Win32PlatformOptions>()?.UseCompositionSwapChain ?? false;
            if (useSwapChain)
                return D3D12CompositionWindowSurface.CreateDComp(_shared, info, d3d12);
            return D3D12DrawingSurfaceWindowSurface.CreateDComp(_shared, info, d3d12);
        }

        return new DirectCompositedWindowSurface(_shared, info);
    }
}
