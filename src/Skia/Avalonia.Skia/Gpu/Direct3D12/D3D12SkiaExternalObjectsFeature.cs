using System;
using System.Collections.Generic;
using Avalonia.Direct3D12;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using SkiaSharp;

namespace Avalonia.Skia.Direct3D12;

internal class D3D12SkiaExternalObjectsFeature : IExternalObjectsRenderInterfaceContextFeature
{
    private readonly D3D12SkiaGpu _gpu;
    private readonly ID3D12PlatformGraphicsExternalObjects _feature;

    public D3D12SkiaExternalObjectsFeature(D3D12SkiaGpu gpu, ID3D12PlatformGraphicsExternalObjects feature)
    {
        _gpu = gpu;
        _feature = feature;
    }

    public IReadOnlyList<string> SupportedImageHandleTypes => _feature.SupportedImageHandleTypes;
    public IReadOnlyList<string> SupportedSemaphoreTypes => _feature.SupportedSemaphoreTypes;
    public byte[]? DeviceUuid => null;
    public byte[]? DeviceLuid => _feature.DeviceLuid;

    public CompositionGpuImportedImageSynchronizationCapabilities GetSynchronizationCapabilities(string imageHandleType) =>
        CompositionGpuImportedImageSynchronizationCapabilities.TimelineSemaphores;

    public IPlatformRenderInterfaceImportedImage ImportImage(IPlatformHandle handle, PlatformGraphicsExternalImageProperties properties) =>
        new Image(_gpu, _feature, _feature.ImportImage(handle.HandleDescriptor ?? throw new ArgumentException("Handle descriptor is required", nameof(handle)), properties, handle.Handle), properties);

    public IPlatformRenderInterfaceImportedImage ImportImage(ICompositionImportableSharedGpuContextImage image) =>
        throw new NotSupportedException();

    public IPlatformRenderInterfaceImportedSemaphore ImportSemaphore(IPlatformHandle handle) =>
        new Semaphore(_feature.ImportSemaphore(handle.HandleDescriptor ?? throw new ArgumentException("Handle descriptor is required", nameof(handle)), handle.Handle));

    private class Semaphore : IPlatformRenderInterfaceImportedSemaphore
    {
        private ID3D12PlatformGraphicsExternalSemaphore? _inner;

        public Semaphore(ID3D12PlatformGraphicsExternalSemaphore inner)
        {
            _inner = inner;
        }

        public ID3D12PlatformGraphicsExternalSemaphore Inner =>
            _inner ?? throw new ObjectDisposedException(nameof(Semaphore));

        public void Dispose()
        {
            _inner?.Dispose();
            _inner = null;
        }
    }

    private class Image : IPlatformRenderInterfaceImportedImage
    {
        private readonly D3D12SkiaGpu _gpu;
        private readonly ID3D12PlatformGraphicsExternalObjects _feature;
        private ID3D12PlatformGraphicsExternalImage? _inner;
        private readonly PlatformGraphicsExternalImageProperties _properties;

        public Image(
            D3D12SkiaGpu gpu,
            ID3D12PlatformGraphicsExternalObjects feature,
            ID3D12PlatformGraphicsExternalImage inner,
            PlatformGraphicsExternalImageProperties properties)
        {
            _gpu = gpu;
            _feature = feature;
            _inner = inner;
            _properties = properties;
        }

        public void Dispose()
        {
            _inner?.Dispose();
            _inner = null;
        }

        public IBitmapImpl SnapshotWithKeyedMutex(uint acquireIndex, uint releaseIndex) =>
            throw new NotSupportedException();

        public IBitmapImpl SnapshotWithSemaphores(
            IPlatformRenderInterfaceImportedSemaphore waitForSemaphore,
            IPlatformRenderInterfaceImportedSemaphore signalSemaphore) =>
            throw new NotSupportedException();

        public IBitmapImpl SnapshotWithTimelineSemaphores(
            IPlatformRenderInterfaceImportedSemaphore waitForSemaphore,
            ulong waitForValue,
            IPlatformRenderInterfaceImportedSemaphore signalSemaphore,
            ulong signalValue)
        {
            if (_inner is null)
                throw new ObjectDisposedException(nameof(Image));

            var waitSem = ((Semaphore)waitForSemaphore).Inner;
            var signalSem = ((Semaphore)signalSemaphore).Inner;
            _gpu.GrContext.Flush();
            _gpu.GrContext.ResetContext();
            _feature.EnqueueWait(waitSem, waitForValue);

            var textureInfo = new GRD3DTextureResourceInfo
            {
                Resource = _inner.ResourceHandle,
                ResourceState = D3D12ResourceStates.Common,
                Format = (uint)_inner.Format,
                SampleCount = (uint)_inner.SampleCount,
                LevelCount = (uint)_inner.LevelCount,
                SampleQualityPattern = 0,
                Protected = false
            };

            using var renderTarget = new GRBackendRenderTarget(_properties.Width, _properties.Height, textureInfo);
            var colorType = _inner.Format switch
            {
                D3D12DxgiFormat.R8G8B8A8_UNORM => SKColorType.Rgba8888,
                D3D12DxgiFormat.B8G8R8A8_UNORM => SKColorType.Bgra8888,
                _ => throw new NotSupportedException($"Unsupported format {_inner.Format}")
            };

            using var colorSpace = SKColorSpace.CreateSrgb();
            using var surface = SKSurface.Create(
                _gpu.GrContext,
                renderTarget,
                _properties.TopLeftOrigin ? GRSurfaceOrigin.TopLeft : GRSurfaceOrigin.BottomLeft,
                colorType,
                colorSpace);

            if (surface is null)
                throw new InvalidOperationException("Failed to create SKSurface from imported D3D12 texture.");

            var image = surface.Snapshot();
            try
            {
                // A wrapped render-target snapshot copies into a Skia-owned image. Submit the copy
                // before returning the source to COMMON and releasing it to the producer.
                _gpu.GrContext.Flush();
                _inner.TransitionToCommon(D3D12ResourceStates.CopySource);
                _feature.EnqueueSignal(signalSem, signalValue);
                return new ImmutableBitmap(image);
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        public IBitmapImpl SnapshotWithAutomaticSync() => throw new NotSupportedException();
    }
}
