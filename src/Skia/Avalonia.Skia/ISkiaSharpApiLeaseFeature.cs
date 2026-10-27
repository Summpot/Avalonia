using System;
using Avalonia.Metadata;
using Avalonia.Platform;
using SkiaSharp;

namespace Avalonia.Skia;

[Unstable]
public interface ISkiaSharpApiLeaseFeature
{
    public ISkiaSharpApiLease Lease();
}

[Unstable]
public interface ISkiaSharpApiLease : IDisposable
{
    /// <summary>Canvas for drawing during the lease. Use <see cref="GrContext"/> for GPU context access.</summary>
    SKCanvas SkCanvas { get; }
    GRContext? GrContext { get; }
    /// <summary>
    /// Backing surface, when available. D3D12 presentation targets provide a drawing-only canvas
    /// and no surface; use an offscreen layer for snapshots or pixel readback.
    /// </summary>
    SKSurface? SkSurface { get; }
    double CurrentOpacity { get; }
    ISkiaSharpPlatformGraphicsApiLease? TryLeasePlatformGraphicsApi();
}

[Unstable]
public interface ISkiaSharpPlatformGraphicsApiLease : IDisposable
{
    IPlatformGraphicsContext Context { get; }
}
