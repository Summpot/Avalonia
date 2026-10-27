using System;
using Avalonia.Metadata;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;

namespace Avalonia.Direct3D12;

[PrivateApi]
public interface ID3D12PlatformSurface : IPlatformRenderSurface
{
    ID3D12PlatformSurfaceRenderTarget CreateD3D12RenderTarget(ID3D12PlatformGraphicsContext context);
}

[PrivateApi]
public interface ID3D12PlatformSurfaceRenderTarget : IDisposable, IPlatformRenderSurfaceRenderTarget
{
    ID3D12PlatformSurfaceRenderingSession BeginRendering();
}

[PrivateApi]
public interface ID3D12PlatformSurfaceRenderingSession : IDisposable
{
    IntPtr Resource { get; }
    uint ResourceState { get; }
    uint Format { get; }
    PixelSize Size { get; }
    double Scaling { get; }
    bool IsYFlipped { get; }
}
