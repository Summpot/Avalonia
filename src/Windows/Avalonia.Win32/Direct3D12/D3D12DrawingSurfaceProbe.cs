using System;
using Avalonia.Logging;
using Avalonia.Win32.DComposition;
using Avalonia.Win32.DirectX;
using Avalonia.Win32.Interop;
using Avalonia.Win32.WinRT;
using Avalonia.Win32.WinRT.Composition;
using MicroCom.Runtime;

namespace Avalonia.Win32.Direct3D12;

internal interface ID3D12DrawingSurfaceFactory : IWindowsSurfaceFactory
{
    D3D12DrawingSurfaceCapabilities ProbeDrawingSurface(D3D12PlatformGraphics graphics);
}

internal readonly struct D3D12DrawingSurfaceCapabilities
{
    public DXGI_FORMAT Format { get; }
    public bool DirectRendering { get; }

    public D3D12DrawingSurfaceCapabilities(DXGI_FORMAT format, bool directRendering)
    {
        Format = format;
        DirectRendering = directRendering;
    }
}

internal static unsafe class D3D12DrawingSurfaceProbe
{
    private const int ProbeSize = 16;

    public static D3D12DrawingSurfaceCapabilities ProbeWinUi(ICompositor compositor, D3D12PlatformGraphics graphics)
    {
        using var device = MicroComRuntime.CreateProxyFor<IUnknown>(graphics.D3D11DeviceHandle, false);
        using var interop = compositor.QueryInterface<ICompositorInterop>();
        using var compositionDevice = interop.CreateGraphicsDevice(device);
        using var compositionDevice2 = compositionDevice.QueryInterface<ICompositionGraphicsDevice2>();
        return SelectCapabilities((format, directRendering) =>
        {
            // The surface alpha mode must match the window's transparency, so both modes have to work.
            foreach (var alphaMode in new[] { DirectXAlphaMode.Premultiplied, DirectXAlphaMode.Ignore })
            {
                using var surface = compositionDevice2.CreateDrawingSurface2(
                    new UnmanagedMethods.SIZE { X = ProbeSize, Y = ProbeSize }, (DirectXPixelFormat)format, alphaMode);
                using var surfaceInterop = surface.QueryInterface<ICompositionDrawingSurfaceInterop>();
                var iid = D3D12Interop.IID_ID3D11Texture2D;
                void* texture;
                var offset = surfaceInterop.BeginDraw(null, &iid, &texture);
                var completed = false;
                try
                {
                    ProbeTexture(graphics, format, (IntPtr)texture, new PixelPoint(offset.X, offset.Y), directRendering);
                    completed = true;
                }
                finally
                {
                    D3D12Interop.Release((IntPtr)texture);
                    try
                    {
                        surfaceInterop.EndDraw();
                    }
                    catch when (!completed)
                    {
                        // The probe failure is the one to report, not a failure to close the failed draw.
                    }
                }
            }
        });
    }

    public static D3D12DrawingSurfaceCapabilities ProbeDirectComposition(IDCompositionDevice2 device, D3D12PlatformGraphics graphics)
    {
        using var renderingDevice = MicroComRuntime.CreateProxyFor<IUnknown>(graphics.D3D11DeviceHandle, false);
        using var surfaceFactory = device.CreateSurfaceFactory(renderingDevice);
        return SelectCapabilities((format, directRendering) =>
        {
            // The surface alpha mode must match the window's transparency, so both modes have to work.
            foreach (var alphaMode in new[]
                     {
                         DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED,
                         DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE
                     })
            {
                using var surface = surfaceFactory.CreateSurface(ProbeSize, ProbeSize, format, alphaMode);
                var rect = new UnmanagedMethods.RECT { right = ProbeSize, bottom = ProbeSize };
                var iid = D3D12Interop.IID_ID3D11Texture2D;
                void* texture;
                var offset = surface.BeginDraw(&rect, &iid, &texture);
                var completed = false;
                try
                {
                    ProbeTexture(graphics, format, (IntPtr)texture, new PixelPoint(offset.X, offset.Y), directRendering);
                    completed = true;
                }
                finally
                {
                    D3D12Interop.Release((IntPtr)texture);
                    try
                    {
                        surface.EndDraw();
                    }
                    catch when (!completed)
                    {
                        // The probe failure is the one to report, not a failure to close the failed draw.
                    }
                }
            }
        });
    }

    internal static D3D12DrawingSurfaceCapabilities SelectCapabilities(Action<DXGI_FORMAT, bool> probe)
    {
        try
        {
            probe(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, true);
            return new(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, true);
        }
        catch (Exception e) when (IsUnsupported(e))
        {
            Logger.TryGet(LogEventLevel.Warning, "Direct3D12")
                ?.Log(null, "Direct composition texture rendering is unavailable; probing the copy path: {0}", e);
        }

        try
        {
            probe(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, false);
            return new(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, false);
        }
        catch (Exception e) when (IsUnsupported(e))
        {
            Logger.TryGet(LogEventLevel.Warning, "Direct3D12")
                ?.Log(null, "RGBA8 composition drawing surfaces are unavailable; trying BGRA8: {0}", e);
        }

        probe(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, false);
        return new(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, false);
    }

    private static bool IsUnsupported(Exception exception) =>
        exception.HResult is unchecked((int)0x80070057) // E_INVALIDARG
            or unchecked((int)0x80004002) // E_NOINTERFACE
            or unchecked((int)0x80004001) // E_NOTIMPL
            or unchecked((int)0x887A0004); // DXGI_ERROR_UNSUPPORTED

    private static void ProbeTexture(D3D12PlatformGraphics graphics, DXGI_FORMAT format,
        IntPtr destination, PixelPoint offset, bool directRendering)
    {
        if (directRendering)
        {
            using var commands = new D3D12PerBufferCommandResources(1);
            try
            {
                using var compositionTexture = new D3D12CompositionTexture(graphics, destination, offset,
                    new PixelSize(ProbeSize, ProbeSize), commands, 0);
            }
            finally
            {
                graphics.WaitForGpu();
            }
            return;
        }

        var texture = graphics.CreateCommittedTexture2D((uint)format, ProbeSize, ProbeSize,
            D3D12Interop.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET,
            D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET);
        IntPtr wrapped = IntPtr.Zero;
        try
        {
            wrapped = graphics.CreateWrappedOffscreenTexture(texture);
            graphics.CopyWrappedOffscreenToComposition(wrapped, destination, offset);
            graphics.WaitForGpu();
        }
        finally
        {
            D3D12Interop.Release(wrapped);
            D3D12Interop.Release(texture);
        }
    }
}
