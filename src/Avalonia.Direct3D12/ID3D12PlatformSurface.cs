using System;
using Avalonia.Metadata;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;

namespace Avalonia.Direct3D12;

/// <summary>
/// Platform surface that can create a D3D12 render target for a graphics context.
/// The surface does not own the context's device/queue; ownership of the returned
/// <see cref="ID3D12PlatformSurfaceRenderTarget"/> transfers to the caller.
/// Targets are bound to the device generation active at creation and report
/// <see cref="PlatformRenderTargetState.Corrupted"/> after device-lost recovery.
/// </summary>
[PrivateApi]
public interface ID3D12PlatformSurface : IPlatformRenderSurface
{
    /// <summary>
    /// Creates a render target using <paramref name="context"/>'s device and command queue.
    /// The caller must <see cref="IDisposable.Dispose"/> the returned target.
    /// </summary>
    ID3D12PlatformSurfaceRenderTarget CreateD3D12RenderTarget(ID3D12PlatformGraphicsContext context);
}

/// <summary>
/// A D3D12 render target (typically a DXGI flip-model swapchain).
/// <see cref="IPlatformRenderSurfaceRenderTarget.State"/> is
/// <see cref="PlatformRenderTargetState.Ready"/>,
/// <see cref="PlatformRenderTargetState.Corrupted"/> (device lost or generation mismatch),
/// or <see cref="PlatformRenderTargetState.Disposed"/>.
/// </summary>
[PrivateApi]
public interface ID3D12PlatformSurfaceRenderTarget : IDisposable, IPlatformRenderSurfaceRenderTarget
{
    /// <summary>
    /// Begins a frame. The caller must submit draw commands against the session resource
    /// before disposing it. Disposing ends the frame and runs the target's end-of-frame work.
    /// </summary>
    ID3D12PlatformSurfaceRenderingSession BeginRendering(IRenderTarget.RenderTargetSceneInfo sceneInfo);
}

/// <summary>
/// Active frame against one D3D12 resource (swapchain buffer, composition texture, or offscreen RT).
/// <see cref="Resource"/> is a borrowed <c>ID3D12Resource*</c>: valid only until this session is
/// disposed, on the render thread that owns the shared context / <c>GRContext</c>.
/// Do not retain or AddRef it, or use it from another thread.
/// The resource is in <see cref="ResourceState"/> when the session begins and must be in that state
/// again when rendering is finished. Disposing the session completes the frame and presents it.
/// </summary>
[PrivateApi]
public interface ID3D12PlatformSurfaceRenderingSession : IDisposable
{
    /// <summary>Borrowed native <c>ID3D12Resource*</c> for this frame (see type remarks).</summary>
    IntPtr Resource { get; }

    /// <summary>D3D12 resource state of <see cref="Resource"/> while it is being rendered to.</summary>
    uint ResourceState { get; }

    /// <summary>DXGI format of <see cref="Resource"/> (e.g. <c>DXGI_FORMAT_B8G8R8A8_UNORM</c>).</summary>
    uint Format { get; }

    /// <summary>Size in pixels of the drawable area.</summary>
    PixelSize Size { get; }

    /// <summary>Full dimensions of <see cref="Resource"/>, including any composition atlas padding.</summary>
    PixelSize ResourceSize { get; }

    /// <summary>Origin in pixels of the drawable <see cref="Size"/> rectangle within <see cref="Resource"/>.</summary>
    PixelPoint Offset { get; }
    double Scaling { get; }
    bool IsYFlipped { get; }
}
