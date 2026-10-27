using System;
using Avalonia.Direct3D12;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Win32.DComposition;
using Avalonia.Win32.DirectX;
using Avalonia.Win32.WinRT.Composition;
using MicroCom.Runtime;

namespace Avalonia.Win32.Direct3D12;

/// <summary>
/// D3D12 render surface that presents through a DXGI flip-model composition swapchain
/// attached to a WinUI or DirectComposition visual host
/// (<see cref="Win32PlatformOptions.UseCompositionSwapChain"/> = true).
/// </summary>
internal sealed class D3D12CompositionWindowSurface : ID3D12PlatformSurface, ICompositionEffectsSurface, IDisposable
{
    private readonly D3D12PlatformGraphics _graphics;
    private readonly Func<ISwapchainVisualHost> _createHost;
    private readonly Func<BlurEffect, bool> _isBlurSupported;
    private ISwapchainVisualHost? _host;

    private D3D12CompositionWindowSurface(
        D3D12PlatformGraphics graphics,
        Func<ISwapchainVisualHost> createHost,
        Func<BlurEffect, bool> isBlurSupported)
    {
        _graphics = graphics;
        _createHost = createHost;
        _isBlurSupported = isBlurSupported;
    }

    public static D3D12CompositionWindowSurface CreateWinUi(
        WinUiCompositionShared shared,
        EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info,
        D3D12PlatformGraphics graphics)
    {
        var options = AvaloniaLocator.Current.GetService<Win32PlatformOptions>();
        return new D3D12CompositionWindowSurface(
            graphics,
            () => new WinUiCompositedWindow(info, shared, options?.WinUICompositionBackdropCornerRadius),
            effect => effect switch
            {
                BlurEffect.None => true,
                BlurEffect.Acrylic => Win32Platform.WindowsVersion >= WinUiCompositionShared.MinAcrylicVersion,
                BlurEffect.MicaLight => Win32Platform.WindowsVersion >= WinUiCompositionShared.MinHostBackdropVersion,
                BlurEffect.MicaDark => Win32Platform.WindowsVersion >= WinUiCompositionShared.MinHostBackdropVersion,
                _ => false
            });
    }

    public static D3D12CompositionWindowSurface CreateDComp(
        DirectCompositionShared shared,
        EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info,
        D3D12PlatformGraphics graphics)
    {
        return new D3D12CompositionWindowSurface(
            graphics,
            () => new DirectCompositedWindow(info, shared),
            // DirectComposition has no backdrop brushes.
            effect => effect == BlurEffect.None);
    }

    public bool IsBlurSupported(BlurEffect effect) => _isBlurSupported(effect);

    public ID3D12PlatformSurfaceRenderTarget CreateD3D12RenderTarget(ID3D12PlatformGraphicsContext context)
    {
        if (context.DeviceHandle != _graphics.DeviceHandle)
            throw new ArgumentException("Render target context must use the same D3D12 device as this surface.", nameof(context));

        _host ??= _createHost();

        return new D3D12CompositionRenderTarget(_host, _graphics);
    }

    public void Dispose()
    {
        _host?.Dispose();
        _host = null;
    }
}

/// <summary>
/// Renders into a DXGI flip-model swapchain created for composition via the D3D12 command queue
/// and attached as visual content through <see cref="ISwapchainVisualHost"/>.
/// </summary>
internal unsafe class D3D12CompositionRenderTarget : D3D12SwapchainRenderTargetBase
{
    private readonly ISwapchainVisualHost _host;
    private bool _isSwapchainTransparencyCapable;

    public D3D12CompositionRenderTarget(ISwapchainVisualHost host, D3D12PlatformGraphics graphics)
        : base(graphics)
    {
        _host = host;
    }

    private void CreateSwapchain(PixelSize size, bool isTransparency)
    {
        _commandResources.EnsureCreated(_graphics);

        using var adapter = MicroComRuntime.CreateProxyFor<IDXGIAdapter>(_graphics.AdapterHandle, false);
        var factoryGuid = MicroComRuntime.GetGuidFor(typeof(IDXGIFactory2));
        using var factory = MicroComRuntime.CreateProxyFor<IDXGIFactory2>(adapter.GetParent(&factoryGuid), true);

        var desc = new DXGI_SWAP_CHAIN_DESC1
        {
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            BufferUsage = DxgiRenderTarget.DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount = D3D12SwapchainBackBuffers.BufferCount,
            Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
            SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
            AlphaMode = isTransparency
                ? DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED
                : DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            Flags = 0
        };

        using var queueProxy = MicroComRuntime.CreateProxyFor<IUnknown>(_graphics.CommandQueueHandle, false);
        var swapChain = factory.CreateSwapChainForComposition(queueProxy, &desc, null);
        InitializeSwapChain(factory, swapChain, size, (f, sc) => _host.SetSwapchainContent(sc));
        _isSwapchainTransparencyCapable = isTransparency;
    }

    protected override void DetachSwapchain()
    {
        if (_swapChain is not null)
        {
            using (_host.BeginTransaction())
                _host.SetSwapchainContent(null);
        }
    }

    public override ID3D12PlatformSurfaceRenderingSession BeginRendering(IRenderTarget.RenderTargetSceneInfo sceneInfo)
    {
        ThrowIfLostOrDisposed();

        var transaction = _host.BeginTransaction();
        try
        {
            var isTransparency = sceneInfo.TransparencyLevel != CompositionTransparencyLevel.None;
            var size = new PixelSize(Math.Max(1, sceneInfo.Size.Width), Math.Max(1, sceneInfo.Size.Height));

            try
            {
                if (_swapChain is null || _isSwapchainTransparencyCapable != isTransparency)
                {
                    if (_swapChain is not null)
                    {
                        _graphics.PrepareForSwapchainMutation();
                        DestroySwapchain();
                    }

                    CreateSwapchain(size, isTransparency);
                }
                else if (_currentSize != size)
                {
                    ResizeSwapchainBuffers(size);
                }

                _host.ResizeIfNeeded(size);
                _host.ApplyEffects(sceneInfo.TransparencyLevel,
                    (sceneInfo.PlatformSpecificSceneInfo as Win32TopLevelSceneInfo)?.ThemeVariant ??
                    PlatformThemeVariant.Light);
            }
            catch (Exception e)
            {
                _lost = true;
                throw new RenderTargetCorruptedException(e);
            }

            var (resource, currentIndex) = PrepareRenderBuffer();

            var session = new D3D12SwapchainRenderingSession(
                this, _graphics, _swapChain!, resource, currentIndex,
                _commandResources, _backBuffers, _currentSize, sceneInfo.Scaling,
                syncInterval: 0,
                transaction: transaction);
            transaction = null;
            return session;
        }
        finally
        {
            transaction?.Dispose();
        }
    }
}
