using System;
using Avalonia.IntegrationTests.Win32.Infrastructure;
using Avalonia.Media;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Win32;
using Avalonia.Win32.Direct3D12;
using SkiaSharp;
using Xunit;

namespace Avalonia.IntegrationTests.Win32;

public class D3D12PresentationTests
{
    [Theory]
    [InlineData(Win32CompositionMode.RedirectionSurface, false, false)]
    [InlineData(Win32CompositionMode.WinUIComposition, false, false)]
    [InlineData(Win32CompositionMode.DirectComposition, false, false)]
    [InlineData(Win32CompositionMode.WinUIComposition, true, false)]
    [InlineData(Win32CompositionMode.DirectComposition, true, false)]
    [InlineData(Win32CompositionMode.WinUIComposition, false, true)]
    [InlineData(Win32CompositionMode.DirectComposition, false, true)]
    public void Presentation_Leases_Draw_Without_Exposing_The_Surface(
        Win32CompositionMode mode, bool swapchain, bool forceCopy)
    {
        using var scope = AvaloniaLocator.EnterScope();
        var options = new Win32PlatformOptions { CompositionMode = [mode], UseCompositionSwapChain = swapchain };
        AvaloniaLocator.CurrentMutable.Bind<Win32PlatformOptions>().ToConstant(options);
        using var graphics = D3D12PlatformGraphics.TryCreate();
        Assert.SkipWhen(graphics is null, "Direct3D 12 is not available.");
        Assert.NotNull(graphics);
        AvaloniaLocator.CurrentMutable.Bind<IPlatformGraphics>().ToConstant(graphics);
        using var debug = new D3D12DebugLayer(graphics.DeviceHandle);
        SkiaPlatform.Initialize();
        Assert.True(Win32GlManager.TryCreateComposition(options, true, out var connection));
        try
        {
            if (!swapchain && connection is ID3D12DrawingSurfaceFactory factory)
                graphics.InitializeDrawingSurface(g =>
                {
                    var capabilities = factory.ProbeDrawingSurface(g);
                    return forceCopy ? new(capabilities.Format, false) : capabilities;
                });

            using var window = new TestWindow();
            var surface = (connection as IWindowsSurfaceFactory ?? graphics).CreateSurface(window);
            try
            {
                var context = graphics.GetSharedContext();
                using var backend = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>()
                    .CreateBackendContext(context);
                using var target = backend.CreateRenderTarget([surface]);
                for (var frame = 0; frame < 12; frame++)
                {
                    window.Size = new PixelSize(64 + frame / 3, 64 + frame / 3);
                    using (context.EnsureCurrent())
                    using (var drawing = target.CreateDrawingContext(new(window.Size, 1,
                        frame < 6 ? CompositionTransparencyLevel.None : CompositionTransparencyLevel.Transparent), out _))
                    {
                        drawing.Clear(Colors.Blue);
                        using var layer = drawing.CreateLayer(window.Size);
                        using (var layerDrawing = layer.CreateDrawingContext())
                        {
                            layerDrawing.Clear(Colors.Red);
                            using var lease = layerDrawing.GetFeature<ISkiaSharpApiLeaseFeature>()!.Lease();
                            Assert.NotNull(lease.SkSurface);
                            using var snapshot = lease.SkSurface.Snapshot();
                            using var pixels = SKBitmap.FromImage(snapshot);
                            Assert.Equal(SKColors.Red, pixels.GetPixel(0, 0));
                        }
                        layer.Blit(drawing);
                        using var targetLease = drawing.GetFeature<ISkiaSharpApiLeaseFeature>()!.Lease();
                        Assert.Null(targetLease.SkSurface);
                        Assert.Null(targetLease.SkCanvas.Surface);
                        Assert.NotNull(targetLease.GrContext);
                        using var paint = new SKPaint { Color = SKColors.Green };
                        targetLease.SkCanvas.DrawRect(SKRect.Create(2, 3, 8, 9), paint);
                    }
                }
                graphics.WaitForGpu();
            }
            finally
            {
                (surface as IDisposable)?.Dispose();
            }
            graphics.WaitForGpu();
            debug.AssertNoErrors();
        }
        finally
        {
            connection?.Stop();
        }
    }

    private sealed class TestWindow : EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo, IDisposable
    {
        private readonly SimpleWindow _window = new(null);
        public IntPtr Handle => _window.Handle;
        public PixelSize Size { get; set; } = new(64, 64);
        public double Scaling => 1;
        public void Dispose() => _window.Dispose();
    }
}
