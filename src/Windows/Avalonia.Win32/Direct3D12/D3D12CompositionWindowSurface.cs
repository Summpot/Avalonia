using System;
using System.Runtime.InteropServices;
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
    private IDisposable? _hostDisposable;

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
        if (context.Device != _graphics.DeviceHandle)
            throw new ArgumentException("Render target context must use the same D3D12 device as this surface.", nameof(context));

        _host ??= _createHost();
        if (_hostDisposable is null && _host is IDisposable disposable)
            _hostDisposable = disposable;

        return new D3D12CompositionRenderTarget(_host, _graphics);
    }

    public void Dispose()
    {
        _hostDisposable?.Dispose();
        _hostDisposable = null;
        _host = null;
    }
}

/// <summary>
/// Renders into a DXGI flip-model swapchain created for composition via the D3D12 command queue
/// and attached as visual content through <see cref="ISwapchainVisualHost"/>.
/// </summary>
internal unsafe class D3D12CompositionRenderTarget : ID3D12PlatformSurfaceRenderTarget
{
    private readonly ISwapchainVisualHost _host;
    private readonly D3D12PlatformGraphics _graphics;
    private readonly int _deviceGeneration;
    private readonly IntPtr _createdOnDevice;
    private readonly D3D12SwapchainBackBuffers _backBuffers = new();
    private readonly D3D12PerBufferCommandResources _commandResources = new();
    private IDXGISwapChain1? _swapChain;
    private IntPtr _swapChain3;
    private PixelSize _size;
    private bool _isSwapchainTransparencyCapable;
    private bool _disposed;
    private bool _lost;

    public D3D12CompositionRenderTarget(ISwapchainVisualHost host, D3D12PlatformGraphics graphics)
    {
        _host = host;
        _graphics = graphics;
        _deviceGeneration = graphics.DeviceGeneration;
        _createdOnDevice = graphics.DeviceHandle;
    }

    public PlatformRenderTargetState State
    {
        get
        {
            if (_disposed)
                return PlatformRenderTargetState.Disposed;
            if (_lost
                || _graphics.IsLost
                || _graphics.DeviceGeneration != _deviceGeneration
                || _graphics.DeviceHandle != _createdOnDevice)
                return PlatformRenderTargetState.Corrupted;
            return PlatformRenderTargetState.Ready;
        }
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
            // Premultiplied only when the window is actually transparent.
            // See https://github.com/AvaloniaUI/Avalonia/issues/20643
            AlphaMode = isTransparency
                ? DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED
                : DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            Flags = 0
        };

        // D3D12: CreateSwapChainForComposition takes the command queue (not the device).
        using var queueProxy = MicroComRuntime.CreateProxyFor<IUnknown>(_graphics.CommandQueueHandle, false);
        var swapChain = factory.CreateSwapChainForComposition(queueProxy, &desc, null);
        IntPtr swapChain3 = IntPtr.Zero;
        try
        {
            var iid = D3D12Interop.IID_IDXGISwapChain3;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
                MicroComRuntime.GetNativeIntPtr(swapChain), in iid, out swapChain3));

            _host.SetSwapchainContent(swapChain);

            _swapChain = swapChain;
            _swapChain3 = swapChain3;
            swapChain3 = IntPtr.Zero;

            _isSwapchainTransparencyCapable = isTransparency;
            _size = size;
            _backBuffers.ClearFrameFenceValues();
            _backBuffers.Acquire(swapChain);
        }
        catch
        {
            if (swapChain3 != IntPtr.Zero)
                Marshal.Release(swapChain3);
            if (_swapChain is not null || _swapChain3 != IntPtr.Zero)
                DestroySwapchain();
            else
                swapChain.Dispose();
            throw;
        }
    }

    private void DestroySwapchain()
    {
        _backBuffers.Release();

        if (_swapChain3 != IntPtr.Zero)
        {
            Marshal.Release(_swapChain3);
            _swapChain3 = IntPtr.Zero;
        }

        _swapChain?.Dispose();
        _swapChain = null;
    }

    public ID3D12PlatformSurfaceRenderingSession BeginRendering(IRenderTarget.RenderTargetSceneInfo sceneInfo)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(D3D12CompositionRenderTarget));
        if (_graphics.DeviceGeneration != _deviceGeneration
            || _graphics.DeviceHandle != _createdOnDevice)
        {
            _lost = true;
            throw new PlatformGraphicsContextLostException();
        }
        if (_graphics.IsLost)
        {
            _lost = true;
            throw new PlatformGraphicsContextLostException();
        }
        if (_lost)
            throw new RenderTargetCorruptedException();

        var transaction = _host.BeginTransaction();
        try
        {
            var isTransparency = sceneInfo.TransparencyLevel != CompositionTransparencyLevel.None;
            // Composition swapchains require explicit non-zero dimensions.
            var size = new PixelSize(Math.Max(1, sceneInfo.Size.Width), Math.Max(1, sceneInfo.Size.Height));

            try
            {
                if (_swapChain is null || _isSwapchainTransparencyCapable != isTransparency)
                {
                    // AlphaMode cannot be changed by ResizeBuffers — recreate the whole chain.
                    if (_swapChain is not null)
                    {
                        _graphics.PrepareForSwapchainMutation();
                        DestroySwapchain();
                    }

                    CreateSwapchain(size, isTransparency);
                }
                else if (_size != size)
                {
                    _graphics.PrepareForSwapchainMutation();
                    _backBuffers.Release();
                    _swapChain.ResizeBuffers(
                        D3D12SwapchainBackBuffers.BufferCount,
                        (uint)size.Width,
                        (uint)size.Height,
                        DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                        0);
                    _backBuffers.Acquire(_swapChain);
                    _size = size;
                    _backBuffers.ClearFrameFenceValues();
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

            var currentIndex = D3D12Interop.GetCurrentBackBufferIndex(_swapChain3);
            _graphics.WaitForFenceValue(_backBuffers.FrameFenceValues[currentIndex]);

            var resource = _backBuffers.GetResource(currentIndex);
            _commandResources.AllocatorFenceValues[currentIndex] = _graphics.ExecuteTransition(
                _commandResources.GetAllocator(currentIndex),
                _commandResources.GetCommandList(currentIndex),
                resource,
                D3D12Interop.D3D12_RESOURCE_STATE_PRESENT,
                D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET,
                _commandResources.AllocatorFenceValues[currentIndex]);

            var session = new D3D12CompositionRenderingSession(
                this, _graphics, _swapChain!, resource, currentIndex,
                _commandResources, _backBuffers, _size, sceneInfo.Scaling,
                transaction);
            transaction = null;
            return session;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    internal void MarkCorrupted() => _lost = true;

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            try
            {
                if (!_graphics.IsLost)
                    _graphics.PrepareForSwapchainMutation();
            }
            catch (PlatformGraphicsContextLostException)
            {
            }
        }
        finally
        {
            try
            {
                DestroySwapchain();
            }
            finally
            {
                _commandResources.Dispose();
                _disposed = true;
            }
        }
    }
}

/// <summary>
/// Composition swapchain frame end: Dispose transitions to PRESENT, calls Present,
/// then commits the visual transaction. A failure marks the owning target Corrupted so it is
/// recreated; only device loss invalidates the device. The first failure is the one reported.
/// </summary>
internal class D3D12CompositionRenderingSession : ID3D12PlatformSurfaceRenderingSession
{
    private readonly D3D12CompositionRenderTarget _target;
    private readonly D3D12PlatformGraphics _graphics;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IntPtr _resource;
    private readonly uint _bufferIndex;
    private readonly D3D12PerBufferCommandResources _commandResources;
    private readonly D3D12SwapchainBackBuffers _backBuffers;
    private readonly PixelSize _size;
    private readonly double _scaling;
    private readonly IDisposable _transaction;
    private bool _disposed;

    public D3D12CompositionRenderingSession(
        D3D12CompositionRenderTarget target,
        D3D12PlatformGraphics graphics,
        IDXGISwapChain1 swapChain,
        IntPtr resource,
        uint bufferIndex,
        D3D12PerBufferCommandResources commandResources,
        D3D12SwapchainBackBuffers backBuffers,
        PixelSize size,
        double scaling,
        IDisposable transaction)
    {
        _target = target;
        _graphics = graphics;
        _swapChain = swapChain;
        _resource = resource;
        _bufferIndex = bufferIndex;
        _commandResources = commandResources;
        _backBuffers = backBuffers;
        _size = size;
        _scaling = scaling;
        _transaction = transaction;
    }

    public IntPtr Resource => _resource;
    public uint ResourceState => D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET;
    public uint Format => (uint)DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
    public PixelSize Size => _size;
    public PixelSize ResourceSize => Size;
    public PixelPoint Offset => default;
    public double Scaling => _scaling;
    public bool IsYFlipped => false;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        var failure = new D3D12PresentationFailure();
        try
        {
            _commandResources.AllocatorFenceValues[_bufferIndex] = _graphics.ExecuteTransition(
                _commandResources.GetAllocator(_bufferIndex),
                _commandResources.GetCommandList(_bufferIndex),
                _resource,
                D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET,
                D3D12Interop.D3D12_RESOURCE_STATE_PRESENT,
                _commandResources.AllocatorFenceValues[_bufferIndex],
                resetAllocator: false);

            // Present before committing the composition transaction so a freshly attached
            // swapchain always has a frame by the time it becomes visible.
            var hr = _swapChain.Present(0, 0);
            D3D12PresentationFailure.ThrowIfFailed(hr);

            _backBuffers.FrameFenceValues[_bufferIndex] = _graphics.Signal();
        }
        catch (Exception ex)
        {
            failure.Capture(ex);
        }
        finally
        {
            try
            {
                _transaction.Dispose();
            }
            catch (Exception ex)
            {
                failure.Capture(ex);
            }
        }
        if (failure.HasFailure)
            failure.Throw(_target.MarkCorrupted, _graphics.MarkLost);
    }
}
