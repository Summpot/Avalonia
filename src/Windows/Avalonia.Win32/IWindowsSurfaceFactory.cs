using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.OpenGL.Egl;

namespace Avalonia.Win32;

internal interface IWindowsSurfaceFactory
{
    bool RequiresNoRedirectionBitmap { get; }

    IPlatformRenderSurface CreateSurface(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info);
}

/// <summary>
/// A composition connection whose render thread runs idle until it is published,
/// so initialization can be validated before the process-wide services are replaced.
/// </summary>
internal interface IWindowsCompositionConnection : IWindowsSurfaceFactory
{
    /// <summary>Registers the connection as the surface factory and render loop.</summary>
    void Publish();

    /// <summary>Waits for the render thread to exit and release its resources. Valid only before <see cref="Publish"/>.</summary>
    void Stop();
}
