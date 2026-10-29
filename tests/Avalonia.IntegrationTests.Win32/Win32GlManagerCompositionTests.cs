using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering;
using Avalonia.Win32;
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

    [Fact]
    public void Failed_Initialization_Stops_The_Connection_Without_Replacing_Services()
    {
        using var scope = EnterPresentationScope();
        var existing = TestConnection.Create();
        var failed = TestConnection.Create();
        try
        {
            existing.Publish();
            var renderLoop = AvaloniaLocator.Current.GetService<IRenderLoop>();
            var expected = new InvalidOperationException("Drawing surfaces are unsupported.");

            var actual = Assert.Throws<InvalidOperationException>(() => Win32GlManager.InitializeComposition(
                failed, () => throw expected, () => Assert.Fail("An unsuccessful probe cannot publish HWND presentation.")));

            Assert.Same(expected, actual);
            Assert.True(failed.Released);
            Assert.False(failed.Owner.IsAlive);
            Assert.False(failed.Published);
            Assert.Same(existing, AvaloniaLocator.Current.GetService<IWindowsSurfaceFactory>());
            Assert.Same(renderLoop, AvaloniaLocator.Current.GetService<IRenderLoop>());
        }
        finally
        {
            failed.Stop();
            existing.Stop();
        }
    }

    [Fact]
    public void Rendering_Fallback_Runs_After_The_Failed_Connections_Thread_Exits()
    {
        using var scope = EnterPresentationScope();
        var failed = TestConnection.Create();
        var next = new TestGraphics();
        var attempts = new List<Win32RenderingMode>();
        var options = new Win32PlatformOptions
        {
            RenderingMode = [Win32RenderingMode.Direct3D12, Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]
        };
        try
        {
            var result = Win32GlManager.InitializeCore(options, mode =>
            {
                attempts.Add(mode);
                if (mode == Win32RenderingMode.Direct3D12)
                {
                    try
                    {
                        Win32GlManager.InitializeComposition(failed,
                            () => throw new NotSupportedException(), () => Assert.Fail("The probe must succeed."));
                    }
                    catch (NotSupportedException)
                    {
                        return null;
                    }
                }
                Assert.True(failed.Released);
                Assert.False(failed.Owner.IsAlive);
                Assert.Null(AvaloniaLocator.Current.GetService<IWindowsSurfaceFactory>());
                Assert.Null(AvaloniaLocator.Current.GetService<IRenderLoop>());
                return next;
            });

            Assert.Same(next, result);
            Assert.Equal([Win32RenderingMode.Direct3D12, Win32RenderingMode.AngleEgl], attempts);
        }
        finally
        {
            failed.Stop();
        }
    }

    [Fact]
    public void Composition_Services_Are_Published_Only_After_Initialization()
    {
        using var scope = EnterPresentationScope();
        var connection = TestConnection.Create();
        try
        {
            Win32GlManager.InitializeComposition(connection, () =>
            {
                Assert.Null(AvaloniaLocator.Current.GetService<IWindowsSurfaceFactory>());
                Assert.Null(AvaloniaLocator.Current.GetService<IRenderLoop>());
            }, () => Assert.Fail("A composition connection does not use HWND presentation."));

            Assert.True(connection.Published);
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
        private readonly WindowsCompositionThread _thread;
        public Thread Owner { get; } = Thread.CurrentThread;
        public bool Released { get; private set; }
        public bool Published { get; private set; }
        public Action<TimeSpan>? Tick { get; set; }
        public bool RunsInBackground => true;
        public bool RequiresNoRedirectionBitmap => true;

        private TestConnection(WindowsCompositionThread thread) => _thread = thread;

        public static TestConnection Create() => WindowsCompositionThread.Start(
            thread => new TestConnection(thread),
            connection => connection._thread.CancellationToken.WaitHandle.WaitOne(),
            connection => connection!.Released = true,
            _ => { });

        public void Publish()
        {
            AvaloniaLocator.CurrentMutable.Bind<IWindowsSurfaceFactory>().ToConstant(this);
            AvaloniaLocator.CurrentMutable.Bind<IRenderLoop>().ToConstant(RenderLoop.FromTimer(this));
            Published = true;
        }

        public void Stop() => _thread.Stop();

        public IPlatformRenderSurface CreateSurface(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info) =>
            throw new NotSupportedException();
    }
}
