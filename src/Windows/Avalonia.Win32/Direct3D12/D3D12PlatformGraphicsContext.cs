using System;
using Avalonia.Direct3D12;
using Avalonia.Platform;

namespace Avalonia.Win32.Direct3D12;

internal class D3D12PlatformGraphicsContext : ID3D12PlatformGraphicsContext
{
    private readonly D3D12PlatformGraphics _parent;
    public IntPtr Adapter => _parent.AdapterHandle;
    public IntPtr Device => _parent.DeviceHandle;
    public IntPtr CommandQueue => _parent.CommandQueueHandle;

    public D3D12PlatformGraphicsContext(D3D12PlatformGraphics parent)
    {
        _parent = parent;
    }

    public bool IsLost => _parent.IsLost;

    public IDisposable EnsureCurrent() => Disposable.Empty;

    public object? TryGetFeature(Type featureType) => null;

    /// <summary>
    /// Shared-context ownership: this context does not own the underlying
    /// <see cref="D3D12PlatformGraphics"/> device/queue. Disposing it is a no-op;
    /// release the device via <see cref="D3D12PlatformGraphics.Dispose"/>.
    /// </summary>
    public void Dispose()
    {
    }

    private class Disposable : IDisposable
    {
        public static readonly Disposable Empty = new();
        public void Dispose() { }
    }
}
