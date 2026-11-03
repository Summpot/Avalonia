using System;
using System.Collections.Generic;
using Avalonia.Metadata;
using Avalonia.Platform;

namespace Avalonia.Direct3D12;

/// <summary>
/// Imports shared textures and timeline fences on the context's adapter.
/// Calls and disposal must be serialized with rendering on the context's render thread.
/// The caller retains ownership of each NT handle and may close it after import.
/// </summary>
[PrivateApi]
public interface ID3D12PlatformGraphicsExternalObjects
{
    IReadOnlyList<string> SupportedImageHandleTypes { get; }
    IReadOnlyList<string> SupportedSemaphoreTypes { get; }
    byte[]? DeviceLuid { get; }

    /// <summary>
    /// Imports a single-sample, single-mip 2D RGBA8 or BGRA8 render target with matching dimensions.
    /// Access starts in COMMON after the producer's fence is reached. D3D11 producers must flush
    /// their writes before signaling; keyed-mutex synchronization is not supported.
    /// D3D11 textures use SHARED | SHARED_NTHANDLE and a handle with read/write access.
    /// </summary>
    ID3D12PlatformGraphicsExternalImage ImportImage(string type, PlatformGraphicsExternalImageProperties properties, IntPtr handle);

    /// <summary>Imports a timeline fence. The caller retains ownership of the NT handle.</summary>
    ID3D12PlatformGraphicsExternalSemaphore ImportSemaphore(string type, IntPtr handle);

    /// <summary>Queues a GPU wait for the fence value without blocking the calling thread.</summary>
    void EnqueueWait(ID3D12PlatformGraphicsExternalSemaphore semaphore, ulong value);

    /// <summary>Signals the fence value on the GPU after all preceding work on the rendering queue.</summary>
    void EnqueueSignal(ID3D12PlatformGraphicsExternalSemaphore semaphore, ulong value);
}

[PrivateApi]
public interface ID3D12PlatformGraphicsExternalImage : IDisposable
{
    /// <summary>Borrowed ID3D12Resource pointer, valid until this image is disposed.</summary>
    IntPtr ResourceHandle { get; }
    D3D12DxgiFormat Format { get; }
    PixelSize Size { get; }
    int SampleCount { get; }
    int LevelCount { get; }

    /// <summary>
    /// Returns the resource to COMMON after submitting all reads in <paramref name="resourceState"/>.
    /// Call before enqueueing the release fence signal. Resources with simultaneous access
    /// decay to COMMON at submission boundaries and need no explicit transition.
    /// </summary>
    void TransitionToCommon(uint resourceState);
}

[PrivateApi]
public interface ID3D12PlatformGraphicsExternalSemaphore : IDisposable
{
    IntPtr FenceHandle { get; }
}
