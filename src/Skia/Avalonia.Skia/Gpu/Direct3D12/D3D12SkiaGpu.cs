using System;
using System.Collections.Generic;
using Avalonia.Direct3D12;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using SkiaSharp;

namespace Avalonia.Skia.Direct3D12;

internal class D3D12SkiaGpu : ISkiaGpu
{
    private GRContext? _context;
    private readonly ID3D12PlatformGraphicsContext _device;
    private readonly Action _beforeSwapchainBufferRelease;

    internal GRContext GrContext => _context ?? throw new ObjectDisposedException(nameof(D3D12SkiaGpu));

    public D3D12SkiaGpu(ID3D12PlatformGraphicsContext device, long? maxResourceBytes, bool? useStencilBuffers = null)
    {
        _device = device;
        var avoidStencilBuffers = SkiaOptions.ShouldAvoidStencilBuffers(useStencilBuffers);
        var backendContext = new GRD3DBackendContext
        {
            Adapter = device.Adapter,
            Device = device.Device,
            Queue = device.CommandQueue,
            ProtectedContext = false
        };

        _context = GRContext.CreateDirect3D(
                       backendContext,
                       new GRContextOptions { AvoidStencilBuffers = avoidStencilBuffers })
                   ?? throw new InvalidOperationException("Unable to create GRContext from Direct3D 12 device.");

        if (maxResourceBytes.HasValue)
            _context.SetResourceCacheLimit(maxResourceBytes.Value);

        _beforeSwapchainBufferRelease = ReleaseSwapchainBufferReferences;
        _device.BeforeSwapchainBufferRelease += _beforeSwapchainBufferRelease;
    }

    /// <summary>
    /// Flushes pending Skia operations synchronously and purges unlocked resources so that
    /// retained <c>ID3D12Resource</c> COM references are released before swapchain mutation.
    /// </summary>
    private void ReleaseSwapchainBufferReferences()
    {
        if (_context is null || _device.IsLost)
            return;

        _context.Flush(true, true);
        _context.PurgeUnlockedResources(false);
    }

    public void Dispose()
    {
        if (_context is null)
            return;

        _device.BeforeSwapchainBufferRelease -= _beforeSwapchainBufferRelease;

        if (_device.IsLost)
        {
            _context.AbandonContext();
            _context.Dispose();
        }
        else
        {
            using (_device.EnsureCurrent())
            {
                _context.AbandonContext(true);
                _context.Dispose();
            }
        }

        _context = null;
        _device.Dispose();
    }

    public object? TryGetFeature(Type featureType) => null;

    public bool IsLost => _device.IsLost;
    public IDisposable EnsureCurrent() => _device.EnsureCurrent();
    public IPlatformGraphicsContext? PlatformGraphicsContext => _device;

    public IScopedResource<GRContext> TryGetGrContext() =>
        ScopedResource<GRContext>.Create(GrContext, EnsureCurrent().Dispose);

    public ISkiaGpuRenderTarget? TryCreateRenderTarget(IEnumerable<IPlatformRenderSurface> surfaces)
    {
        foreach (var surface in surfaces)
        {
            if (surface is ID3D12PlatformSurface d3d12Surface)
            {
                var target = d3d12Surface.CreateD3D12RenderTarget(_device);
                return new D3D12SkiaRenderTarget(this, target);
            }
        }

        return null;
    }

    public bool IsReadyToCreateRenderTarget(IEnumerable<IPlatformRenderSurface> surfaces)
    {
        foreach (var surface in surfaces)
        {
            if (surface is ID3D12PlatformSurface)
            {
                return surface.IsReady;
            }
        }

        return false;
    }

    public ISkiaSurface? TryCreateSurface(PixelSize size, ISkiaGpuRenderSession? session) => null;
}
