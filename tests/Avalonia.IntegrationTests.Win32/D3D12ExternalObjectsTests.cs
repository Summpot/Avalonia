using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Win32.DirectX;
using MicroCom.Runtime;
using SkiaSharp;
using Avalonia.Direct3D12;
using Avalonia.IntegrationTests.Win32.Infrastructure;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Win32.Direct3D12;
using Xunit;

namespace Avalonia.IntegrationTests.Win32;

public unsafe class D3D12ExternalObjectsTests
{
    [Fact]
    public void Context_Exposes_ExternalObjects_Feature()
    {
        using var graphics = D3D12PlatformGraphics.TryCreate();
        Assert.SkipWhen(graphics is null, "Direct3D 12 is not available.");
        Assert.NotNull(graphics);

        using var context = (ID3D12PlatformGraphicsContext)graphics.CreateContext();
        var feature = context.TryGetFeature<ID3D12PlatformGraphicsExternalObjects>();

        Assert.NotNull(feature);
        Assert.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle, feature.SupportedImageHandleTypes);
        Assert.Contains(KnownPlatformGraphicsExternalImageHandleTypes.Direct3D12TextureNtHandle, feature.SupportedImageHandleTypes);
        Assert.Contains(KnownPlatformGraphicsExternalSemaphoreHandleTypes.Direct3D12FenceNtHandle, feature.SupportedSemaphoreTypes);
        Assert.NotNull(feature.DeviceLuid);
        Assert.Equal(8, feature.DeviceLuid.Length);
    }

    [Fact]
    public void Skia_Backend_Context_Exposes_ExternalObjects_RenderInterface_Feature()
    {
        using var graphics = D3D12PlatformGraphics.TryCreate();
        Assert.SkipWhen(graphics is null, "Direct3D 12 is not available.");
        Assert.NotNull(graphics);

        using var scope = AvaloniaLocator.EnterScope();
        SkiaPlatform.Initialize();
        using var context = (ID3D12PlatformGraphicsContext)graphics.CreateContext();
        var renderInterface = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();
        using var backendContext = renderInterface.CreateBackendContext(context);

        var feature = backendContext.TryGetFeature<IExternalObjectsRenderInterfaceContextFeature>();

        Assert.NotNull(feature);
        Assert.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle, feature.SupportedImageHandleTypes);
        Assert.Contains(KnownPlatformGraphicsExternalImageHandleTypes.Direct3D12TextureNtHandle, feature.SupportedImageHandleTypes);
        Assert.Contains(KnownPlatformGraphicsExternalSemaphoreHandleTypes.Direct3D12FenceNtHandle, feature.SupportedSemaphoreTypes);
        Assert.NotNull(feature.DeviceLuid);
        Assert.Equal(CompositionGpuImportedImageSynchronizationCapabilities.TimelineSemaphores,
            feature.GetSynchronizationCapabilities(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle));
    }

    [Fact]
    public void ExternalObjects_Validates_Invalid_Handles()
    {
        using var graphics = D3D12PlatformGraphics.TryCreate();
        Assert.SkipWhen(graphics is null, "Direct3D 12 is not available.");
        Assert.NotNull(graphics);

        using var context = (ID3D12PlatformGraphicsContext)graphics.CreateContext();
        var feature = context.TryGetFeature<ID3D12PlatformGraphicsExternalObjects>();
        Assert.NotNull(feature);

        Assert.Throws<ArgumentException>(() =>
            feature.ImportImage(KnownPlatformGraphicsExternalImageHandleTypes.Direct3D12TextureNtHandle, new PlatformGraphicsExternalImageProperties { Width = 100, Height = 100, Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm }, IntPtr.Zero));

        Assert.Throws<NotSupportedException>(() =>
            feature.ImportSemaphore("UnknownSemaphoreType", new IntPtr(1)));

        Assert.Throws<ArgumentException>(() =>
            feature.ImportSemaphore(KnownPlatformGraphicsExternalSemaphoreHandleTypes.Direct3D12FenceNtHandle, IntPtr.Zero));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snapshots_Copy_Pixels_And_Return_Texture_To_Common(bool simultaneousAccess)
    {
        using var fixture = new SharedTexture(simultaneousAccess);
        using var image = fixture.ImportImage();
        using var semaphore = fixture.ImportSemaphore();
        using var producer = GRContext.CreateDirect3D(new GRD3DBackendContext
        {
            Adapter = fixture.Graphics.AdapterHandle,
            Device = fixture.Graphics.DeviceHandle,
            Queue = fixture.Graphics.CommandQueueHandle
        });
        Assert.NotNull(producer);
        using var commands = new D3D12PerBufferCommandResources(1);
        commands.EnsureCreated(fixture.Graphics);

        for (var frame = 0; frame < 5; frame++)
        {
            var color = frame % 2 == 0 ? SKColors.Red : SKColors.Blue;
            using (var target = new GRBackendRenderTarget(32, 32, new GRD3DTextureResourceInfo
            {
                Resource = fixture.Resource, ResourceState = 0, Format = 87,
                SampleCount = 1, LevelCount = 1
            }))
            using (var surface = SKSurface.Create(producer, target, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888))
            {
                Assert.NotNull(surface);
                surface.Canvas.Clear(color);
                producer.Flush(true, true);
            }
            if (!simultaneousAccess)
            {
                var value = fixture.Graphics.ExecuteTransition(commands.GetAllocator(0), commands.GetCommandList(0),
                    fixture.Resource, D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET,
                    D3D12Interop.D3D12_RESOURCE_STATE_COMMON, commands.AllocatorFenceValues[0]);
                commands.AllocatorFenceValues[0] = value;
            }
            Marshal.ThrowExceptionForHR(fixture.Graphics.CommandQueue.Signal((void*)fixture.Fence, (ulong)frame * 2 + 1));
            using var snapshot = image.SnapshotWithTimelineSemaphores(semaphore, (ulong)frame * 2 + 1, semaphore, (ulong)frame * 2 + 2);
            fixture.Graphics.WaitForGpu();
            Assert.True(fixture.FenceProxy.CompletedValue >= (ulong)frame * 2 + 2);
            using var stream = new MemoryStream();
            snapshot.Save(stream, new PngBitmapEncoderOptions());
            stream.Position = 0;
            using var bitmap = SKBitmap.Decode(stream);
            Assert.NotNull(bitmap);
            Assert.Equal(color, bitmap.GetPixel(0, 0));
            Assert.Equal(color, bitmap.GetPixel(31, 31));
            fixture.AssertNoErrors();
        }
    }

    [Fact]
    public void D3D11_Producer_Snapshots_Copy_Pixels_With_Timeline_Fences()
    {
        using var fixture = new SharedTexture(false);
        IntPtr contextPointer;
        DirectXUnmanagedMethods.D3D11CreateDevice(fixture.Graphics.AdapterHandle,
            D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, IntPtr.Zero, D3D12Interop.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            [D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0], 1, 7, out var devicePointer, out _, &contextPointer);
        using var device = MicroComRuntime.CreateProxyFor<ID3D11Device>(devicePointer, true);
        using var context = MicroComRuntime.CreateProxyFor<ID3D11DeviceContext>(contextPointer, true);
        using var device5 = QueryInterface(devicePointer, "8ffde202-a0e7-45df-9e01-e837801b5ea0");
        using var context4 = QueryInterface(contextPointer, "917600da-f58c-4c33-98d8-3e15b390fa24");
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = 32, Height = 32, MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            MiscFlags = D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED_NTHANDLE
                | D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED
        };
        using var texture = device.CreateTexture2D(&desc, IntPtr.Zero);
        using var view = device.CreateRenderTargetView(texture.GetNativeIntPtr(), IntPtr.Zero);
        using var resource1 = QueryInterface(texture.GetNativeIntPtr(), "30961379-4609-4a41-998e-54fe567ee0c1");
        var resourcePointer = resource1.GetNativeIntPtr();
        var resourceVtable = *(IntPtr**)resourcePointer;
        IntPtr textureHandle;
        // IDXGIResource1::CreateSharedHandle, with DXGI_SHARED_RESOURCE_READ | WRITE.
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, void*, uint, char*, IntPtr*, int>)
            resourceVtable[13])(resourcePointer, null, 0x80000001, null, &textureHandle));
        try
        {
            var device5Pointer = device5.GetNativeIntPtr();
            var deviceVtable = *(IntPtr**)device5Pointer;
            var fenceId = new Guid("affde9d1-1df7-4bb7-8a34-0f46251dab80");
            IntPtr fencePointer;
            // ID3D11Device5::OpenSharedFence imports the D3D12 timeline fence.
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)
                deviceVtable[67])(device5Pointer, fixture.FenceHandle, &fenceId, &fencePointer));
            using var fence = MicroComRuntime.CreateProxyFor<IUnknown>(fencePointer, true);
            using var semaphore = fixture.ImportSemaphore();
            using var image = fixture.ImportD3D11Image(textureHandle);
            var context4Pointer = context4.GetNativeIntPtr();
            var contextVtable = *(IntPtr**)context4Pointer;
            var signal = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, int>)contextVtable[147];
            var wait = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, int>)contextVtable[148];
            var rgba = stackalloc float[4];
            for (var frame = 0; frame < 5; frame++)
            {
                // ID3D11DeviceContext4::Wait orders the next write after Avalonia's copy.
                if (frame > 0)
                    Marshal.ThrowExceptionForHR(wait(context4Pointer, fencePointer, (ulong)frame * 2));
                rgba[0] = frame % 2 == 0 ? 1 : 0;
                rgba[1] = 0;
                rgba[2] = frame % 2 == 0 ? 0 : 1;
                rgba[3] = 1;
                context.ClearRenderTargetView((void*)view.GetNativeIntPtr(), rgba);
                context.Flush();
                Marshal.ThrowExceptionForHR(signal(context4Pointer, fencePointer, (ulong)frame * 2 + 1));
                context.Flush();
                using var snapshot = image.SnapshotWithTimelineSemaphores(
                    semaphore, (ulong)frame * 2 + 1, semaphore, (ulong)frame * 2 + 2);
                using var stream = new MemoryStream();
                snapshot.Save(stream, new PngBitmapEncoderOptions());
                stream.Position = 0;
                using var bitmap = SKBitmap.Decode(stream);
                Assert.NotNull(bitmap);
                var expected = frame % 2 == 0 ? SKColors.Red : SKColors.Blue;
                Assert.Equal(expected, bitmap.GetPixel(0, 0));
                Assert.Equal(expected, bitmap.GetPixel(31, 31));
                fixture.AssertNoErrors();
            }
            fixture.Graphics.WaitForGpu();
            Assert.Equal(10ul, fixture.FenceProxy.CompletedValue);
        }
        finally
        {
            Avalonia.Win32.Interop.UnmanagedMethods.CloseHandle(textureHandle);
        }
    }

    private static IUnknown QueryInterface(IntPtr pointer, string interfaceId)
    {
        var iid = new Guid(interfaceId);
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(pointer, in iid, out var result));
        return MicroComRuntime.CreateProxyFor<IUnknown>(result, true);
    }

    [Fact]
    public void Snapshot_Submits_Without_Waiting_For_Producer_On_Cpu()
    {
        using var fixture = new SharedTexture(false);
        using var image = fixture.ImportImage();
        using var semaphore = fixture.ImportSemaphore();
        using var snapshotReturned = new ManualResetEventSlim();
        var returnedBeforeSignal = false;
        var signalResult = 0;
        var producer = new Thread(() =>
        {
            // The timeout releases a blocked queue so a regression fails instead of hanging teardown.
            returnedBeforeSignal = snapshotReturned.Wait(TimeSpan.FromSeconds(5));
            signalResult = fixture.FenceProxy.Signal(1);
        });
        producer.Start();
        try
        {
            using var snapshot = image.SnapshotWithTimelineSemaphores(semaphore, 1, semaphore, 2);
        }
        finally
        {
            snapshotReturned.Set();
            producer.Join();
        }
        Marshal.ThrowExceptionForHR(signalResult);
        Assert.True(returnedBeforeSignal, "Snapshot waited for the producer on the CPU.");
        fixture.Graphics.WaitForGpu();
        Assert.Equal(2ul, fixture.FenceProxy.CompletedValue);
    }

    [Fact]
    public void Import_Rejects_Unsupported_Format_And_Mismatched_Resource_Properties()
    {
        using var fixture = new SharedTexture(false);
        var feature = fixture.Graphics.ExternalObjects;
        var properties = SharedTexture.Properties;
        properties.Format = (PlatformGraphicsExternalImageFormat)99;
        Assert.Throws<NotSupportedException>(() => feature.ImportImage(
            KnownPlatformGraphicsExternalImageHandleTypes.Direct3D12TextureNtHandle, properties, new IntPtr(1)));
        properties = SharedTexture.Properties;
        properties.Width++;
        Assert.Throws<ArgumentException>(() => feature.ImportImage(
            KnownPlatformGraphicsExternalImageHandleTypes.Direct3D12TextureNtHandle, properties, fixture.TextureHandle));
        Assert.Throws<NotSupportedException>(() => feature.ImportImage("UnknownImageType", SharedTexture.Properties, fixture.TextureHandle));
        using var validImage = fixture.ImportImage();
    }

    private sealed class SharedTexture : IDisposable
    {
        private readonly IDisposable _scope;
        private readonly IPlatformRenderInterfaceContext _backend;
        private readonly IExternalObjectsRenderInterfaceContextFeature _feature;
        private readonly D3D12DebugLayer _debugLayer;
        private readonly IntPtr _fenceHandle;
        public D3D12PlatformGraphics Graphics { get; }
        public IntPtr Resource { get; }
        public IntPtr TextureHandle { get; }
        public IntPtr Fence { get; }
        public ID3D12Fence FenceProxy { get; }
        public IntPtr FenceHandle => _fenceHandle;
        public static PlatformGraphicsExternalImageProperties Properties => new()
        {
            Width = 32, Height = 32, Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm, TopLeftOrigin = true
        };

        public SharedTexture(bool simultaneousAccess)
        {
            var graphics = D3D12PlatformGraphics.TryCreate();
            Assert.SkipWhen(graphics is null, "Direct3D 12 is not available.");
            Graphics = graphics!;
            _debugLayer = new D3D12DebugLayer(Graphics.DeviceHandle);
            _scope = AvaloniaLocator.EnterScope();
            SkiaPlatform.Initialize();
            _backend = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>()
                .CreateBackendContext(Graphics.GetSharedContext());
            _feature = _backend.TryGetFeature<IExternalObjectsRenderInterfaceContextFeature>()!;
            var heap = D3D12Interop.D3D12_HEAP_PROPERTIES.Create(D3D12Interop.D3D12_HEAP_TYPE_DEFAULT);
            var desc = D3D12Interop.D3D12_RESOURCE_DESC.Texture2D(87, 32, 32,
                D3D12Interop.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET
                | (simultaneousAccess ? D3D12Interop.D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS : 0));
            Marshal.ThrowExceptionForHR(D3D12Interop.CreateCommittedResource(Graphics.DeviceHandle, in heap,
                1, in desc, D3D12Interop.D3D12_RESOURCE_STATE_COMMON, IntPtr.Zero,
                in D3D12Interop.IID_ID3D12Resource, out var resource)); // D3D12_HEAP_FLAG_SHARED
            Resource = resource;
            Marshal.ThrowExceptionForHR(D3D12Interop.CreateSharedHandle(Graphics.DeviceHandle, resource,
                IntPtr.Zero, 0x10000000, null, out var textureHandle));
            TextureHandle = textureHandle;
            Marshal.ThrowExceptionForHR(D3D12Interop.CreateFence(Graphics.DeviceHandle, 0, 1,
                in D3D12Interop.IID_ID3D12Fence, out var fence)); // D3D12_FENCE_FLAG_SHARED
            Fence = fence;
            FenceProxy = MicroComRuntime.CreateProxyFor<ID3D12Fence>(fence, true);
            Marshal.ThrowExceptionForHR(D3D12Interop.CreateSharedHandle(Graphics.DeviceHandle, fence,
                IntPtr.Zero, 0x10000000, null, out _fenceHandle));
        }

        public IPlatformRenderInterfaceImportedImage ImportImage() => _feature.ImportImage(
            new PlatformHandle(TextureHandle, KnownPlatformGraphicsExternalImageHandleTypes.Direct3D12TextureNtHandle), Properties);

        public IPlatformRenderInterfaceImportedImage ImportD3D11Image(IntPtr handle) => _feature.ImportImage(
            new PlatformHandle(handle, KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle), Properties);

        public IPlatformRenderInterfaceImportedSemaphore ImportSemaphore() => _feature.ImportSemaphore(
            new PlatformHandle(_fenceHandle, KnownPlatformGraphicsExternalSemaphoreHandleTypes.Direct3D12FenceNtHandle));

        public void AssertNoErrors() => _debugLayer.AssertNoErrors();

        public void Dispose()
        {
            Graphics.WaitForGpu();
            _backend.Dispose();
            Avalonia.Win32.Interop.UnmanagedMethods.CloseHandle(_fenceHandle);
            Avalonia.Win32.Interop.UnmanagedMethods.CloseHandle(TextureHandle);
            FenceProxy.Dispose();
            D3D12Interop.Release(Resource);
            _debugLayer.Dispose();
            Graphics.Dispose();
            _scope.Dispose();
        }
    }
}
