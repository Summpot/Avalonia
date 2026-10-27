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
}
