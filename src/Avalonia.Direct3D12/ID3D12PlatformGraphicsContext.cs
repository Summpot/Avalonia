using System;
using Avalonia.Metadata;
using Avalonia.Platform;

namespace Avalonia.Direct3D12;

/// <summary>
/// A D3D12 device and direct command queue shared with the renderer. Native handles are borrowed
/// and remain valid for this context's device generation. Serialize their use with
/// <see cref="IPlatformGraphicsContext.EnsureCurrent"/> and stop using them when the context is lost.
/// </summary>
[PrivateApi]
public interface ID3D12PlatformGraphicsContext : IPlatformGraphicsContext
{
    /// <summary>Borrowed IDXGIAdapter pointer for matching external resources to this device.</summary>
    IntPtr AdapterHandle { get; }
    /// <summary>Borrowed ID3D12Device pointer.</summary>
    IntPtr DeviceHandle { get; }
    /// <summary>Borrowed ID3D12CommandQueue pointer used for rendering and external-object synchronization.</summary>
    IntPtr CommandQueueHandle { get; }

    /// <summary>
    /// Invoked before swapchain buffers are released. Subscribers must submit pending work
    /// and release references to those buffers.
    /// </summary>
    event Action? BeforeSwapchainBufferRelease;
}
