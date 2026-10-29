using System;
using Avalonia.Direct3D12;
using Avalonia.Platform;
using Avalonia.Reactive;

namespace Avalonia.Win32.Direct3D12;

internal class D3D12PlatformGraphicsContext : ID3D12PlatformGraphicsContext
{
    private readonly D3D12PlatformGraphics _parent;
    private readonly int _deviceGeneration;

    public IntPtr Adapter => _parent.AdapterHandle;
    public IntPtr Device => _parent.DeviceHandle;
    public IntPtr CommandQueue => _parent.CommandQueueHandle;

    public event Action? BeforeSwapchainBufferRelease
    {
        add => _parent.BeforeSwapchainBufferRelease += value;
        remove => _parent.BeforeSwapchainBufferRelease -= value;
    }

    public D3D12PlatformGraphicsContext(D3D12PlatformGraphics parent)
    {
        _parent = parent;
        _deviceGeneration = parent.DeviceGeneration;
    }

    /// <summary>
    /// A recreated device is a new generation, so this context stays lost after device recovery
    /// and stale <c>GRContext</c> holders see the loss immediately.
    /// </summary>
    public bool IsLost =>
        _deviceGeneration != _parent.DeviceGeneration || _parent.IsLost;

    /// <summary>
    /// D3D12 has no make-current binding, so the shared context is always current.
    /// It is used from the render thread that owns it and any <c>GRContext</c> built from it.
    /// The device and queue helpers on <see cref="D3D12PlatformGraphics"/> are serialized by its lock,
    /// which does not cover <c>GRContext</c>; Skia callers must serialize those themselves.
    /// </summary>
    public IDisposable EnsureCurrent() => Disposable.Empty;

    public object? TryGetFeature(Type featureType) => null;

    /// <summary>
    /// Shared-context ownership: this context does not own the underlying
    /// <see cref="D3D12PlatformGraphics"/> device/queue. Disposing it is a no-op;
    /// release the device via <see cref="D3D12PlatformGraphics.Dispose"/>.
    /// After device-lost recovery a new context instance is issued; this one stays lost.
    /// </summary>
    public void Dispose()
    {
    }
}
