using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Logging;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Win32.DirectX;
using MicroCom.Runtime;

namespace Avalonia.Win32.Direct3D12;

internal unsafe class D3D12PlatformGraphics : IPlatformGraphics, IWindowsSurfaceFactory, IDisposable
{
    // DXGI_ADAPTER_FLAG_SOFTWARE — prefer hardware adapters, then optional WARP.
    private const uint DxgiAdapterFlagSoftware = 0x2;

    private IntPtr _device;
    private IntPtr _commandQueue;
    private IntPtr _adapter;
    private IntPtr _commandAllocator;
    private IntPtr _commandList;
    private IntPtr _fence;
    private ulong _fenceValue;
    private ulong _commandAllocatorFenceValue;
    private IntPtr _fenceEvent;
    private readonly IntPtr[] _executeList = new IntPtr[1];
    private bool _disposed;
    private bool _isLost;

    public IntPtr DeviceHandle => _device;
    public IntPtr CommandQueueHandle => _commandQueue;
    public IntPtr AdapterHandle => _adapter;

    public bool UsesSharedContext => true;
    public bool RequiresNoRedirectionBitmap => true;

    private readonly D3D12PlatformGraphicsContext _context;

    public bool IsLost =>
        _isLost
        || _disposed
        || _device == IntPtr.Zero
        || D3D12Interop.GetDeviceRemovedReason(_device) != 0;

    private D3D12PlatformGraphics(IntPtr adapter, IntPtr device, IntPtr commandQueue, IntPtr allocator, IntPtr list, IntPtr fence, IntPtr fenceEvent)
    {
        _adapter = adapter;
        _device = device;
        _commandQueue = commandQueue;
        _commandAllocator = allocator;
        _commandList = list;
        _fence = fence;
        _fenceEvent = fenceEvent;
        _context = new D3D12PlatformGraphicsContext(this);
    }

    public static D3D12PlatformGraphics? TryCreate()
    {
        try
        {
            var dxgiFactoryGuid = MicroComRuntime.GetGuidFor(typeof(IDXGIFactory1));
            DirectXUnmanagedMethods.CreateDXGIFactory1(ref dxgiFactoryGuid, out var pDxgiFactory);
            if (pDxgiFactory == null)
                return null;

            using var factory = MicroComRuntime.CreateProxyFor<IDXGIFactory1>(pDxgiFactory, true);

            // Pass 1: hardware adapters (skip SOFTWARE / WARP).
            if (TryCreateFromAdapters(factory, softwareOnly: false, out var graphics))
                return graphics;

            // Pass 2: optional WARP / software adapter fallback.
            if (TryCreateFromAdapters(factory, softwareOnly: true, out graphics))
            {
                Logger.TryGet(LogEventLevel.Warning, "Direct3D12")
                    ?.Log(null, "Using DXGI software / WARP adapter for Direct3D 12.");
                return graphics;
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.TryGet(LogEventLevel.Error, "Direct3D12")
                ?.Log(null, "Failed to initialize Direct3D 12 platform graphics: {0}", ex);
            return null;
        }
    }

    private static bool TryCreateFromAdapters(IDXGIFactory1 factory, bool softwareOnly, out D3D12PlatformGraphics? graphics)
    {
        graphics = null;
        ushort adapterIndex = 0;
        void* pAdapter = null;
        while (factory.EnumAdapters1(adapterIndex, &pAdapter) == 0 && pAdapter != null)
        {
            adapterIndex++;
            using var adapterProxy = MicroComRuntime.CreateProxyFor<IDXGIAdapter1>(pAdapter, true);
            var desc = adapterProxy.Desc1;
            var isSoftware = (desc.Flags & DxgiAdapterFlagSoftware) != 0;
            if (isSoftware != softwareOnly)
                continue;

            var adapterPtr = MicroComRuntime.GetNativeIntPtr(adapterProxy);
            // Keep a ref for the graphics object if creation succeeds.
            Marshal.AddRef(adapterPtr);

            if (TryCreateOnAdapter(adapterPtr, out graphics))
                return true;

            Marshal.Release(adapterPtr);
        }

        return false;
    }

    private static bool TryCreateOnAdapter(IntPtr adapter, out D3D12PlatformGraphics? graphics)
    {
        graphics = null;

        var hr = D3D12Interop.D3D12CreateDevice(
            adapter,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
            in D3D12Interop.IID_ID3D12Device,
            out var device);

        if (hr != 0 || device == IntPtr.Zero)
            return false;

        var queueDesc = new D3D12Interop.D3D12_COMMAND_QUEUE_DESC
        {
            Type = D3D12Interop.D3D12_COMMAND_LIST_TYPE_DIRECT,
            Priority = 0,
            Flags = D3D12Interop.D3D12_COMMAND_QUEUE_FLAG_NONE,
            NodeMask = 0
        };

        hr = D3D12Interop.CreateCommandQueue(device, in queueDesc, in D3D12Interop.IID_ID3D12CommandQueue, out var commandQueue);
        if (hr != 0 || commandQueue == IntPtr.Zero)
        {
            D3D12Interop.Release(device);
            return false;
        }

        hr = D3D12Interop.CreateCommandAllocator(device, D3D12Interop.D3D12_COMMAND_LIST_TYPE_DIRECT, in D3D12Interop.IID_ID3D12CommandAllocator, out var allocator);
        if (hr != 0 || allocator == IntPtr.Zero)
        {
            D3D12Interop.Release(commandQueue);
            D3D12Interop.Release(device);
            return false;
        }

        hr = D3D12Interop.CreateCommandList(device, 0, D3D12Interop.D3D12_COMMAND_LIST_TYPE_DIRECT, allocator, IntPtr.Zero, in D3D12Interop.IID_ID3D12GraphicsCommandList, out var list);
        if (hr != 0 || list == IntPtr.Zero)
        {
            D3D12Interop.Release(allocator);
            D3D12Interop.Release(commandQueue);
            D3D12Interop.Release(device);
            return false;
        }

        hr = D3D12Interop.CloseCommandList(list);
        if (hr != 0)
        {
            D3D12Interop.Release(list);
            D3D12Interop.Release(allocator);
            D3D12Interop.Release(commandQueue);
            D3D12Interop.Release(device);
            return false;
        }

        hr = D3D12Interop.CreateFence(device, 0, D3D12Interop.D3D12_FENCE_FLAG_NONE, in D3D12Interop.IID_ID3D12Fence, out var fence);
        if (hr != 0 || fence == IntPtr.Zero)
        {
            D3D12Interop.Release(list);
            D3D12Interop.Release(allocator);
            D3D12Interop.Release(commandQueue);
            D3D12Interop.Release(device);
            return false;
        }

        var fenceEvent = D3D12Interop.CreateEventW(IntPtr.Zero, false, false, null);
        if (fenceEvent == IntPtr.Zero)
        {
            D3D12Interop.Release(fence);
            D3D12Interop.Release(list);
            D3D12Interop.Release(allocator);
            D3D12Interop.Release(commandQueue);
            D3D12Interop.Release(device);
            return false;
        }

        graphics = new D3D12PlatformGraphics(adapter, device, commandQueue, allocator, list, fence, fenceEvent);
        return true;
    }

    public IPlatformRenderSurface CreateSurface(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info)
        => new D3D12SwapchainWindowSurface(info, this);

    public void ExecuteTransition(IntPtr resource, uint stateBefore, uint stateAfter)
    {
        lock (this)
        {
            ThrowIfDisposedOrLost();

            // The allocator's memory must remain intact until its previous submission completes.
            WaitForFence(_commandAllocatorFenceValue);
            Marshal.ThrowExceptionForHR(D3D12Interop.ResetCommandAllocator(_commandAllocator));
            Marshal.ThrowExceptionForHR(D3D12Interop.ResetCommandList(_commandList, _commandAllocator, IntPtr.Zero));

            var barrier = D3D12Interop.D3D12_RESOURCE_BARRIER.CreateTransition(resource, stateBefore, stateAfter);
            D3D12Interop.ResourceBarrier(_commandList, 1, in barrier);
            Marshal.ThrowExceptionForHR(D3D12Interop.CloseCommandList(_commandList));

            _executeList[0] = _commandList;
            D3D12Interop.ExecuteCommandLists(_commandQueue, 1, _executeList);
            _commandAllocatorFenceValue = SignalQueue();
        }
    }

    public void WaitForGpu()
    {
        lock (this)
        {
            ThrowIfDisposedOrLost();
            WaitForFence(SignalQueue());
        }
    }

    /// <summary>
    /// Waits until the shared fence has reached <paramref name="value"/> (no-op if already complete).
    /// Used for per-back-buffer frame sync without a full GPU idle.
    /// </summary>
    public void WaitForFenceValue(ulong value)
    {
        lock (this)
        {
            ThrowIfDisposedOrLost();
            WaitForFence(value);
        }
    }

    /// <summary>
    /// Signals the shared fence and returns the new fence value for the caller to track.
    /// </summary>
    public ulong Signal()
    {
        lock (this)
        {
            ThrowIfDisposedOrLost();
            return SignalQueue();
        }
    }

    // Called with the graphics lock held so fence values and the shared event are serialized.
    private ulong SignalQueue()
    {
        var value = ++_fenceValue;
        Marshal.ThrowExceptionForHR(D3D12Interop.Signal(_commandQueue, _fence, value));
        return value;
    }

    private void WaitForFence(ulong value)
    {
        var completedValue = D3D12Interop.GetCompletedValue(_fence);
        if (completedValue == ulong.MaxValue)
        {
            _isLost = true;
            throw new PlatformGraphicsContextLostException();
        }

        if (completedValue < value)
        {
            Marshal.ThrowExceptionForHR(D3D12Interop.SetEventOnCompletion(_fence, value, _fenceEvent));
            if (D3D12Interop.WaitForSingleObject(_fenceEvent, 0xFFFFFFFF) != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private void ThrowIfDisposedOrLost()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(D3D12PlatformGraphics));
        if (IsLost)
            throw new PlatformGraphicsContextLostException();
    }

    internal void MarkLost() => _isLost = true;

    public IPlatformGraphicsContext CreateContext() => _context;
    public IPlatformGraphicsContext GetSharedContext() => _context;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (_device != IntPtr.Zero && _commandQueue != IntPtr.Zero && _fence != IntPtr.Zero)
            {
                try
                {
                    WaitForFence(SignalQueue());
                }
                catch
                {
                    // Best-effort drain; continue releasing COM objects.
                }
            }
        }
        finally
        {
            if (_fenceEvent != IntPtr.Zero)
            {
                D3D12Interop.CloseHandle(_fenceEvent);
                _fenceEvent = IntPtr.Zero;
            }

            ReleaseAndClear(ref _fence);
            ReleaseAndClear(ref _commandList);
            ReleaseAndClear(ref _commandAllocator);
            ReleaseAndClear(ref _commandQueue);
            ReleaseAndClear(ref _device);
            ReleaseAndClear(ref _adapter);
        }
    }

    private static void ReleaseAndClear(ref IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return;
        D3D12Interop.Release(ptr);
        ptr = IntPtr.Zero;
    }
}
