using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Logging;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Direct3D12;
using Avalonia.Platform.Surfaces;
using Avalonia.Reactive;
using Avalonia.Win32.DirectX;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32.Direct3D12;

internal unsafe class D3D12PlatformGraphics : IPlatformGraphics, IWindowsSurfaceFactory, IDisposable
{
    // DXGI_ADAPTER_FLAG_SOFTWARE — prefer hardware adapters, then software adapters.
    private const uint DxgiAdapterFlagSoftware = 0x2;

    private ID3D12Device? _device;
    private ID3D12CommandQueue? _commandQueue;
    private IDXGIAdapter1? _adapter;
    private ID3D12Fence? _fence;
    private ulong _fenceValue;
    private IntPtr _fenceEvent;
    internal object SyncRoot { get; } = new();
    private bool _disposed;
    private bool _isLost;
    private int _deviceGeneration;
    private byte[]? _deviceLuid;
    private ID3D12PlatformGraphicsExternalObjects? _externalObjects;

    // D3D11On12 companions for composition drawing surfaces (device-lifetime; rebuilt with DeviceGeneration).
    private IUnknown? _d3d11Device;
    private ID3D11DeviceContext? _d3d11Context;
    private ID3D11On12Device? _d3d11On12Device;
    private ID3D11On12Device2? _d3d11On12Device2;
    private int _d3d11On12Generation;
    private Func<D3D12PlatformGraphics, D3D12DrawingSurfaceCapabilities>? _drawingSurfaceProbe;
    private D3D12DrawingSurfaceCapabilities? _drawingSurfaceCapabilities;

    /// <summary>
    /// Invoked immediately before releasing swapchain buffers to drop external references.
    /// </summary>
    internal event Action? BeforeSwapchainBufferRelease;

    // All device-owned resources must be released before recreating the adapter's D3D12 device.
    internal event Action? BeforeDeviceRelease;

    public IntPtr DeviceHandle => _device?.GetNativeIntPtr() ?? IntPtr.Zero;
    public IntPtr CommandQueueHandle => _commandQueue?.GetNativeIntPtr() ?? IntPtr.Zero;
    public IntPtr AdapterHandle => _adapter?.GetNativeIntPtr() ?? IntPtr.Zero;
    internal ID3D12CommandQueue CommandQueue => _commandQueue!;
    public byte[]? DeviceLuid => _deviceLuid;
    public ID3D12PlatformGraphicsExternalObjects ExternalObjects =>
        _externalObjects ??= new D3D12ExternalObjectsFeature(this);

    /// <summary>
    /// D3D11 device created via D3D11On12 over this D3D12 device/queue.
    /// Used to create WinUI / DComp composition graphics devices and drawing surfaces.
    /// </summary>
    internal IntPtr D3D11DeviceHandle
    {
        get
        {
            lock (SyncRoot)
            {
                EnsureD3D11On12Unlocked();
                return _d3d11Device!.GetNativeIntPtr();
            }
        }
    }

    /// <summary>
    /// Drawing-surface format and rendering path selected for the current device generation.
    /// </summary>
    internal D3D12DrawingSurfaceCapabilities DrawingSurfaceCapabilities
    {
        get
        {
            lock (SyncRoot)
            {
                ThrowIfDisposedOrLost();
                return _drawingSurfaceCapabilities
                    ?? throw new InvalidOperationException("Drawing-surface presentation has not been initialized.");
            }
        }
    }

    internal DXGI_FORMAT DrawingSurfaceFormat => DrawingSurfaceCapabilities.Format;

    /// <summary>
    /// Probes the composition connection before creating window surfaces and repeats
    /// the probe when the D3D12 device is recreated.
    /// </summary>
    internal void InitializeDrawingSurface(Func<D3D12PlatformGraphics, D3D12DrawingSurfaceCapabilities> probe)
    {
        lock (SyncRoot)
        {
            ThrowIfDisposedOrLost();
            if (_drawingSurfaceProbe is not null)
                throw new InvalidOperationException("Drawing-surface presentation is already initialized.");

            _drawingSurfaceCapabilities = probe(this);
            _drawingSurfaceProbe = probe;
        }
    }

    /// <summary>
    /// Incremented each time the underlying D3D12 device is (re)created.
    /// Swapchain targets compare this to detect stale resources after device recovery.
    /// </summary>
    public int DeviceGeneration => _deviceGeneration;

    public bool UsesSharedContext => true;

    internal IDisposable EnsureCurrent(int deviceGeneration)
    {
        Monitor.Enter(SyncRoot);
        try
        {
            ThrowIfDisposedOrLost();
            if (deviceGeneration != _deviceGeneration)
                throw new PlatformGraphicsContextLostException();
            return Disposable.Create(() => Monitor.Exit(SyncRoot));
        }
        catch
        {
            Monitor.Exit(SyncRoot);
            throw;
        }
    }

    /// <summary>
    /// The HWND presentation path creates the window without a redirection bitmap and is opaque-only
    /// (<c>DXGI_ALPHA_MODE_IGNORE</c>). Transparency, Acrylic and Mica require a composition mode.
    /// </summary>
    public bool RequiresNoRedirectionBitmap => true;

    private D3D12PlatformGraphicsContext? _context;

    public bool IsLost
    {
        get
        {
            // Serialized with device release and recreation, which free the native device.
            lock (SyncRoot)
            {
                return _isLost
                       || _disposed
                       || _device is null
                       || _device!.DeviceRemovedReason != 0;
            }
        }
    }

    private D3D12PlatformGraphics(IntPtr adapter, IntPtr device, IntPtr commandQueue, IntPtr fence, IntPtr fenceEvent)
    {
        _adapter = MicroComRuntime.CreateProxyFor<IDXGIAdapter1>(adapter, true);
        _device = MicroComRuntime.CreateProxyFor<ID3D12Device>(device, true);
        _commandQueue = MicroComRuntime.CreateProxyFor<ID3D12CommandQueue>(commandQueue, true);
        _fence = MicroComRuntime.CreateProxyFor<ID3D12Fence>(fence, true);
        _fenceEvent = fenceEvent;
        _deviceGeneration = 1;
        UpdateDeviceLuid();
    }

    public static D3D12PlatformGraphics? TryCreate()
    {
        try
        {
            if (!TryCreateNativeResources(out var adapter, out var device, out var queue, out var fence, out var fenceEvent))
                return null;

            return new D3D12PlatformGraphics(adapter, device, queue, fence, fenceEvent);
        }
        catch (Exception ex)
        {
            Logger.TryGet(LogEventLevel.Error, LogArea.Win32Platform)
                ?.Log(null, "Failed to initialize Direct3D 12 platform graphics: {0}", ex);
            return null;
        }
    }

    private static bool TryCreateNativeResources(
        out IntPtr adapter,
        out IntPtr device,
        out IntPtr commandQueue,
        out IntPtr fence,
        out IntPtr fenceEvent)
    {
        adapter = device = commandQueue = fence = fenceEvent = IntPtr.Zero;

        var dxgiFactoryGuid = MicroComRuntime.GetGuidFor(typeof(IDXGIFactory1));
        DirectXUnmanagedMethods.CreateDXGIFactory1(ref dxgiFactoryGuid, out var pDxgiFactory);
        if (pDxgiFactory == null)
            return false;

        using var factory = MicroComRuntime.CreateProxyFor<IDXGIFactory1>(pDxgiFactory, true);

        var selectionCallback = AvaloniaLocator.Current.GetService<Win32PlatformOptions>()
            ?.GraphicsAdapterSelectionCallback;

        // An explicit selection uses only that adapter; there is no fallback to another one.
        if (selectionCallback != null)
            return TryCreateFromSelectedAdapter(factory, selectionCallback, out adapter, out device, out commandQueue, out fence, out fenceEvent);

        // Default path: two-pass walk — try each hardware adapter, then each
        // SOFTWARE/WARP adapter — so one failing card does not block startup.
        if (TryCreateFromAdapters(factory, softwareOnly: false, out adapter, out device, out commandQueue, out fence, out fenceEvent))
            return true;

        if (TryCreateFromAdapters(factory, softwareOnly: true, out adapter, out device, out commandQueue, out fence, out fenceEvent))
        {
            Logger.TryGet(LogEventLevel.Warning, LogArea.Win32Platform)
                ?.Log(null, "Using DXGI software / WARP adapter for Direct3D 12.");
            return true;
        }

        return false;
    }

    private static bool TryCreateFromSelectedAdapter(
        IDXGIFactory1 factory,
        Func<IReadOnlyList<PlatformGraphicsDeviceAdapterDescription>, int> selectionCallback,
        out IntPtr adapter,
        out IntPtr device,
        out IntPtr commandQueue,
        out IntPtr fence,
        out IntPtr fenceEvent)
    {
        adapter = device = commandQueue = fence = fenceEvent = IntPtr.Zero;

        var adapters = new List<(IntPtr ptr, PlatformGraphicsDeviceAdapterDescription desc)>();
        ushort adapterIndex = 0;
        void* pAdapter = null;
        while (factory.EnumAdapters1(adapterIndex, &pAdapter) == 0 && pAdapter != null)
        {
            adapterIndex++;
            using var adapterProxy = MicroComRuntime.CreateProxyFor<IDXGIAdapter1>(pAdapter, true);
            var desc1 = adapterProxy.Desc1;
            var name = Marshal.PtrToStringUni(new IntPtr(desc1.Description))?.ToLowerInvariant();
            var luid = BitConverter.GetBytes(desc1.AdapterLuid);
            var adapterPtr = MicroComRuntime.GetNativeIntPtr(adapterProxy);
            Marshal.AddRef(adapterPtr);
            adapters.Add((adapterPtr, new PlatformGraphicsDeviceAdapterDescription
            {
                Description = name,
                DeviceLuid = luid
            }));
        }

        if (adapters.Count == 0)
            return false;

        try
        {
            var chosenIndex = selectionCallback(adapters.Select(a => a.desc).ToArray());
            if (chosenIndex < 0 || chosenIndex >= adapters.Count)
            {
                Logger.TryGet(LogEventLevel.Error, LogArea.Win32Platform)
                    ?.Log(null,
                        "GraphicsAdapterSelectionCallback returned invalid index {Index} (adapter count {Count}).",
                        chosenIndex, adapters.Count);
                return false;
            }

            var chosenPtr = adapters[chosenIndex].ptr;
            if (!TryCreateOnAdapter(chosenPtr, out device, out commandQueue, out fence, out fenceEvent))
            {
                Logger.TryGet(LogEventLevel.Error, LogArea.Win32Platform)
                    ?.Log(null,
                        "Failed to create Direct3D 12 device on adapter selected by GraphicsAdapterSelectionCallback ('{Description}').",
                        adapters[chosenIndex].desc.Description);
                return false;
            }

            // Transfer ownership of the chosen adapter to the caller; release the rest below.
            adapter = chosenPtr;
            adapters[chosenIndex] = (IntPtr.Zero, adapters[chosenIndex].desc);
            return true;
        }
        finally
        {
            foreach (var (ptr, _) in adapters)
            {
                if (ptr != IntPtr.Zero)
                    Marshal.Release(ptr);
            }
        }
    }

    private static bool TryCreateFromAdapters(
        IDXGIFactory1 factory,
        bool softwareOnly,
        out IntPtr adapter,
        out IntPtr device,
        out IntPtr commandQueue,
        out IntPtr fence,
        out IntPtr fenceEvent)
    {
        adapter = device = commandQueue = fence = fenceEvent = IntPtr.Zero;
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
            Marshal.AddRef(adapterPtr);

            var success = false;
            try
            {
                if (TryCreateOnAdapter(adapterPtr, out device, out commandQueue, out fence, out fenceEvent))
                {
                    adapter = adapterPtr;
                    success = true;
                    return true;
                }
            }
            finally
            {
                if (!success)
                {
                    Marshal.Release(adapterPtr);
                }
            }
        }

        return false;
    }

    private static bool TryCreateOnAdapter(
        IntPtr adapter,
        out IntPtr device,
        out IntPtr commandQueue,
        out IntPtr fence,
        out IntPtr fenceEvent)
    {
        // Device, command queue, and fence are shared; command allocators and lists are managed per swapchain buffer.
        device = commandQueue = fence = fenceEvent = IntPtr.Zero;

        var hr = D3D12Interop.D3D12CreateDevice(
            adapter,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
            in D3D12Interop.IID_ID3D12Device,
            out device);

        if (hr != 0 || device == IntPtr.Zero)
            return false;

        var queueDesc = new D3D12Interop.D3D12_COMMAND_QUEUE_DESC
        {
            Type = D3D12Interop.D3D12_COMMAND_LIST_TYPE_DIRECT,
            Priority = 0,
            Flags = D3D12Interop.D3D12_COMMAND_QUEUE_FLAG_NONE,
            NodeMask = 0
        };

        hr = D3D12Interop.CreateCommandQueue(device, in queueDesc, in D3D12Interop.IID_ID3D12CommandQueue, out commandQueue);
        if (hr != 0 || commandQueue == IntPtr.Zero)
        {
            D3D12Interop.Release(device);
            device = IntPtr.Zero;
            return false;
        }

        hr = D3D12Interop.CreateFence(device, 0, D3D12Interop.D3D12_FENCE_FLAG_NONE, in D3D12Interop.IID_ID3D12Fence, out fence);
        if (hr != 0 || fence == IntPtr.Zero)
        {
            D3D12Interop.Release(commandQueue);
            D3D12Interop.Release(device);
            commandQueue = device = IntPtr.Zero;
            return false;
        }

        fenceEvent = UnmanagedMethods.CreateEventW(IntPtr.Zero, false, false, null);
        if (fenceEvent == IntPtr.Zero)
        {
            D3D12Interop.Release(fence);
            D3D12Interop.Release(commandQueue);
            D3D12Interop.Release(device);
            fence = commandQueue = device = IntPtr.Zero;
            return false;
        }

        return true;
    }

    public IPlatformRenderSurface CreateSurface(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info)
        => new D3D12SwapchainWindowSurface(info, this);


    /// <summary>
    /// Creates a direct command allocator for one swapchain back-buffer / frame-in-flight slot.
    /// </summary>
    public ID3D12CommandAllocator CreateCommandAllocator()
    {
        lock (SyncRoot)
        {
            ThrowIfDisposedOrLost();
            Marshal.ThrowExceptionForHR(D3D12Interop.CreateCommandAllocator(
                DeviceHandle,
                D3D12Interop.D3D12_COMMAND_LIST_TYPE_DIRECT,
                in D3D12Interop.IID_ID3D12CommandAllocator,
                out var allocator));
            return MicroComRuntime.CreateProxyFor<ID3D12CommandAllocator>(allocator, true);
        }
    }

    /// <summary>
    /// Creates a graphics command list bound to <paramref name="allocator"/> and closes it
    /// so the first use can <c>Reset</c> like a recycled list.
    /// </summary>
    public ID3D12GraphicsCommandList CreateClosedCommandList(ID3D12CommandAllocator allocator)
    {
        lock (SyncRoot)
        {
            ThrowIfDisposedOrLost();
            Marshal.ThrowExceptionForHR(D3D12Interop.CreateCommandList(
                DeviceHandle,
                0,
                D3D12Interop.D3D12_COMMAND_LIST_TYPE_DIRECT,
                allocator.GetNativeIntPtr(),
                IntPtr.Zero,
                in D3D12Interop.IID_ID3D12GraphicsCommandList,
                out var list));
            var commandList = MicroComRuntime.CreateProxyFor<ID3D12GraphicsCommandList>(list, true);
            var hr = commandList.Close();
            if (hr != 0)
            {
                commandList.Dispose();
                Marshal.ThrowExceptionForHR(hr);
            }

            return commandList;
        }
    }

    /// <summary>
    /// Records a resource-state transition on the caller's per-slot allocator/list.
    /// When <paramref name="resetAllocator"/> is true (Begin of a frame), waits until
    /// <paramref name="allocatorFenceValue"/> completes, then resets the allocator —
    /// other windows' slots are unaffected. When false (same-frame Present barrier),
    /// only resets the command list so the Begin fence is not waited again.
    /// Returns the fence value signaled after <c>ExecuteCommandLists</c>.
    /// </summary>
    public ulong ExecuteTransition(
        ID3D12CommandAllocator commandAllocator,
        ID3D12GraphicsCommandList commandList,
        IntPtr resource,
        uint stateBefore,
        uint stateAfter,
        ulong allocatorFenceValue,
        bool resetAllocator = true)
    {
        lock (SyncRoot)
        {
            ThrowIfDisposedOrLost();
            if (resetAllocator && allocatorFenceValue > 0)
            {
                WaitForFence(allocatorFenceValue);
                Marshal.ThrowExceptionForHR(commandAllocator.Reset());
            }

            Marshal.ThrowExceptionForHR(commandList.Reset((void*)commandAllocator.GetNativeIntPtr(), null));
            var barrier = D3D12Interop.D3D12_RESOURCE_BARRIER.CreateTransition(resource, stateBefore, stateAfter);
            commandList.ResourceBarrier(1, &barrier);
            Marshal.ThrowExceptionForHR(commandList.Close());

            var pList = (void*)commandList.GetNativeIntPtr();
            _commandQueue!.ExecuteCommandLists(1, &pList);
            return SignalQueue();
        }
    }

    internal IntPtr CreateCommittedTexture2D(
        uint format,
        uint width,
        uint height,
        uint resourceFlags,
        uint initialState)
    {
        lock (SyncRoot)
        {
            ThrowIfDisposedOrLost();
            var heap = D3D12Interop.D3D12_HEAP_PROPERTIES.Create(D3D12Interop.D3D12_HEAP_TYPE_DEFAULT);
            var desc = D3D12Interop.D3D12_RESOURCE_DESC.Texture2D(format, width, height, resourceFlags);
            Marshal.ThrowExceptionForHR(D3D12Interop.CreateCommittedResource(
                DeviceHandle,
                in heap,
                D3D12Interop.D3D12_HEAP_FLAG_NONE,
                in desc,
                initialState,
                IntPtr.Zero,
                in D3D12Interop.IID_ID3D12Resource,
                out var resource));
            return resource;
        }
    }

    public void WaitForGpu()
    {
        lock (SyncRoot)
        {
            ThrowIfDisposedOrLost();
            WaitForFence(SignalQueue());
        }
    }

    /// <summary>
    /// Flushes external references (such as Skia resource wraps) and drains the GPU
    /// before resizing or destroying swapchain buffers.
    /// </summary>
    public void PrepareForSwapchainMutation()
    {
        lock (SyncRoot)
        {
            BeforeSwapchainBufferRelease?.Invoke();
            WaitForGpu();
        }
    }

    /// <summary>
    /// Waits until the shared fence has reached <paramref name="value"/> (no-op if already complete).
    /// Used for per-back-buffer frame sync without a full GPU idle.
    /// </summary>
    public void WaitForFenceValue(ulong value)
    {
        lock (SyncRoot)
        {
            if (value != 0)
                WaitForFence(value);
        }
    }

    /// <summary>
    /// Signals the shared fence and returns the new fence value for the caller to track.
    /// </summary>
    public ulong Signal()
    {
        lock (SyncRoot)
        {
            ThrowIfDisposedOrLost();
            return SignalQueue();
        }
    }

    // Called with SyncRoot held to serialize queue submission and fence signaling.
    private ulong SignalQueue()
    {
        var value = ++_fenceValue;
        Marshal.ThrowExceptionForHR(_commandQueue!.Signal((void*)_fence!.GetNativeIntPtr(), value));
        return value;
    }

    // Called with the graphics lock held to serialize fence event waits and poll for device removal.
    private void WaitForFence(ulong value)
    {
        ThrowIfDisposedOrLost();

        var completedValue = _fence!.CompletedValue;
        if (completedValue == ulong.MaxValue)
        {
            _isLost = true;
            throw new PlatformGraphicsContextLostException();
        }

        if (completedValue >= value)
            return;

        Marshal.ThrowExceptionForHR(_fence!.SetEventOnCompletion(value, _fenceEvent));

        const uint SliceTimeoutMs = 1000;
        const uint WAIT_OBJECT_0 = 0;
        const uint WAIT_TIMEOUT = 258;

        while (true)
        {
            var waitResult = UnmanagedMethods.WaitForSingleObject(_fenceEvent, SliceTimeoutMs);
            if (waitResult == WAIT_OBJECT_0)
                break;

            if (waitResult == WAIT_TIMEOUT)
            {
                if (_device is null || _device!.DeviceRemovedReason != 0)
                {
                    _isLost = true;
                    throw new PlatformGraphicsContextLostException();
                }

                var current = _fence!.CompletedValue;
                if (current == ulong.MaxValue)
                {
                    _isLost = true;
                    throw new PlatformGraphicsContextLostException();
                }

                if (current >= value)
                    break;

                continue;
            }

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

    /// <summary>
    /// Ensures a D3D11On12 device/context exists for the current <see cref="DeviceGeneration"/>.
    /// Caller must hold <c>lock (SyncRoot)</c>.
    /// </summary>
    private void EnsureD3D11On12Unlocked()
    {
        ThrowIfDisposedOrLost();
        if (_d3d11On12Device is not null && _d3d11On12Generation == _deviceGeneration)
            return;

        ReleaseD3D11On12Unlocked();

        var queue = CommandQueueHandle;
        var hr = D3D12Interop.D3D11On12CreateDevice(
            DeviceHandle,
            D3D12Interop.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            null,
            0,
            &queue,
            1,
            0,
            out var d3d11Device,
            out var d3d11Context,
            null);
        Marshal.ThrowExceptionForHR(hr);
        if (d3d11Device == IntPtr.Zero)
            throw new InvalidOperationException("D3D11On12CreateDevice returned a null ID3D11Device.");

        using var device = MicroComRuntime.CreateProxyFor<IUnknown>(d3d11Device, true);
        using var context = MicroComRuntime.CreateProxyFor<ID3D11DeviceContext>(d3d11Context, true);
        _d3d11On12Device = device.QueryInterface<ID3D11On12Device>();
        _d3d11Device = device.CloneReference();
        _d3d11Context = context.CloneReference();
        _d3d11On12Generation = _deviceGeneration;
    }

    private void UpdateDeviceLuid()
    {
        if (_adapter is not null)
        {
            var desc = _adapter.Desc1;
            _deviceLuid = BitConverter.GetBytes(desc.AdapterLuid);
        }
        else
        {
            _deviceLuid = null;
        }
    }

    private void ReleaseD3D11On12Unlocked()
    {
        ReleaseAndClear(ref _d3d11On12Device2);
        ReleaseAndClear(ref _d3d11On12Device);
        ReleaseAndClear(ref _d3d11Context);
        ReleaseAndClear(ref _d3d11Device);
        _d3d11On12Generation = 0;
    }

    internal IntPtr UnwrapCompositionTexture(IntPtr texture)
    {
        lock (SyncRoot)
        {
            if (texture == IntPtr.Zero)
                throw new ArgumentNullException(nameof(texture));
            EnsureD3D11On12Unlocked();
            if (_d3d11On12Device2 is null)
                _d3d11On12Device2 = _d3d11On12Device!.QueryInterface<ID3D11On12Device2>();

            var iid = D3D12Interop.IID_ID3D12Resource;
            void* resource;
            Marshal.ThrowExceptionForHR(_d3d11On12Device2.UnwrapUnderlyingResource(
                (void*)texture, (void*)_commandQueue!.GetNativeIntPtr(), &iid, &resource));
            // Unwrap may enqueue transitions in the translation layer. Submit these before
            // native D3D12 work on the shared queue, including any waits it depends on.
            _d3d11Context!.Flush();
            return (IntPtr)resource;
        }
    }

    internal void ReturnCompositionTexture(IntPtr texture, ulong fenceValue)
    {
        lock (SyncRoot)
        {
            ThrowIfDisposedOrLost();
            var fence = (void*)_fence!.GetNativeIntPtr();
            Marshal.ThrowExceptionForHR(_d3d11On12Device2!.ReturnUnderlyingResource(
                (void*)texture, fenceValue == 0 ? 0u : 1u, &fenceValue, &fence));
            _d3d11Context!.Flush();
        }
    }

    /// <summary>
    /// Wraps a D3D12 offscreen RT as <c>ID3D11Texture2D</c> for D3D11 copies into BeginDraw
    /// textures. In/Out state stay RENDER_TARGET so Skia can reuse the resource next frame.
    /// Returned pointer owns a reference the caller must Release.
    /// </summary>
    internal IntPtr CreateWrappedOffscreenTexture(IntPtr d3d12Resource)
    {
        lock (SyncRoot)
        {
            EnsureD3D11On12Unlocked();
            var flags = new D3D12Interop.D3D11_RESOURCE_FLAGS
            {
                BindFlags = D3D12Interop.D3D11_BIND_RENDER_TARGET
            };
            var iid = D3D12Interop.IID_ID3D11Texture2D;
            void* wrapped;
            var hr = _d3d11On12Device!.CreateWrappedResource(
                (void*)d3d12Resource,
                &flags,
                D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET,
                D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET,
                &iid,
                &wrapped);
            if (hr != 0 || wrapped == null)
            {
                Marshal.ThrowExceptionForHR(hr != 0 ? hr : unchecked((int)0x80070057)); // E_INVALIDARG
            }
            return (IntPtr)wrapped;
        }
    }

    /// <summary>
    /// After Skia Flush: Acquire the wrapped offscreen, D3D11-copy into the BeginDraw
    /// texture at <paramref name="dstOffset"/>, Release, Flush.
    /// D3D12 and D3D11On12 share one queue, so ordering plus Acquire/Release/Flush
    /// covers interop; a full-queue CPU wait is reserved for resource reuse/reclaim.
    /// Used when initialization selects the offscreen copy path.
    /// </summary>
    internal void CopyWrappedOffscreenToComposition(
        IntPtr wrappedOffscreen11,
        IntPtr compositionTexture11,
        PixelPoint dstOffset)
    {
        lock (SyncRoot)
        {
            ThrowIfDisposedOrLost();
            if (wrappedOffscreen11 == IntPtr.Zero)
                throw new ArgumentNullException(nameof(wrappedOffscreen11));
            if (compositionTexture11 == IntPtr.Zero)
                throw new ArgumentNullException(nameof(compositionTexture11));
            EnsureD3D11On12Unlocked();

            var resource = (void*)wrappedOffscreen11;
            _d3d11On12Device!.AcquireWrappedResources(&resource, 1);
            try
            {
                _d3d11Context!.CopySubresourceRegion(
                    (void*)compositionTexture11,
                    0,
                    (uint)dstOffset.X,
                    (uint)dstOffset.Y,
                    0,
                    (void*)wrappedOffscreen11,
                    0,
                    null);
            }
            finally
            {
                _d3d11On12Device!.ReleaseWrappedResources(&resource, 1);
                // Required before D3D12 reuses the resource (next Skia frame).
                _d3d11Context!.Flush();
            }
        }
    }

    internal void MarkLost() => _isLost = true;

    public IPlatformGraphicsContext CreateContext() => GetSharedContext();

    /// <summary>
    /// Returns a context for the current device generation, recreating a lost device if necessary.
    /// Older contexts remain lost. Throws <see cref="PlatformGraphicsContextLostException"/>
    /// if device recreation fails.
    /// </summary>
    public IPlatformGraphicsContext GetSharedContext()
    {
        lock (SyncRoot)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(D3D12PlatformGraphics));

            if (_context?.IsLost == true)
                _context = null;

            if (_context is null)
            {
                if (IsLost)
                {
                    if (!TryRecreateDeviceUnlocked())
                        throw new PlatformGraphicsContextLostException();
                }

                _context = new D3D12PlatformGraphicsContext(this);
            }

            return _context;
        }
    }

    /// <summary>
    /// Releases native objects and attempts to create a fresh D3D12 device.
    /// Caller must hold <c>lock (SyncRoot)</c>.
    /// </summary>
    private bool TryRecreateDeviceUnlocked()
    {
        Logger.TryGet(LogEventLevel.Warning, LogArea.Win32Platform)
            ?.Log(this, "Direct3D 12 device lost; attempting to recreate.");

        ReleaseNativeResources(drainGpu: false);

        try
        {
            if (!TryCreateNativeResources(out var adapter, out var device, out var queue, out var fence, out var fenceEvent))
            {
                _isLost = true;
                Logger.TryGet(LogEventLevel.Error, LogArea.Win32Platform)
                    ?.Log(this, "Failed to recreate Direct3D 12 device after device lost.");
                return false;
            }

            _adapter = MicroComRuntime.CreateProxyFor<IDXGIAdapter1>(adapter, true);
            _device = MicroComRuntime.CreateProxyFor<ID3D12Device>(device, true);
            _commandQueue = MicroComRuntime.CreateProxyFor<ID3D12CommandQueue>(queue, true);
            _fence = MicroComRuntime.CreateProxyFor<ID3D12Fence>(fence, true);
            _fenceEvent = fenceEvent;
            _fenceValue = 0;
            _isLost = false;
            _deviceGeneration++;
            UpdateDeviceLuid();
            _drawingSurfaceCapabilities = _drawingSurfaceProbe?.Invoke(this);

            Logger.TryGet(LogEventLevel.Information, LogArea.Win32Platform)
                ?.Log(this, "Direct3D 12 device recreated (generation {Generation}).", _deviceGeneration);
            return true;
        }
        catch (Exception ex)
        {
            _isLost = true;
            Logger.TryGet(LogEventLevel.Error, LogArea.Win32Platform)
                ?.Log(this, "Exception while recreating Direct3D 12 device: {0}", ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (SyncRoot)
        {
            if (_disposed)
                return;
            try
            {
                ReleaseNativeResources(drainGpu: true);
            }
            finally
            {
                _disposed = true;
                _context = null;
            }
        }
    }

    private void ReleaseNativeResources(bool drainGpu)
    {
        _drawingSurfaceCapabilities = null;
        _externalObjects = null;
        try
        {
            if (drainGpu && _device is not null && _commandQueue is not null && _fence is not null)
            {
                try
                {
                    WaitForFence(SignalQueue());
                }
                catch
                {
                }
            }
        }
        finally
        {
            var releaseResources = BeforeDeviceRelease;
            BeforeDeviceRelease = null;
            if (releaseResources is not null)
            {
                foreach (Action release in releaseResources.GetInvocationList())
                {
                    try
                    {
                        release();
                    }
                    catch (Exception e)
                    {
                        Logger.TryGet(LogEventLevel.Warning, LogArea.Win32Platform)
                            ?.Log(this, "Failed to release a Direct3D 12 device resource: {0}", e);
                    }
                }
            }
            ReleaseD3D11On12Unlocked();

            if (_fenceEvent != IntPtr.Zero)
            {
                UnmanagedMethods.CloseHandle(_fenceEvent);
                _fenceEvent = IntPtr.Zero;
            }

            ReleaseAndClear(ref _fence);
            ReleaseAndClear(ref _commandQueue);
            ReleaseAndClear(ref _device);
            ReleaseAndClear(ref _adapter);
        }
    }

    private static void ReleaseAndClear<T>(ref T? resource) where T : class, IDisposable
    {
        resource?.Dispose();
        resource = null;
    }
}
