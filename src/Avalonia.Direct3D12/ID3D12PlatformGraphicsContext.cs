using System;
using Avalonia.Metadata;
using Avalonia.Platform;

namespace Avalonia.Direct3D12;

[PrivateApi]
public interface ID3D12PlatformGraphicsContext : IPlatformGraphicsContext
{
    IntPtr Adapter { get; }
    IntPtr Device { get; }
    IntPtr CommandQueue { get; }

    /// <summary>
    /// Invoked by the platform immediately before releasing swapchain buffers for
    /// <c>ResizeBuffers</c> or swapchain recreation. Skia registers this so
    /// <c>GRContext</c> can drop retained <c>ID3D12Resource</c> wraps.
    /// </summary>
    event Action? BeforeSwapchainBufferRelease;
}
