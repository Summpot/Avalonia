using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Direct3D12;
using Avalonia.Media;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Win32;
using Avalonia.Win32.Direct3D12;
using Avalonia.Win32.DirectX;
using MicroCom.Runtime;
using Xunit;

namespace Avalonia.IntegrationTests.Win32;

public unsafe class D3D12LifecycleTests
{
    [Theory]
    [InlineData(Win32CompositionMode.RedirectionSurface, false)]
    [InlineData(Win32CompositionMode.WinUIComposition, false)]
    [InlineData(Win32CompositionMode.DirectComposition, false)]
    [InlineData(Win32CompositionMode.WinUIComposition, true)]
    [InlineData(Win32CompositionMode.DirectComposition, true)]
    public void Device_Removal_Recreates_The_Context_With_Live_Render_Targets(
        Win32CompositionMode mode, bool swapchain)
    {
        using var scope = AvaloniaLocator.EnterScope();
        var options = new Win32PlatformOptions
        {
            CompositionMode = [mode],
            UseCompositionSwapChain = swapchain,
            GraphicsAdapterSelectionCallback = _ => 0
        };
        AvaloniaLocator.CurrentMutable.Bind<Win32PlatformOptions>().ToConstant(options);
        using var graphics = CreateGraphics();
        AvaloniaLocator.CurrentMutable.Bind<IPlatformGraphics>().ToConstant(graphics);
        SkiaPlatform.Initialize();
        Assert.True(Win32GlManager.TryCreateComposition(options, true, out var connection));
        try
        {
            if (!swapchain && connection is ID3D12DrawingSurfaceFactory factory)
                graphics.InitializeDrawingSurface(factory.ProbeDrawingSurface);

            using var window = new TestWindow();
            var surface = (connection as IWindowsSurfaceFactory ?? graphics).CreateSurface(window);
            var renderInterface = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();
            var context = graphics.GetSharedContext();
            var backend = renderInterface.CreateBackendContext(context);
            try
            {
                using var target = backend.CreateRenderTarget([surface]);
                Draw(context, target, window.Size);
                var luid = graphics.DeviceLuid;
                var generation = graphics.DeviceGeneration;
                graphics.WaitForGpu();
                RemoveDevice(graphics.DeviceHandle);

                // The compositor recreates its backend before disposing corrupted window targets.
                backend.Dispose();
                var restoredContext = graphics.GetSharedContext();
                backend = renderInterface.CreateBackendContext(restoredContext);

                Assert.True(context.IsLost);
                Assert.Throws<PlatformGraphicsContextLostException>(() => context.EnsureCurrent());
                Assert.Equal(generation + 1, graphics.DeviceGeneration);
                Assert.Equal(luid, graphics.DeviceLuid);
                Assert.True(target.PlatformRenderTargetState.IsCorrupted);
                target.Dispose();

                using var restored = backend.CreateRenderTarget([surface]);
                Draw(restoredContext, restored, window.Size);
                window.Size = new PixelSize(96, 80);
                Draw(restoredContext, restored, window.Size);
                graphics.WaitForGpu();
                // Closing a window can dispose its visual host before the render target.
                (surface as IDisposable)?.Dispose();
                restored.Dispose();
            }
            finally
            {
                backend.Dispose();
                (surface as IDisposable)?.Dispose();
            }
        }
        finally
        {
            connection?.Stop();
        }
    }

    [Fact]
    public void Device_Removal_Releases_Imported_Resources_Before_Recreation()
    {
        using var scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<Win32PlatformOptions>().ToConstant(new Win32PlatformOptions
        {
            GraphicsAdapterSelectionCallback = _ => 0
        });
        using var graphics = CreateGraphics();
        var context = graphics.GetSharedContext();
        var feature = graphics.ExternalObjects;
        var heap = D3D12Interop.D3D12_HEAP_PROPERTIES.Create(D3D12Interop.D3D12_HEAP_TYPE_DEFAULT);
        var desc = D3D12Interop.D3D12_RESOURCE_DESC.Texture2D(87, 32, 32,
            D3D12Interop.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET);
        Marshal.ThrowExceptionForHR(D3D12Interop.CreateCommittedResource(graphics.DeviceHandle, in heap,
            1, in desc, D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET, IntPtr.Zero,
            in D3D12Interop.IID_ID3D12Resource, out var resource)); // D3D12_HEAP_FLAG_SHARED
        var textureHandle = ExportAndRelease(graphics, resource);
        ID3D12PlatformGraphicsExternalImage image;
        try
        {
            image = feature.ImportImage(KnownPlatformGraphicsExternalImageHandleTypes.Direct3D12TextureNtHandle,
                new() { Width = 32, Height = 32, Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm },
                textureHandle);
        }
        finally
        {
            Avalonia.Win32.Interop.UnmanagedMethods.CloseHandle(textureHandle);
        }
        using (image)
        {
            Marshal.ThrowExceptionForHR(D3D12Interop.CreateFence(graphics.DeviceHandle, 0, 1,
                in D3D12Interop.IID_ID3D12Fence, out var fence)); // D3D12_FENCE_FLAG_SHARED
            var fenceHandle = ExportAndRelease(graphics, fence);
            ID3D12PlatformGraphicsExternalSemaphore semaphore;
            try
            {
                semaphore = feature.ImportSemaphore(
                    KnownPlatformGraphicsExternalSemaphoreHandleTypes.Direct3D12FenceNtHandle, fenceHandle);
            }
            finally
            {
                Avalonia.Win32.Interop.UnmanagedMethods.CloseHandle(fenceHandle);
            }
            using (semaphore)
            {
                image.TransitionToCommon(D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET);
                graphics.WaitForGpu();
                RemoveDevice(graphics.DeviceHandle);
                Assert.NotSame(context, graphics.GetSharedContext());
                Assert.Throws<ObjectDisposedException>(() => image.ResourceHandle);
                Assert.Throws<ObjectDisposedException>(() => semaphore.FenceHandle);
                Assert.Throws<PlatformGraphicsContextLostException>(() => feature.EnqueueSignal(semaphore, 1));
            }
        }
    }

    private static IntPtr ExportAndRelease(D3D12PlatformGraphics graphics, IntPtr resource)
    {
        try
        {
            Marshal.ThrowExceptionForHR(D3D12Interop.CreateSharedHandle(graphics.DeviceHandle, resource,
                IntPtr.Zero, 0x10000000, null, out var handle));
            return handle;
        }
        finally
        {
            Marshal.Release(resource);
        }
    }

    [Fact]
    public void Dispose_Waits_For_Queued_Gpu_Work()
    {
        using var graphics = CreateGraphics();
        Marshal.ThrowExceptionForHR(D3D12Interop.CreateFence(graphics.DeviceHandle, 0, 0,
            in D3D12Interop.IID_ID3D12Fence, out var pointer));
        using var fence = MicroComRuntime.CreateProxyFor<ID3D12Fence>(pointer, true);
        Marshal.ThrowExceptionForHR(graphics.CommandQueue.Wait((void*)pointer, 1));
        using var disposeReturned = new ManualResetEventSlim();
        var returnedBeforeSignal = false;
        var signalResult = 0;
        var producer = new Thread(() =>
        {
            // Always release the queue so a failed assertion cannot strand teardown.
            returnedBeforeSignal = disposeReturned.Wait(TimeSpan.FromMilliseconds(250));
            signalResult = fence.Signal(1);
        });
        producer.Start();
        try
        {
            graphics.Dispose();
            disposeReturned.Set();
        }
        finally
        {
            producer.Join();
        }

        Marshal.ThrowExceptionForHR(signalResult);
        Assert.False(returnedBeforeSignal);
    }

    [Fact]
    public void EnsureCurrent_Serializes_Threads_And_Allows_Nesting()
    {
        using var graphics = CreateGraphics();
        var context = graphics.GetSharedContext();
        using var attempted = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var second = new Thread(() =>
        {
            attempted.Set();
            using (context.EnsureCurrent())
                entered.Set();
        });
        var current = context.EnsureCurrent();
        try
        {
            using (context.EnsureCurrent())
                Assert.False(context.IsLost);
            second.Start();
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.False(entered.Wait(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
        }
        finally
        {
            current.Dispose();
            Assert.True(second.Join(TimeSpan.FromSeconds(5)));
        }
        Assert.True(entered.IsSet);
    }

    private static D3D12PlatformGraphics CreateGraphics()
    {
        var graphics = D3D12PlatformGraphics.TryCreate();
        Assert.SkipWhen(graphics is null, "Direct3D 12 is not available.");
        return graphics!;
    }

    private static void Draw(IPlatformGraphicsContext context, IRenderTarget target, PixelSize size)
    {
        using (context.EnsureCurrent())
        using (var drawing = target.CreateDrawingContext(new(size, 1, CompositionTransparencyLevel.None), out _))
            drawing.Clear(Colors.Blue);
    }

    private static void RemoveDevice(IntPtr device)
    {
        var iid = new Guid("8b4f173b-2fea-4b80-8f58-4307191ab95d");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out var device5));
        try
        {
            // ID3D12Device5::RemoveDevice removes only this process's device, without resetting the adapter.
            var vtable = *(IntPtr**)device5;
            ((delegate* unmanaged[Stdcall]<IntPtr, void>)vtable[58])(device5);
        }
        finally
        {
            Marshal.Release(device5);
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
