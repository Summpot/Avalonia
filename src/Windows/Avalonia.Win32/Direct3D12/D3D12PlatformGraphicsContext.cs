using System;
using Avalonia.Direct3D12;
using Avalonia.Platform;

namespace Avalonia.Win32.Direct3D12;

internal class D3D12PlatformGraphicsContext : ID3D12PlatformGraphicsContext
{
    private readonly D3D12PlatformGraphics _parent;
    private readonly int _deviceGeneration;

    public IntPtr AdapterHandle => _parent.AdapterHandle;
    public IntPtr DeviceHandle => _parent.DeviceHandle;
    public IntPtr CommandQueueHandle => _parent.CommandQueueHandle;

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

    // A context remains lost when its device generation has been replaced.
    public bool IsLost =>
        _deviceGeneration != _parent.DeviceGeneration || _parent.IsLost;

    public IDisposable EnsureCurrent() => _parent.EnsureCurrent(_deviceGeneration);

    public object? TryGetFeature(Type featureType)
    {
        if (featureType == typeof(ID3D12PlatformGraphicsExternalObjects))
            return _parent.ExternalObjects;
        return null;
    }

    public void Dispose()
    {
        // The shared device and command queue are owned by D3D12PlatformGraphics.
    }
}
