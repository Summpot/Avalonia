using System;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// A session whose drawable area starts at a non-zero pixel offset inside the render target
    /// (e.g. a region of a composition atlas texture).
    /// </summary>
    internal interface ISkiaGpuRenderSessionWithOffset
    {
        /// <summary>
        /// Origin of the drawable area in render-target pixels, measured from the top-left corner.
        /// Positive X points right and positive Y points down; the offset is applied after DPI scaling.
        /// </summary>
        PixelPoint Offset { get; }
    }

    /// <summary>
    /// Custom render session for Skia render target.
    /// </summary>
    public interface ISkiaGpuRenderSession : IDisposable
    {
        /// <summary>
        /// GrContext used by this session.
        /// </summary>
        GRContext GrContext { get; }

        /// <summary>
        /// Canvas that will be used to render.
        /// </summary>
        SKSurface SkSurface { get; }

        /// <summary>
        /// Scaling factor.
        /// </summary>
        double ScaleFactor { get; }
        
        GRSurfaceOrigin SurfaceOrigin { get; }
    }
}
