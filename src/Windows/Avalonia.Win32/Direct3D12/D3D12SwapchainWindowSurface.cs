using System;
using System.Runtime.InteropServices;
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
        if (context.Device != _graphics.DeviceHandle)
            throw new ArgumentException("Render target context must use the same D3D12 device as this surface.", nameof(context));

        return new D3D12SwapchainRenderTarget(_window, _graphics);
    }
}

internal unsafe class D3D12SwapchainRenderTarget : ID3D12PlatformSurfaceRenderTarget
{
    private const uint BufferCount = 2;
    private readonly EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo _window;
    private readonly D3D12PlatformGraphics _graphics;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IntPtr _swapChain3;
    private readonly IntPtr[] _renderTargets = new IntPtr[BufferCount];
    private readonly ulong[] _frameFenceValues = new ulong[BufferCount];
    private PixelSize _currentSize;
    private bool _disposed;

    public D3D12SwapchainRenderTarget(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo window, D3D12PlatformGraphics graphics)
    {
        _window = window;
        _graphics = graphics;
        _currentSize = new PixelSize(Math.Max(1, _window.Size.Width), Math.Max(1, _window.Size.Height));

        // Factory must come from the same adapter that created the D3D12 device.
        using var adapter = MicroComRuntime.CreateProxyFor<IDXGIAdapter>(_graphics.AdapterHandle, false);
        var factoryGuid = MicroComRuntime.GetGuidFor(typeof(IDXGIFactory2));
        using var factory = MicroComRuntime.CreateProxyFor<IDXGIFactory2>(adapter.GetParent(&factoryGuid), true);

        var desc = new DXGI_SWAP_CHAIN_DESC1
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            BufferUsage = 0x00000020U, // DXGI_USAGE_RENDER_TARGET_OUTPUT
            BufferCount = BufferCount,
            Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
            SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
            AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            Width = (uint)_currentSize.Width,
            Height = (uint)_currentSize.Height,
            Flags = 0
        };

        using var queueProxy = MicroComRuntime.CreateProxyFor<IUnknown>(_graphics.CommandQueueHandle, false);
        _swapChain = factory.CreateSwapChainForHwnd(queueProxy, _window.Handle, &desc, null, null);

        factory.MakeWindowAssociation(_window.Handle, (uint)(DXGI_MWA.DXGI_MWA_NO_ALT_ENTER | DXGI_MWA.DXGI_MWA_NO_PRINT_SCREEN));

        var iid = D3D12Interop.IID_IDXGISwapChain3;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
            MicroComRuntime.GetNativeIntPtr(_swapChain), in iid, out _swapChain3));

        AcquireBuffers();
    }

    private void AcquireBuffers()
    {
        for (uint i = 0; i < BufferCount; i++)
        {
            var resGuid = D3D12Interop.IID_ID3D12Resource;
            _renderTargets[i] = (IntPtr)_swapChain.GetBuffer(i, &resGuid);
        }
    }

    private void ReleaseBuffers()
    {
        for (var i = 0; i < BufferCount; i++)
        {
            if (_renderTargets[i] != IntPtr.Zero)
            {
                D3D12Interop.Release(_renderTargets[i]);
                _renderTargets[i] = IntPtr.Zero;
            }
        }
    }

    public ID3D12PlatformSurfaceRenderingSession BeginRendering()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(D3D12SwapchainRenderTarget));
        if (_graphics.IsLost)
            throw new PlatformGraphicsContextLostException();

        var newSize = new PixelSize(Math.Max(1, _window.Size.Width), Math.Max(1, _window.Size.Height));
        if (newSize != _currentSize)
        {
            _graphics.WaitForGpu();
            ReleaseBuffers();
            _swapChain.ResizeBuffers(BufferCount, (uint)newSize.Width, (uint)newSize.Height, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, 0);
            AcquireBuffers();
            _currentSize = newSize;
            Array.Clear(_frameFenceValues, 0, _frameFenceValues.Length);
        }

        var currentIndex = D3D12Interop.GetCurrentBackBufferIndex(_swapChain3);
        _graphics.WaitForFenceValue(_frameFenceValues[currentIndex]);

        var resource = _renderTargets[currentIndex];

        // Transition backbuffer to RENDER_TARGET for drawing
        _graphics.ExecuteTransition(resource, D3D12Interop.D3D12_RESOURCE_STATE_PRESENT, D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET);

        return new D3D12SwapchainRenderingSession(_graphics, _swapChain, resource, currentIndex, _frameFenceValues, _currentSize, _window.Scaling);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (!_graphics.IsLost)
                _graphics.WaitForGpu();
        }
        catch (PlatformGraphicsContextLostException)
        {
            // Continue releasing resources.
        }

        ReleaseBuffers();

        if (_swapChain3 != IntPtr.Zero)
            Marshal.Release(_swapChain3);

        _swapChain.Dispose();
    }
}

internal class D3D12SwapchainRenderingSession : ID3D12PlatformSurfaceRenderingSession
{
    private readonly D3D12PlatformGraphics _graphics;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IntPtr _resource;
    private readonly uint _bufferIndex;
    private readonly ulong[] _frameFenceValues;
    private readonly PixelSize _size;
    private readonly double _scaling;
    private bool _disposed;

    public D3D12SwapchainRenderingSession(
        D3D12PlatformGraphics graphics,
        IDXGISwapChain1 swapChain,
        IntPtr resource,
        uint bufferIndex,
        ulong[] frameFenceValues,
        PixelSize size,
        double scaling)
    {
        _graphics = graphics;
        _swapChain = swapChain;
        _resource = resource;
        _bufferIndex = bufferIndex;
        _frameFenceValues = frameFenceValues;
        _size = size;
        _scaling = scaling;
    }

    public IntPtr Resource => _resource;
    public uint ResourceState => D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET;
    public uint Format => (uint)DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
    public PixelSize Size => _size;
    public double Scaling => _scaling;
    public bool IsYFlipped => false;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Transition backbuffer back to PRESENT
        _graphics.ExecuteTransition(_resource, D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12Interop.D3D12_RESOURCE_STATE_PRESENT);

        var hr = _swapChain.Present(1, 0);
        if (hr < 0)
        {
            var err = (DXGI_ERROR)unchecked((uint)hr);
            if (err.IsDeviceLostError())
            {
                _graphics.MarkLost();
                throw new PlatformGraphicsContextLostException();
            }

            Marshal.ThrowExceptionForHR(hr);
        }

        // Per-back-buffer fence: do not WaitForGpu() here — that would stall every frame.
        _frameFenceValues[_bufferIndex] = _graphics.Signal();
    }
}
