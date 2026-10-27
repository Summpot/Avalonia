using System;
using Avalonia.Direct3D12;
using Avalonia.Platform;
using SkiaSharp;

namespace Avalonia.Skia.Direct3D12;

internal class D3D12SkiaRenderTarget : ISkiaGpuRenderTarget
{
    private readonly D3D12SkiaGpu _gpu;
    private ID3D12PlatformSurfaceRenderTarget? _target;

    public D3D12SkiaRenderTarget(D3D12SkiaGpu gpu, ID3D12PlatformSurfaceRenderTarget target)
    {
        _gpu = gpu;
        _target = target;
    }

    public void Dispose()
    {
        _target?.Dispose();
        _target = null;
    }

    public ISkiaGpuRenderSession BeginRenderingSession(IRenderTarget.RenderTargetSceneInfo sceneInfo)
    {
        ID3D12PlatformSurfaceRenderingSession? session = null;
        GRBackendRenderTarget? backendTarget = null;
        SKSurface? surface = null;
        var success = false;
        try
        {
            session = (_target ?? throw new ObjectDisposedException(nameof(D3D12SkiaRenderTarget))).BeginRendering(sceneInfo);

            // The platform session owns transitions into and out of RENDER_TARGET.
            _gpu.GrContext.ResetContext();

            var textureInfo = new GRD3DTextureResourceInfo
            {
                Resource = session.Resource,
                ResourceState = session.ResourceState,
                Format = session.Format,
                SampleCount = 1,
                LevelCount = 1,
                SampleQualityPattern = 0,
                Protected = false
            };

            backendTarget = new GRBackendRenderTarget(session.ResourceSize.Width, session.ResourceSize.Height, textureInfo);

            var colorType = session.Format switch
            {
                D3D12DxgiFormats.DXGI_FORMAT_R8G8B8A8_UNORM => SKColorType.Rgba8888,
                D3D12DxgiFormats.DXGI_FORMAT_B8G8R8A8_UNORM => SKColorType.Bgra8888,
                _ => throw new NotSupportedException($"Unsupported D3D12 render target format: {session.Format}.")
            };
            using var colorSpace = SKColorSpace.CreateSrgb();
            surface = SKSurface.Create(_gpu.GrContext, backendTarget,
                session.IsYFlipped ? GRSurfaceOrigin.BottomLeft : GRSurfaceOrigin.TopLeft,
                colorType, colorSpace);

            if (surface is null)
                throw new InvalidOperationException("Failed to create SKSurface from D3D12 backend render target.");

            // Composition can place the update rectangle inside a larger atlas texture.
            // Keep the clip in device coordinates so Clear and transform changes respect it.
            surface.Canvas.ClipRect(SKRect.Create(session.Offset.X, session.Offset.Y, session.Size.Width, session.Size.Height));

            var result = new D3D12SkiaRenderSession(_gpu, surface, session, backendTarget);
            success = true;
            return result;
        }
        finally
        {
            if (!success)
            {
                surface?.Dispose();
                backendTarget?.Dispose();
                session?.Dispose();
            }
        }
    }

    public PlatformRenderTargetState State => _target?.State ?? PlatformRenderTargetState.Disposed;
}

internal class D3D12SkiaRenderSession : ISkiaGpuRenderSession, ISkiaGpuRenderSessionWithOffset
{
    private readonly D3D12SkiaGpu _gpu;
    private SKSurface? _surface;
    private ID3D12PlatformSurfaceRenderingSession? _session;
    private GRBackendRenderTarget? _backendTarget;

    public D3D12SkiaRenderSession(
        D3D12SkiaGpu gpu,
        SKSurface surface,
        ID3D12PlatformSurfaceRenderingSession session,
        GRBackendRenderTarget backendTarget)
    {
        _gpu = gpu;
        _surface = surface;
        _session = session;
        _backendTarget = backendTarget;
    }

    public void Dispose()
    {
        try
        {
            // Platform session Dispose owns the end-of-frame barriers, so no Present-access flush here.
            _gpu.GrContext.Flush();
        }
        finally
        {
            _surface?.Dispose();
            _surface = null;
            _backendTarget?.Dispose();
            _backendTarget = null;
            _session?.Dispose();
            _session = null;
        }
    }

    public GRContext GrContext => _gpu.GrContext;
    public SKSurface SkSurface => _surface!;
    public double ScaleFactor => _session?.Scaling ?? 1.0;
    public PixelPoint Offset => _session?.Offset ?? default;
    public GRSurfaceOrigin SurfaceOrigin =>
        (_session?.IsYFlipped ?? false) ? GRSurfaceOrigin.BottomLeft : GRSurfaceOrigin.TopLeft;
}
