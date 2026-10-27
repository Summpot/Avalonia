using System;
using Avalonia.Direct3D12;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Win32.DirectX;
using MicroCom.Runtime;

namespace Avalonia.Win32.Direct3D12;

internal class D3D12SwapchainWindowSurface : ID3D12PlatformSurface
{
    private readonly EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo _window;
    private readonly D3D12PlatformGraphics _graphics;

    public D3D12SwapchainWindowSurface(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo window, D3D12PlatformGraphics graphics)
    {
        _window = window;
        _graphics = graphics;
    }

    public bool IsReady => _window.Handle != IntPtr.Zero && _window.Size.Width > 0 && _window.Size.Height > 0;

    public ID3D12PlatformSurfaceRenderTarget CreateD3D12RenderTarget(ID3D12PlatformGraphicsContext context)
    {
        if (context.DeviceHandle != _graphics.DeviceHandle)
            throw new ArgumentException("Render target context must use the same D3D12 device as this surface.", nameof(context));

        return new D3D12SwapchainRenderTarget(_window, _graphics);
    }
}

/// <summary>
/// HWND flip-model swapchain via <c>CreateSwapChainForHwnd</c>.
/// Always uses <see cref="DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE"/> —
/// <c>DXGI_ALPHA_MODE_PREMULTIPLIED</c> returns <c>DXGI_ERROR_INVALID_CALL</c> on real hardware.
/// Transparent windows require composition drawing surfaces or a composition swapchain.
/// Acrylic and Mica backdrops require WinUI composition.
/// </summary>
internal unsafe class D3D12SwapchainRenderTarget : D3D12SwapchainRenderTargetBase
{
    private readonly EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo _window;

    public D3D12SwapchainRenderTarget(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo window, D3D12PlatformGraphics graphics)
        : base(graphics)
    {
        _window = window;
        _currentSize = new PixelSize(Math.Max(1, _window.Size.Width), Math.Max(1, _window.Size.Height));
        try
        {
            _commandResources.EnsureCreated(graphics);
            CreateSwapchain(_currentSize);
        }
        catch
        {
            ReleaseDeviceResources();
            throw;
        }
    }

    private void CreateSwapchain(PixelSize size)
    {
        using var adapter = MicroComRuntime.CreateProxyFor<IDXGIAdapter>(_graphics.AdapterHandle, false);
        var factoryGuid = MicroComRuntime.GetGuidFor(typeof(IDXGIFactory2));
        using var factory = MicroComRuntime.CreateProxyFor<IDXGIFactory2>(adapter.GetParent(&factoryGuid), true);

        var desc = new DXGI_SWAP_CHAIN_DESC1
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            BufferUsage = DxgiRenderTarget.DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount = D3D12SwapchainBackBuffers.BufferCount,
            Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
            SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
            AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            Flags = 0
        };

        using var queueProxy = MicroComRuntime.CreateProxyFor<IUnknown>(_graphics.CommandQueueHandle, false);
        var swapChain = factory.CreateSwapChainForHwnd(queueProxy, _window.Handle, &desc, null, null);
        InitializeSwapChain(factory, swapChain, size, (f, sc) =>
            f.MakeWindowAssociation(_window.Handle, (uint)(DXGI_MWA.DXGI_MWA_NO_ALT_ENTER | DXGI_MWA.DXGI_MWA_NO_PRINT_SCREEN)));
    }

    public override ID3D12PlatformSurfaceRenderingSession BeginRendering(IRenderTarget.RenderTargetSceneInfo sceneInfo)
    {
        ThrowIfLostOrDisposed();

        try
        {
            var newSize = new PixelSize(Math.Max(1, _window.Size.Width), Math.Max(1, _window.Size.Height));

            if (_swapChain is null)
            {
                _graphics.PrepareForSwapchainMutation();
                CreateSwapchain(newSize);
            }
            else if (newSize != _currentSize)
            {
                ResizeSwapchainBuffers(newSize);
            }
        }
        catch (Exception e)
        {
            _lost = true;
            throw new RenderTargetCorruptedException(e);
        }

        var (resource, currentIndex) = PrepareRenderBuffer();

        return new D3D12SwapchainRenderingSession(
            this, _graphics, _swapChain!, resource, currentIndex,
            _commandResources, _backBuffers, _currentSize, _window.Scaling,
            syncInterval: 1);
    }
}
