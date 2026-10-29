using System;
using System.Runtime.InteropServices;
using Avalonia.Direct3D12;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
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
        if (context.Device != _graphics.DeviceHandle)
            throw new ArgumentException("Render target context must use the same D3D12 device as this surface.", nameof(context));

        return new D3D12SwapchainRenderTarget(_window, _graphics);
    }
}

/// <summary>
/// HWND flip-model swapchain via <c>CreateSwapChainForHwnd</c>.
/// Always uses <see cref="DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE"/> —
/// <c>DXGI_ALPHA_MODE_PREMULTIPLIED</c> returns <c>DXGI_ERROR_INVALID_CALL</c> on real hardware.
/// Transparent / Acrylic / Mica windows must use the composition swapchain path instead.
/// </summary>
internal unsafe class D3D12SwapchainRenderTarget : ID3D12PlatformSurfaceRenderTarget
{
    private readonly EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo _window;
    private readonly D3D12PlatformGraphics _graphics;
    private readonly int _deviceGeneration;
    private readonly IntPtr _createdOnDevice;
    private readonly D3D12SwapchainBackBuffers _backBuffers = new();
    private readonly D3D12PerBufferCommandResources _commandResources = new();
    private IDXGISwapChain1? _swapChain;
    private IntPtr _swapChain3;
    private PixelSize _currentSize;
    private bool _disposed;
    private bool _lost;

    public D3D12SwapchainRenderTarget(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo window, D3D12PlatformGraphics graphics)
    {
        _window = window;
        _graphics = graphics;
        _deviceGeneration = graphics.DeviceGeneration;
        _createdOnDevice = graphics.DeviceHandle;
        _currentSize = new PixelSize(Math.Max(1, _window.Size.Width), Math.Max(1, _window.Size.Height));
        try
        {
            _commandResources.EnsureCreated(graphics);
            CreateSwapchain(_currentSize);
        }
        catch
        {
            DestroySwapchain();
            _commandResources.Release();
            throw;
        }
    }

    private void CreateSwapchain(PixelSize size)
    {
        // Factory must come from the same adapter that created the D3D12 device.
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
        IntPtr swapChain3 = IntPtr.Zero;
        try
        {
            factory.MakeWindowAssociation(_window.Handle, (uint)(DXGI_MWA.DXGI_MWA_NO_ALT_ENTER | DXGI_MWA.DXGI_MWA_NO_PRINT_SCREEN));

            var iid = D3D12Interop.IID_IDXGISwapChain3;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
                MicroComRuntime.GetNativeIntPtr(swapChain), in iid, out swapChain3));

            _swapChain = swapChain;
            _swapChain3 = swapChain3;
            swapChain3 = IntPtr.Zero;

            _currentSize = size;
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

    /// <summary>
    /// Corrupted when this target's device was lost or replaced (generation mismatch),
    /// so the compositor drops it and creates a swapchain on the recovered device.
    /// </summary>
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

    public ID3D12PlatformSurfaceRenderingSession BeginRendering(IRenderTarget.RenderTargetSceneInfo sceneInfo)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(D3D12SwapchainRenderTarget));
        // When bound to a superseded device generation, invalidate this target locally without affecting the active platform device.
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
                _graphics.PrepareForSwapchainMutation();
                _backBuffers.Release();
                _swapChain.ResizeBuffers(
                    D3D12SwapchainBackBuffers.BufferCount,
                    (uint)newSize.Width,
                    (uint)newSize.Height,
                    DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                    0);
                _backBuffers.Acquire(_swapChain);
                _currentSize = newSize;
                _backBuffers.ClearFrameFenceValues();
            }
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

        return new D3D12SwapchainRenderingSession(
            this, _graphics, _swapChain!, resource, currentIndex,
            _commandResources, _backBuffers, _currentSize, _window.Scaling);
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
/// HWND swapchain frame end: Dispose transitions the back buffer to PRESENT and calls Present.
/// A presentation failure marks the owning target Corrupted so it is recreated; only device loss
/// invalidates the device.
/// </summary>
internal class D3D12SwapchainRenderingSession : ID3D12PlatformSurfaceRenderingSession
{
    private readonly D3D12SwapchainRenderTarget _target;
    private readonly D3D12PlatformGraphics _graphics;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IntPtr _resource;
    private readonly uint _bufferIndex;
    private readonly D3D12PerBufferCommandResources _commandResources;
    private readonly D3D12SwapchainBackBuffers _backBuffers;
    private readonly PixelSize _size;
    private readonly double _scaling;
    private bool _disposed;

    public D3D12SwapchainRenderingSession(
        D3D12SwapchainRenderTarget target,
        D3D12PlatformGraphics graphics,
        IDXGISwapChain1 swapChain,
        IntPtr resource,
        uint bufferIndex,
        D3D12PerBufferCommandResources commandResources,
        D3D12SwapchainBackBuffers backBuffers,
        PixelSize size,
        double scaling)
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

            var hr = _swapChain.Present(1, 0);
            D3D12PresentationFailure.ThrowIfFailed(hr);

            _backBuffers.FrameFenceValues[_bufferIndex] = _graphics.Signal();
        }
        catch (Exception ex)
        {
            failure.Capture(ex);
        }
        if (failure.HasFailure)
            failure.Throw(_target.MarkCorrupted, _graphics.MarkLost);
    }
}
