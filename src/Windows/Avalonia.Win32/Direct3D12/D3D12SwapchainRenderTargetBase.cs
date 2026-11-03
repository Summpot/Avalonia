using System;
using Avalonia.Direct3D12;
using Avalonia.Platform;
using Avalonia.Win32.DirectX;
using MicroCom.Runtime;

namespace Avalonia.Win32.Direct3D12;

internal abstract unsafe class D3D12SwapchainRenderTargetBase : ID3D12PlatformSurfaceRenderTarget
{
    protected readonly D3D12PlatformGraphics _graphics;
    protected readonly int _deviceGeneration;
    protected readonly D3D12SwapchainBackBuffers _backBuffers = new();
    protected readonly D3D12PerBufferCommandResources _commandResources = new();
    protected IDXGISwapChain1? _swapChain;
    protected IDXGISwapChain3? _swapChain3;
    protected PixelSize _currentSize;
    protected bool _disposed;
    protected bool _lost;
    private bool _resourcesReleased;

    protected D3D12SwapchainRenderTargetBase(D3D12PlatformGraphics graphics)
    {
        _graphics = graphics;
        _deviceGeneration = graphics.DeviceGeneration;
        graphics.BeforeDeviceRelease += ReleaseDeviceResources;
    }

    public PlatformRenderTargetState State
    {
        get
        {
            if (_disposed)
                return PlatformRenderTargetState.Disposed;
            if (_lost
                || _graphics.IsLost
                || _graphics.DeviceGeneration != _deviceGeneration)
                return PlatformRenderTargetState.Corrupted;
            return PlatformRenderTargetState.Ready;
        }
    }

    protected void ThrowIfLostOrDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
        if (_graphics.DeviceGeneration != _deviceGeneration)
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
    }

    protected void InitializeSwapChain(
        IDXGIFactory2 factory,
        IDXGISwapChain1 swapChain,
        PixelSize size,
        Action<IDXGIFactory2, IDXGISwapChain1>? onInitialized = null)
    {
        try
        {
            onInitialized?.Invoke(factory, swapChain);

            _swapChain = swapChain;
            _swapChain3 = swapChain.QueryInterface<IDXGISwapChain3>();

            _currentSize = size;
            _backBuffers.ClearFrameFenceValues();
            _backBuffers.Acquire(swapChain);
        }
        catch
        {
            if (_swapChain is not null)
                DestroySwapchain();
            else
                swapChain.Dispose();
            throw;
        }
    }

    protected void DestroySwapchain()
    {
        _backBuffers.Release();

        _swapChain3?.Dispose();
        _swapChain3 = null;

        _swapChain?.Dispose();
        _swapChain = null;
    }

    protected void ResizeSwapchainBuffers(PixelSize newSize)
    {
        // DXGI requires all back-buffer references to be released before resizing buffers.
        _graphics.PrepareForSwapchainMutation();
        _backBuffers.Release();
        _swapChain!.ResizeBuffers(
            D3D12SwapchainBackBuffers.BufferCount,
            (uint)newSize.Width,
            (uint)newSize.Height,
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            0);
        _backBuffers.Acquire(_swapChain);
        _currentSize = newSize;
        _backBuffers.ClearFrameFenceValues();
    }

    protected (IntPtr resource, uint currentIndex) PrepareRenderBuffer()
    {
        var currentIndex = _swapChain3!.CurrentBackBufferIndex;
        _graphics.WaitForFenceValue(_backBuffers.FrameFenceValues[currentIndex]);

        var resource = _backBuffers.GetResource(currentIndex);

        _commandResources.AllocatorFenceValues[currentIndex] = _graphics.ExecuteTransition(
            _commandResources.GetAllocator(currentIndex),
            _commandResources.GetCommandList(currentIndex),
            resource,
            D3D12Interop.D3D12_RESOURCE_STATE_PRESENT,
            D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET,
            _commandResources.AllocatorFenceValues[currentIndex]);

        return (resource, currentIndex);
    }

    internal void MarkCorrupted() => _lost = true;

    public abstract ID3D12PlatformSurfaceRenderingSession BeginRendering(IRenderTarget.RenderTargetSceneInfo sceneInfo);

    protected virtual void DetachSwapchain()
    {
    }

    protected void ReleaseDeviceResources()
    {
        lock (_graphics.SyncRoot)
        {
            if (_resourcesReleased)
                return;
            _resourcesReleased = true;
            _lost = true;
            _graphics.BeforeDeviceRelease -= ReleaseDeviceResources;
            try
            {
                DetachSwapchain();
            }
            finally
            {
                DestroySwapchain();
                _commandResources.Dispose();
            }
        }
    }

    public virtual void Dispose()
    {
        lock (_graphics.SyncRoot)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (!_resourcesReleased && !_graphics.IsLost && _graphics.DeviceGeneration == _deviceGeneration)
                    _graphics.PrepareForSwapchainMutation();
            }
            catch (PlatformGraphicsContextLostException)
            {
            }
            finally
            {
                ReleaseDeviceResources();
            }
        }
    }
}

internal class D3D12SwapchainRenderingSession : ID3D12PlatformSurfaceRenderingSession
{
    private readonly D3D12SwapchainRenderTargetBase _target;
    private readonly D3D12PlatformGraphics _graphics;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IntPtr _resource;
    private readonly uint _bufferIndex;
    private readonly D3D12PerBufferCommandResources _commandResources;
    private readonly D3D12SwapchainBackBuffers _backBuffers;
    private readonly PixelSize _size;
    private readonly double _scaling;
    private readonly uint _syncInterval;
    private readonly IDisposable? _transaction;
    private bool _disposed;

    public D3D12SwapchainRenderingSession(
        D3D12SwapchainRenderTargetBase target,
        D3D12PlatformGraphics graphics,
        IDXGISwapChain1 swapChain,
        IntPtr resource,
        uint bufferIndex,
        D3D12PerBufferCommandResources commandResources,
        D3D12SwapchainBackBuffers backBuffers,
        PixelSize size,
        double scaling,
        uint syncInterval = 1,
        IDisposable? transaction = null)
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
        _syncInterval = syncInterval;
        _transaction = transaction;
    }

    public IntPtr Resource => _resource;
    public uint ResourceState => D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET;
    public D3D12DxgiFormat Format => D3D12DxgiFormat.B8G8R8A8_UNORM;
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

            var hr = _swapChain.Present(_syncInterval, 0);
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
                _transaction?.Dispose();
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
