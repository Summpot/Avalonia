using System;
using Avalonia.Direct3D12;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Win32.DComposition;
using Avalonia.Win32.WinRT.Composition;

namespace Avalonia.Win32.Direct3D12;

/// <summary>
/// Presents D3D12 frames through a WinUI or DirectComposition drawing surface,
/// using the format selected for the current device generation.
/// </summary>
internal sealed class D3D12DrawingSurfaceWindowSurface : ID3D12PlatformSurface, ICompositionEffectsSurface, IDisposable
{
    private readonly D3D12PlatformGraphics _graphics;
    private readonly Func<ID3D12DrawingSurfaceVisualHost> _createHost;
    private readonly Func<BlurEffect, bool> _isBlurSupported;
    private ID3D12DrawingSurfaceVisualHost? _host;

    private D3D12DrawingSurfaceWindowSurface(
        D3D12PlatformGraphics graphics,
        Func<ID3D12DrawingSurfaceVisualHost> createHost,
        Func<BlurEffect, bool> isBlurSupported)
    {
        _graphics = graphics;
        _createHost = createHost;
        _isBlurSupported = isBlurSupported;
    }

    public static D3D12DrawingSurfaceWindowSurface CreateWinUi(
        WinUiCompositionShared shared,
        EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info,
        D3D12PlatformGraphics graphics)
    {
        var options = AvaloniaLocator.Current.GetService<Win32PlatformOptions>();
        return new D3D12DrawingSurfaceWindowSurface(
            graphics,
            () => new WinUiDrawingSurfaceHost(info, shared, options?.WinUICompositionBackdropCornerRadius),
            effect => effect switch
            {
                BlurEffect.None => true,
                BlurEffect.Acrylic => Win32Platform.WindowsVersion >= WinUiCompositionShared.MinAcrylicVersion,
                BlurEffect.MicaLight => Win32Platform.WindowsVersion >= WinUiCompositionShared.MinHostBackdropVersion,
                BlurEffect.MicaDark => Win32Platform.WindowsVersion >= WinUiCompositionShared.MinHostBackdropVersion,
                _ => false
            });
    }

    public static D3D12DrawingSurfaceWindowSurface CreateDComp(
        DirectCompositionShared shared,
        EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info,
        D3D12PlatformGraphics graphics)
    {
        return new D3D12DrawingSurfaceWindowSurface(
            graphics,
            () => new DCompDrawingSurfaceHost(info, shared),
            effect => effect == BlurEffect.None);
    }

    public bool IsBlurSupported(BlurEffect effect) => _isBlurSupported(effect);

    public ID3D12PlatformSurfaceRenderTarget CreateD3D12RenderTarget(ID3D12PlatformGraphicsContext context)
    {
        if (context.DeviceHandle != _graphics.DeviceHandle)
            throw new ArgumentException("Render target context must use the same D3D12 device as this surface.", nameof(context));

        _host ??= _createHost();

        return new D3D12DrawingSurfaceRenderTarget(_host, _graphics);
    }

    public void Dispose()
    {
        _host?.Dispose();
        _host = null;
    }
}
