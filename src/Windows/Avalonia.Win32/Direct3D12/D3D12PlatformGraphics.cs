using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Logging;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Direct3D12;
using Avalonia.Platform.Surfaces;
using Avalonia.Win32.DirectX;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32.Direct3D12;

internal unsafe class D3D12PlatformGraphics : IPlatformGraphics, IWindowsSurfaceFactory, IDisposable
{
    // DXGI_ADAPTER_FLAG_SOFTWARE — prefer hardware adapters, then optional WARP.
    private const uint DxgiAdapterFlagSoftware = 0x2;

    private IntPtr _device;
    private IntPtr _commandQueue;
    private IntPtr _adapter;
    private IntPtr _fence;
    private ulong _fenceValue;
    private IntPtr _fenceEvent;
    private bool _disposed;
    private bool _isLost;
    private int _deviceGeneration;

    // D3D11On12 companions for composition drawing surfaces (device-lifetime; rebuilt with DeviceGeneration).
    private IntPtr _d3d11Device;
    private IntPtr _d3d11Context;
    private IntPtr _d3d11On12Device;
    private IntPtr _d3d11On12Device2;
    private int _d3d11On12Generation;
    private Func<D3D12PlatformGraphics, D3D12DrawingSurfaceCapabilities>? _drawingSurfaceProbe;
    private D3D12DrawingSurfaceCapabilities? _drawingSurfaceCapabilities;

    /// <summary>
    /// Invoked immediately before releasing swapchain buffers to drop external references.
    /// </summary>
    internal event Action? BeforeSwapchainBufferRelease;

    public IntPtr DeviceHandle => _device;
    public IntPtr CommandQueueHandle => _commandQueue;
    public IntPtr AdapterHandle => _adapter;

    /// <summary>
    /// D3D11 device created via D3D11On12 over this D3D12 device/queue.
    /// Used to create WinUI / DComp composition graphics devices and drawing surfaces.
    /// </summary>
    internal IntPtr D3D11DeviceHandle
    {
        get
        {
            lock (this)
            {
                EnsureD3D11On12Unlocked();
                return _d3d11Device;
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
            lock (this)
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
        lock (this)
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

    /// <summary>
    /// The HWND presentation path creates the window without a redirection bitmap and is opaque-only
    /// (<c>DXGI_ALPHA_MODE_IGNORE</c>). Transparency, Acrylic and Mica require a composition mode.
    /// </summary>
    public bool RequiresNoRedirectionBitmap => true;

    /// <summary>
    /// Current shared context. Replaced (not mutated) after device-lost recovery so
    /// holders of the previous instance keep seeing <see cref="IPlatformGraphicsContext.IsLost"/>.
    /// </summary>
    private D3D12PlatformGraphicsContext? _context;

    public bool IsLost
    {
        get
        {
            // Serialized with device release and recreation, which free the native device.
            lock (this)
            {
                return _isLost
                       || _disposed
                       || _device == IntPtr.Zero
                       || D3D12Interop.GetDeviceRemovedReason(_device) != 0;
            }
        }
    }

    private D3D12PlatformGraphics(IntPtr adapter, IntPtr device, IntPtr commandQueue, IntPtr fence, IntPtr fenceEvent)
    {
        _adapter = adapter;
        _device = device;
        _commandQueue = commandQueue;
        _fence = fence;
        _fenceEvent = fenceEvent;
        _deviceGeneration = 1;
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
            Logger.TryGet(LogEventLevel.Error, "Direct3D12")
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
            Logger.TryGet(LogEventLevel.Warning, "Direct3D12")
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
                Logger.TryGet(LogEventLevel.Error, "Direct3D12")
                    ?.Log(null,
                        "GraphicsAdapterSelectionCallback returned invalid index {Index} (adapter count {Count}).",
                        chosenIndex, adapters.Count);
                return false;
            }

            var chosenPtr = adapters[chosenIndex].ptr;
            if (!TryCreateOnAdapter(chosenPtr, out device, out commandQueue, out fence, out fenceEvent))
            {
                Logger.TryGet(LogEventLevel.Error, "Direct3D12")
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

            if (TryCreateOnAdapter(adapterPtr, out device, out commandQueue, out fence, out fenceEvent))
            {
                adapter = adapterPtr;
                return true;
            }

            Marshal.Release(adapterPtr);
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
    public IntPtr CreateCommandAllocator()
    {
        lock (this)
        {
            ThrowIfDisposedOrLost();
            Marshal.ThrowExceptionForHR(D3D12Interop.CreateCommandAllocator(
                _device,
                D3D12Interop.D3D12_COMMAND_LIST_TYPE_DIRECT,
                in D3D12Interop.IID_ID3D12CommandAllocator,
                out var allocator));
            return allocator;
        }
    }

    /// <summary>
    /// Creates a graphics command list bound to <paramref name="allocator"/> and closes it
    /// so the first use can <c>Reset</c> like a recycled list.
    /// </summary>
    public IntPtr CreateClosedCommandList(IntPtr allocator)
    {
        lock (this)
        {
            ThrowIfDisposedOrLost();
            Marshal.ThrowExceptionForHR(D3D12Interop.CreateCommandList(
                _device,
                0,
                D3D12Interop.D3D12_COMMAND_LIST_TYPE_DIRECT,
                allocator,
                IntPtr.Zero,
                in D3D12Interop.IID_ID3D12GraphicsCommandList,
                out var list));
            var hr = D3D12Interop.CloseCommandList(list);
            if (hr != 0)
            {
                D3D12Interop.Release(list);
                Marshal.ThrowExceptionForHR(hr);
            }

            return list;
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
        IntPtr commandAllocator,
        IntPtr commandList,
        IntPtr resource,
        uint stateBefore,
        uint stateAfter,
        ulong allocatorFenceValue,
        bool resetAllocator = true)
    {
        lock (this)
        {
            ThrowIfDisposedOrLost();

            if (resetAllocator)
            {
                // Allocator memory must remain intact until its previous submission completes.
                WaitForFence(allocatorFenceValue);
                Marshal.ThrowExceptionForHR(D3D12Interop.ResetCommandAllocator(commandAllocator));
            }

            Marshal.ThrowExceptionForHR(D3D12Interop.ResetCommandList(commandList, commandAllocator, IntPtr.Zero));

            var barrier = D3D12Interop.D3D12_RESOURCE_BARRIER.CreateTransition(resource, stateBefore, stateAfter);
            D3D12Interop.ResourceBarrier(commandList, 1, in barrier);
            Marshal.ThrowExceptionForHR(D3D12Interop.CloseCommandList(commandList));

            IntPtr pList = commandList;
            D3D12Interop.ExecuteCommandLists(_commandQueue, 1, &pList);
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
        lock (this)
        {
            ThrowIfDisposedOrLost();
            var heap = D3D12Interop.D3D12_HEAP_PROPERTIES.Create(D3D12Interop.D3D12_HEAP_TYPE_DEFAULT);
            var desc = D3D12Interop.D3D12_RESOURCE_DESC.Texture2D(format, width, height, resourceFlags);
            Marshal.ThrowExceptionForHR(D3D12Interop.CreateCommittedResource(
                _device,
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
        lock (this)
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
        BeforeSwapchainBufferRelease?.Invoke();
        WaitForGpu();
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

    // The wait runs under the graphics lock: the fence event is shared and the lock also
    // protects the device from being released while it is in use.
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
            if (UnmanagedMethods.WaitForSingleObject(_fenceEvent, UnmanagedMethods.INFINITE) != 0)
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
    /// Caller must hold <c>lock (this)</c>.
    /// </summary>
    private void EnsureD3D11On12Unlocked()
    {
        ThrowIfDisposedOrLost();
        if (_d3d11On12Device != IntPtr.Zero && _d3d11On12Generation == _deviceGeneration)
            return;

        ReleaseD3D11On12Unlocked();

        IntPtr queue = _commandQueue;
        var hr = D3D12Interop.D3D11On12CreateDevice(
            _device,
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

        IntPtr on12 = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d11Device, in D3D12Interop.IID_ID3D11On12Device, out on12));

            _d3d11Device = d3d11Device;
            _d3d11Context = d3d11Context;
            _d3d11On12Device = on12;
            _d3d11On12Generation = _deviceGeneration;
            d3d11Device = IntPtr.Zero;
            d3d11Context = IntPtr.Zero;
            on12 = IntPtr.Zero;
        }
        catch
        {
            if (on12 != IntPtr.Zero)
                D3D12Interop.Release(on12);
            if (d3d11Context != IntPtr.Zero)
                D3D12Interop.Release(d3d11Context);
            if (d3d11Device != IntPtr.Zero)
                D3D12Interop.Release(d3d11Device);
            throw;
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
        lock (this)
        {
            if (texture == IntPtr.Zero)
                throw new ArgumentNullException(nameof(texture));
            EnsureD3D11On12Unlocked();
            if (_d3d11On12Device2 == IntPtr.Zero)
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
                    _d3d11On12Device, in D3D12Interop.IID_ID3D11On12Device2, out _d3d11On12Device2));

            Marshal.ThrowExceptionForHR(D3D12Interop.UnwrapUnderlyingResource(
                _d3d11On12Device2, texture, _commandQueue, out var resource));
            // Unwrap may enqueue transitions in the translation layer. Submit these before
            // native D3D12 work on the shared queue, including any waits it depends on.
            D3D12Interop.Flush(_d3d11Context);
            return resource;
        }
    }

    internal void ReturnCompositionTexture(IntPtr texture, ulong fenceValue)
    {
        lock (this)
        {
            ThrowIfDisposedOrLost();
            var fence = _fence;
            Marshal.ThrowExceptionForHR(D3D12Interop.ReturnUnderlyingResource(
                _d3d11On12Device2, texture, fenceValue == 0 ? 0u : 1u, &fenceValue, &fence));
            D3D12Interop.Flush(_d3d11Context);
        }
    }

    /// <summary>
    /// Wraps a D3D12 offscreen RT as <c>ID3D11Texture2D</c> for D3D11 copies into BeginDraw
    /// textures. In/Out state stay RENDER_TARGET so Skia can reuse the resource next frame.
    /// Returned pointer owns a reference the caller must Release.
    /// </summary>
    internal IntPtr CreateWrappedOffscreenTexture(IntPtr d3d12Resource)
    {
        lock (this)
        {
            EnsureD3D11On12Unlocked();
            var flags = new D3D12Interop.D3D11_RESOURCE_FLAGS
            {
                BindFlags = D3D12Interop.D3D11_BIND_RENDER_TARGET
            };
            var hr = D3D12Interop.CreateWrappedResource(
                _d3d11On12Device,
                d3d12Resource,
                in flags,
                D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET,
                D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET,
                in D3D12Interop.IID_ID3D11Texture2D,
                out var wrapped);
            if (hr != 0 || wrapped == IntPtr.Zero)
            {
                Marshal.ThrowExceptionForHR(hr != 0 ? hr : unchecked((int)0x80070057)); // E_INVALIDARG
            }
            return wrapped;
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
        lock (this)
        {
            ThrowIfDisposedOrLost();
            if (wrappedOffscreen11 == IntPtr.Zero)
                throw new ArgumentNullException(nameof(wrappedOffscreen11));
            if (compositionTexture11 == IntPtr.Zero)
                throw new ArgumentNullException(nameof(compositionTexture11));
            EnsureD3D11On12Unlocked();

            IntPtr resource = wrappedOffscreen11;
            D3D12Interop.AcquireWrappedResources(_d3d11On12Device, &resource, 1);
            try
            {
                D3D12Interop.CopySubresourceRegion(
                    _d3d11Context,
                    compositionTexture11,
                    0,
                    (uint)dstOffset.X,
                    (uint)dstOffset.Y,
                    0,
                    wrappedOffscreen11,
                    0,
                    IntPtr.Zero);
            }
            finally
            {
                D3D12Interop.ReleaseWrappedResources(_d3d11On12Device, &resource, 1);
                // Required before D3D12 reuses the resource (next Skia frame).
                D3D12Interop.Flush(_d3d11Context);
            }
        }
    }

    internal void MarkLost() => _isLost = true;

    public IPlatformGraphicsContext CreateContext() => GetSharedContext();

    /// <summary>
    /// Returns the shared context, recreating the D3D12 device when the previous one was lost.
    /// A lost context is replaced by a new instance bound to the new <see cref="DeviceGeneration"/>.
    /// Throws <see cref="PlatformGraphicsContextLostException"/> if recreation fails so
    /// <c>PlatformRenderInterfaceContextManager</c> cannot keep retrying the same dead context.
    /// </summary>
    public IPlatformGraphicsContext GetSharedContext()
    {
        lock (this)
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
    /// Caller must hold <c>lock (this)</c>.
    /// </summary>
    private bool TryRecreateDeviceUnlocked()
    {
        Logger.TryGet(LogEventLevel.Warning, "Direct3D12")
            ?.Log(this, "Direct3D 12 device lost; attempting to recreate.");

        ReleaseNativeResources(drainGpu: false);

        try
        {
            if (!TryCreateNativeResources(out var adapter, out var device, out var queue, out var fence, out var fenceEvent))
            {
                _isLost = true;
                Logger.TryGet(LogEventLevel.Error, "Direct3D12")
                    ?.Log(this, "Failed to recreate Direct3D 12 device after device lost.");
                return false;
            }

            _adapter = adapter;
            _device = device;
            _commandQueue = queue;
            _fence = fence;
            _fenceEvent = fenceEvent;
            _fenceValue = 0;
            _isLost = false;
            _deviceGeneration++;
            _drawingSurfaceCapabilities = _drawingSurfaceProbe?.Invoke(this);

            Logger.TryGet(LogEventLevel.Information, "Direct3D12")
                ?.Log(this, "Direct3D 12 device recreated (generation {Generation}).", _deviceGeneration);
            return true;
        }
        catch (Exception ex)
        {
            _isLost = true;
            Logger.TryGet(LogEventLevel.Error, "Direct3D12")
                ?.Log(this, "Exception while recreating Direct3D 12 device: {0}", ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (this)
        {
            if (_disposed)
                return;
            _disposed = true;
            _context = null;
            ReleaseNativeResources(drainGpu: true);
        }
    }

    private void ReleaseNativeResources(bool drainGpu)
    {
        _drawingSurfaceCapabilities = null;
        try
        {
            if (drainGpu && _device != IntPtr.Zero && _commandQueue != IntPtr.Zero && _fence != IntPtr.Zero)
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

    private static void ReleaseAndClear(ref IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return;
        D3D12Interop.Release(ptr);
        ptr = IntPtr.Zero;
    }
}
