using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Win32.DComposition;
using Avalonia.Win32.DirectX;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32.Direct3D12;

internal sealed class DCompDrawingSurfaceHost : ID3D12DrawingSurfaceVisualHost
{
    private readonly DirectCompositedWindow _window;
    private readonly DirectCompositionShared _shared;
    private IDCompositionSurface? _surface;
    private PixelSize _size;
    private bool _isSurfaceSupportTransparency;
    private DXGI_FORMAT _surfaceFormat;
    private IntPtr _boundD3D11Device;
    private bool _disposed;

    public DCompDrawingSurfaceHost(
        EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info,
        DirectCompositionShared shared)
    {
        _shared = shared;
        _window = new DirectCompositedWindow(info, shared);
    }

    public IDisposable BeginTransaction() => _window.BeginTransaction();

    [MemberNotNull(nameof(_surface))]
    private void CreateSurface(IntPtr d3d11Device, DXGI_FORMAT format, PixelSize surfaceSize, bool isTransparency)
    {
        using var d3d11 = MicroComRuntime.CreateProxyFor<IUnknown>(d3d11Device, false);
        using var surfaceFactory = _shared.Device.CreateSurfaceFactory(d3d11);

        var alphaMode = isTransparency
            ? DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED
            : DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE;

        // Fixed-size surface (not virtual). BeginDraw of the full rect may still return a
        // non-zero offset (DComp/WinUI); the render target draws directly into the composition
        // texture or copies an offscreen RT into that region.
        _surface = surfaceFactory.CreateSurface(
            (uint)Math.Max(1, surfaceSize.Width),
            (uint)Math.Max(1, surfaceSize.Height),
            format,
            alphaMode);
        _isSurfaceSupportTransparency = isTransparency;
        _surfaceFormat = format;
        _size = surfaceSize;
        _boundD3D11Device = d3d11Device;
    }

    public unsafe D3D12DrawingSurfaceBeginDrawResult BeginDrawSurface(
        IntPtr d3d11Device, DXGI_FORMAT format, in IRenderTarget.RenderTargetSceneInfo sceneInfo)
    {
        bool isTransparency = sceneInfo.TransparencyLevel != CompositionTransparencyLevel.None;
        var size = new PixelSize(Math.Max(1, sceneInfo.Size.Width), Math.Max(1, sceneInfo.Size.Height));

        if (_surface is null
            || _boundD3D11Device != d3d11Device
            || isTransparency != _isSurfaceSupportTransparency
            || _surfaceFormat != format
            || _size != size)
        {
            _surface?.Dispose();
            _surface = null;
            CreateSurface(d3d11Device, format, size, isTransparency);
        }

        _window.SetSurface(_surface!);

        void* pTexture;
        var rect = new UnmanagedMethods.RECT { right = size.Width, bottom = size.Height };
        var iid = D3D12Interop.IID_ID3D11Texture2D;
        var off = _surface!.BeginDraw(&rect, &iid, &pTexture);
        return new D3D12DrawingSurfaceBeginDrawResult
        {
            D3D11Texture2D = new IntPtr(pTexture),
            Offset = new PixelPoint(off.X, off.Y),
            Size = size
        };
    }

    public void EndDrawSurface() => _surface?.EndDraw();

    public void DestroySurface()
    {
        if (_disposed)
            return;
        using var transaction = _window.BeginTransaction();
        _window.SetSurface(null);
        _surface?.Dispose();
        _surface = null;
        _boundD3D11Device = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        DestroySurface();
        _disposed = true;
        _window.Dispose();
    }
}
