using System;
using System.Collections.Generic;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering;
using Avalonia.Win32;
using Avalonia.Win32.DComposition;
using Avalonia.Win32.WinRT.Composition;
using Xunit;

namespace Avalonia.IntegrationTests.Win32;

public class Win32GlManagerCompositionTests
{
    [Fact]
    public void RedirectionSurface_Needs_No_Composition_Connection()
    {
        using var _ = EnterPresentationScope();
        var options = new Win32PlatformOptions { CompositionMode = [Win32CompositionMode.RedirectionSurface] };

        var result = Win32GlManager.TryCreateComposition(options, forDirect3D12: true, out var connection);

        Assert.True(result);
        Assert.Null(connection);
        Assert.Null(AvaloniaLocator.Current.GetService<IWindowsSurfaceFactory>());
        Assert.Null(AvaloniaLocator.Current.GetService<IRenderLoop>());
    }

    [Fact]
    public void LowLatencyDxgiSwapChain_Is_Not_Used_By_Direct3D12()
    {
        using var _ = EnterPresentationScope();
        var options = new Win32PlatformOptions { CompositionMode = [Win32CompositionMode.LowLatencyDxgiSwapChain] };

        var result = Win32GlManager.TryCreateComposition(options, forDirect3D12: true, out var connection);

        Assert.False(result);
        Assert.Null(connection);
        Assert.Null(AvaloniaLocator.Current.GetService<IWindowsSurfaceFactory>());
        Assert.Null(AvaloniaLocator.Current.GetService<IRenderLoop>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Empty_CompositionMode_Throws(bool forDirect3D12)
    {
        using var scope = EnterPresentationScope();
        var options = new Win32PlatformOptions { CompositionMode = [] };

        Assert.Throws<InvalidOperationException>(() =>
            Win32GlManager.TryCreateComposition(options, forDirect3D12, out _));
    }

    [Theory]
    [InlineData(Win32CompositionMode.DirectComposition)]
    [InlineData(Win32CompositionMode.RedirectionSurface)]
    public void Failed_Probe_Tries_The_Next_Composition_Mode(Win32CompositionMode fallback)
    {
        using var scope = EnterPresentationScope();
        Assert.SkipWhen(!WinUiCompositorConnection.IsSupported(), "WinUI composition is unavailable.");
        var options = new Win32PlatformOptions
        {
            CompositionMode = [Win32CompositionMode.WinUIComposition, fallback]
        };
        var attempts = new List<IWindowsCompositionConnection>();
        IWindowsCompositionConnection? selected = null;
        try
        {
            var result = Win32GlManager.TryCreateComposition(options, true, out selected, candidate =>
            {
                attempts.Add(candidate);
                Assert.Null(AvaloniaLocator.Current.GetService<IWindowsSurfaceFactory>());
                Assert.Null(AvaloniaLocator.Current.GetService<IRenderLoop>());
                if (candidate is WinUiCompositorConnection)
                    throw new NotSupportedException("Drawing surfaces are unavailable.");
            });

            Assert.True(result);
            Assert.IsType<WinUiCompositorConnection>(attempts[0]);
            if (fallback == Win32CompositionMode.DirectComposition)
            {
                Assert.Equal(2, attempts.Count);
                Assert.IsType<DirectCompositionConnection>(selected);
            }
            else
            {
                Assert.Single(attempts);
                Assert.Null(selected);
            }
            Assert.Null(AvaloniaLocator.Current.GetService<IWindowsSurfaceFactory>());
            Assert.Null(AvaloniaLocator.Current.GetService<IRenderLoop>());
        }
        finally
        {
            selected?.Stop();
            foreach (var attempt in attempts)
                attempt.Stop();
        }
    }

    [Fact]
    public void Failed_Probes_Preserve_Existing_Services()
    {
        using var scope = EnterPresentationScope();
        Assert.SkipWhen(!WinUiCompositorConnection.IsSupported(), "WinUI composition is unavailable.");
        var existing = new TestConnection();
        var attempts = new List<IWindowsCompositionConnection>();
        try
        {
            existing.Publish();
            var renderLoop = AvaloniaLocator.Current.GetService<IRenderLoop>();
            var options = new Win32PlatformOptions
            {
                CompositionMode = [Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition]
            };
            var result = Win32GlManager.TryCreateComposition(options, true, out var selected, candidate =>
            {
                attempts.Add(candidate);
                throw new NotSupportedException("Drawing surfaces are unavailable.");
            });

            Assert.False(result);
            Assert.Null(selected);
            Assert.Equal(2, attempts.Count);
            Assert.Same(existing, AvaloniaLocator.Current.GetService<IWindowsSurfaceFactory>());
            Assert.Same(renderLoop, AvaloniaLocator.Current.GetService<IRenderLoop>());
        }
        finally
        {
            foreach (var attempt in attempts)
                attempt.Stop();
            existing.Stop();
        }
    }

    [Fact]
    public void Rendering_Fallback_Runs_After_All_Composition_Probes_Fail()
    {
        using var scope = EnterPresentationScope();
        Assert.SkipWhen(!WinUiCompositorConnection.IsSupported(), "WinUI composition is unavailable.");
        var next = new TestGraphics();
        var attempts = new List<Win32RenderingMode>();
        var connections = new List<IWindowsCompositionConnection>();
        var options = new Win32PlatformOptions
        {
            RenderingMode = [Win32RenderingMode.Direct3D12, Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            CompositionMode = [Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition]
        };
        try
        {
            var result = Win32GlManager.InitializeCore(options, mode =>
            {
                attempts.Add(mode);
                if (mode == Win32RenderingMode.Direct3D12)
                {
                    Assert.False(Win32GlManager.TryCreateComposition(options, true, out _, candidate =>
                    {
                        connections.Add(candidate);
                        throw new NotSupportedException();
                    }));
                    return null;
                }
                Assert.Equal(2, connections.Count);
                Assert.Null(AvaloniaLocator.Current.GetService<IWindowsSurfaceFactory>());
                Assert.Null(AvaloniaLocator.Current.GetService<IRenderLoop>());
                return next;
            });

            Assert.Same(next, result);
            Assert.Equal([Win32RenderingMode.Direct3D12, Win32RenderingMode.AngleEgl], attempts);
        }
        finally
        {
            foreach (var connection in connections)
                connection.Stop();
        }
    }

    [Fact]
    public void Composition_Services_Are_Published_Only_After_Validation()
    {
        using var scope = EnterPresentationScope();
        Assert.SkipWhen(!WinUiCompositorConnection.IsSupported(), "WinUI composition is unavailable.");
        var options = new Win32PlatformOptions { CompositionMode = [Win32CompositionMode.WinUIComposition] };
        var validated = false;
        Assert.True(Win32GlManager.TryCreateComposition(options, true, out var connection, _ =>
        {
            Assert.Null(AvaloniaLocator.Current.GetService<IWindowsSurfaceFactory>());
            Assert.Null(AvaloniaLocator.Current.GetService<IRenderLoop>());
            validated = true;
        }));
        Assert.NotNull(connection);
        try
        {
            Assert.True(validated);
            Assert.Null(AvaloniaLocator.Current.GetService<IWindowsSurfaceFactory>());
            connection.Publish();
            Assert.Same(connection, AvaloniaLocator.Current.GetService<IWindowsSurfaceFactory>());
            Assert.NotNull(AvaloniaLocator.Current.GetService<IRenderLoop>());
        }
        finally
        {
            connection.Stop();
        }
    }

    private static IDisposable EnterPresentationScope()
    {
        var scope = AvaloniaLocator.EnterScope();
        // Tests own these registrations without inheriting the initialized application's presentation services.
        AvaloniaLocator.CurrentMutable.Bind<IWindowsSurfaceFactory>().ToConstant<IWindowsSurfaceFactory>(null!);
        AvaloniaLocator.CurrentMutable.Bind<IRenderLoop>().ToConstant<IRenderLoop>(null!);
        return scope;
    }

    private sealed class TestGraphics : IPlatformGraphics
    {
        public bool UsesSharedContext => true;
        public IPlatformGraphicsContext CreateContext() => throw new NotSupportedException();
        public IPlatformGraphicsContext GetSharedContext() => throw new NotSupportedException();
    }

    private sealed class TestConnection : IWindowsCompositionConnection, IRenderTimer
    {
        public Action<TimeSpan>? Tick { get; set; }
        public bool RunsInBackground => true;
        public bool RequiresNoRedirectionBitmap => true;

        public void Publish()
        {
            AvaloniaLocator.CurrentMutable.Bind<IWindowsSurfaceFactory>().ToConstant(this);
            AvaloniaLocator.CurrentMutable.Bind<IRenderLoop>().ToConstant(RenderLoop.FromTimer(this));
        }

        public void Stop()
        {
        }

        public IPlatformRenderSurface CreateSurface(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info) =>
            throw new NotSupportedException();
    }
}
