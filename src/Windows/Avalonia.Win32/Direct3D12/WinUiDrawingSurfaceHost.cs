using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Win32.DirectX;
using Avalonia.Win32.Interop;
using Avalonia.Win32.WinRT;
using Avalonia.Win32.WinRT.Composition;
using MicroCom.Runtime;

namespace Avalonia.Win32.Direct3D12;

internal sealed class WinUiDrawingSurfaceHost : ID3D12DrawingSurfaceVisualHost
{
    private readonly WinUiCompositedWindow _window;
    private readonly ICompositor _compositor;
    private ICompositionGraphicsDevice? _compositionDevice;
    private ICompositionGraphicsDevice2? _compositionDevice2;
    private ICompositionSurface? _surface;
    private ICompositionDrawingSurfaceInterop? _surfaceInterop;
    private ICompositionDrawingSurface? _drawingSurface;
    private PixelSize _size;
    private bool _isSurfaceSupportTransparency;
    private DXGI_FORMAT _surfaceFormat;
    private IntPtr _boundD3D11Device;
    private bool _disposed;

    public WinUiDrawingSurfaceHost(
        EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info,
        WinUiCompositionShared shared,
        float? backdropCornerRadius)
    {
        _window = new WinUiCompositedWindow(info, shared, backdropCornerRadius);
        _compositor = shared.Compositor.CloneReference();
    }

    public IDisposable BeginTransaction() => _window.BeginTransaction();

    private void EnsureCompositionDevice(IntPtr d3d11Device)
    {
        if (_compositionDevice is not null && _boundD3D11Device == d3d11Device)
            return;

        DestroySurface();

        using var d3d11 = MicroComRuntime.CreateProxyFor<IUnknown>(d3d11Device, false);
        using var interop = _compositor.QueryInterface<ICompositorInterop>();
        _compositionDevice = interop.CreateGraphicsDevice(d3d11);
        _compositionDevice2 = _compositionDevice.QueryInterface<ICompositionGraphicsDevice2>();
        _boundD3D11Device = d3d11Device;
    }

    [MemberNotNull(nameof(_drawingSurface), nameof(_surface), nameof(_surfaceInterop))]
    private void CreateSurface(DXGI_FORMAT format, in IRenderTarget.RenderTargetSceneInfo sceneInfo)
    {
        bool isTransparency = sceneInfo.TransparencyLevel != CompositionTransparencyLevel.None;
        var surfaceSize = new PixelSize(Math.Max(1, sceneInfo.Size.Width), Math.Max(1, sceneInfo.Size.Height));
        var alphaMode = isTransparency ? DirectXAlphaMode.Premultiplied : DirectXAlphaMode.Ignore;
        _drawingSurface = _compositionDevice2!.CreateDrawingSurface2(new UnmanagedMethods.SIZE
            {
                X = surfaceSize.Width,
                Y = surfaceSize.Height,
            },
            (DirectXPixelFormat)format, alphaMode);
        _surface = _drawingSurface.QueryInterface<ICompositionSurface>();
        _surfaceInterop = _drawingSurface.QueryInterface<ICompositionDrawingSurfaceInterop>();
        _isSurfaceSupportTransparency = isTransparency;
        _surfaceFormat = format;
        _size = surfaceSize;
    }

    public unsafe D3D12DrawingSurfaceBeginDrawResult BeginDrawSurface(
        IntPtr d3d11Device, DXGI_FORMAT format, in IRenderTarget.RenderTargetSceneInfo sceneInfo)
    {
        EnsureCompositionDevice(d3d11Device);

        bool isTransparency = sceneInfo.TransparencyLevel != CompositionTransparencyLevel.None;
        if (_surface is null || _surfaceInterop is null || _drawingSurface is null
            || _isSurfaceSupportTransparency != isTransparency || _surfaceFormat != format)
        {
            _surface?.Dispose();
            _surfaceInterop?.Dispose();
            _drawingSurface?.Dispose();
            _surface = null;
            _surfaceInterop = null;
            _drawingSurface = null;
            CreateSurface(format, in sceneInfo);
        }

        var size = new PixelSize(Math.Max(1, sceneInfo.Size.Width), Math.Max(1, sceneInfo.Size.Height));
        _window.ResizeIfNeeded(sceneInfo.Size);
        _window.ApplyEffects(sceneInfo.TransparencyLevel,
            (sceneInfo.PlatformSpecificSceneInfo as Win32TopLevelSceneInfo)?.ThemeVariant ??
            PlatformThemeVariant.Light);
        _window.SetSurface(_surface!);

        if (_size != size)
        {
            _surfaceInterop!.Resize(new UnmanagedMethods.POINT
            {
                X = size.Width,
                Y = size.Height
            });
            _size = size;
        }

        void* pTexture;
        var iid = D3D12Interop.IID_ID3D11Texture2D;
        var off = _surfaceInterop!.BeginDraw(null, &iid, &pTexture);
        return new D3D12DrawingSurfaceBeginDrawResult
        {
            D3D11Texture2D = new IntPtr(pTexture),
            Offset = new PixelPoint(off.X, off.Y),
            Size = size
        };
    }

    public void EndDrawSurface() => _surfaceInterop?.EndDraw();

    public void DestroySurface()
    {
        if (_disposed)
            return;
        using var transaction = _window.BeginTransaction();
        _window.SetSurface(null);
        _surface?.Dispose();
        _surfaceInterop?.Dispose();
        _drawingSurface?.Dispose();
        _surface = null;
        _surfaceInterop = null;
        _drawingSurface = null;
        _compositionDevice2?.Dispose();
        _compositionDevice?.Dispose();
        _compositionDevice2 = null;
        _compositionDevice = null;
        _boundD3D11Device = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        DestroySurface();
        _disposed = true;
        _compositor.Dispose();
        _window.Dispose();
    }
}
