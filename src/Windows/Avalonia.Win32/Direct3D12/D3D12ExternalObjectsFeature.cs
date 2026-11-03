using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Direct3D12;
using Avalonia.Platform;

namespace Avalonia.Win32.Direct3D12;

internal unsafe class D3D12ExternalObjectsFeature : ID3D12PlatformGraphicsExternalObjects
{
    private readonly D3D12PlatformGraphics _graphics;
    private readonly int _deviceGeneration;

    public D3D12ExternalObjectsFeature(D3D12PlatformGraphics graphics)
    {
        _graphics = graphics;
        _deviceGeneration = graphics.DeviceGeneration;
    }

    public IReadOnlyList<string> SupportedImageHandleTypes { get; } =
    [
        KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle,
        KnownPlatformGraphicsExternalImageHandleTypes.Direct3D12TextureNtHandle
    ];

    public IReadOnlyList<string> SupportedSemaphoreTypes { get; } =
    [
        KnownPlatformGraphicsExternalSemaphoreHandleTypes.Direct3D12FenceNtHandle
    ];

    public byte[]? DeviceLuid => _graphics.DeviceLuid;

    public ID3D12PlatformGraphicsExternalImage ImportImage(string type, PlatformGraphicsExternalImageProperties properties, IntPtr handle)
    {
        using var current = _graphics.EnsureCurrent(_deviceGeneration);
        if (type != KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle
            && type != KnownPlatformGraphicsExternalImageHandleTypes.Direct3D12TextureNtHandle)
            throw new NotSupportedException($"Unsupported image handle type: {type}");
        if (handle == IntPtr.Zero)
            throw new ArgumentException("Invalid handle", nameof(handle));
        if (properties.Width <= 0 || properties.Height <= 0)
            throw new ArgumentException("Image dimensions must be positive", nameof(properties));

        var format = properties.Format switch
        {
            PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm => D3D12DxgiFormat.R8G8B8A8_UNORM,
            PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm => D3D12DxgiFormat.B8G8R8A8_UNORM,
            _ => throw new NotSupportedException($"Unsupported external image format {properties.Format}")
        };

        Marshal.ThrowExceptionForHR(D3D12Interop.OpenSharedHandle(
            _graphics.DeviceHandle, handle, in D3D12Interop.IID_ID3D12Resource, out var resource));
        try
        {
            var desc = D3D12Interop.GetResourceDescription(resource);
            if (desc.Dimension != D3D12Interop.D3D12_RESOURCE_DIMENSION_TEXTURE2D
                || desc.Width != (ulong)properties.Width || desc.Height != properties.Height
                || desc.Format != (uint)format || desc.DepthOrArraySize != 1
                || desc.MipLevels != 1 || desc.SampleCount != 1
                || (desc.Flags & D3D12Interop.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET) == 0)
                throw new ArgumentException("The shared resource must be a single-sample, single-mip 2D render target matching the image properties", nameof(properties));

            return new D3D12ExternalImage(_graphics, resource, format,
                new PixelSize(properties.Width, properties.Height),
                (desc.Flags & D3D12Interop.D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS) != 0);
        }
        catch
        {
            D3D12Interop.Release(resource);
            throw;
        }
    }

    public ID3D12PlatformGraphicsExternalSemaphore ImportSemaphore(string type, IntPtr handle)
    {
        using var current = _graphics.EnsureCurrent(_deviceGeneration);
        if (type != KnownPlatformGraphicsExternalSemaphoreHandleTypes.Direct3D12FenceNtHandle)
            throw new NotSupportedException($"Unsupported semaphore type: {type}");

        if (handle == IntPtr.Zero)
            throw new ArgumentException("Invalid handle", nameof(handle));

        var hr = D3D12Interop.OpenSharedHandle(
            _graphics.DeviceHandle,
            handle,
            in D3D12Interop.IID_ID3D12Fence,
            out var fence);

        if (hr != 0 || fence == IntPtr.Zero)
            Marshal.ThrowExceptionForHR(hr != 0 ? hr : unchecked((int)0x80004005));

        return new D3D12ExternalSemaphore(_graphics, fence);
    }

    public void EnqueueWait(ID3D12PlatformGraphicsExternalSemaphore semaphore, ulong value)
    {
        using var current = _graphics.EnsureCurrent(_deviceGeneration);
        var hr = _graphics.CommandQueue.Wait((void*)semaphore.FenceHandle, value);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    public void EnqueueSignal(ID3D12PlatformGraphicsExternalSemaphore semaphore, ulong value)
    {
        using var current = _graphics.EnsureCurrent(_deviceGeneration);
        var hr = _graphics.CommandQueue.Signal((void*)semaphore.FenceHandle, value);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);
    }
}

internal class D3D12ExternalImage : ID3D12PlatformGraphicsExternalImage
{
    private readonly D3D12PlatformGraphics _graphics;
    private readonly int _deviceGeneration;
    private readonly bool _simultaneousAccess;
    private readonly D3D12PerBufferCommandResources _commands = new();
    private uint _commandIndex;
    private ulong _lastUse;
    private IntPtr _resource;

    public D3D12ExternalImage(D3D12PlatformGraphics graphics, IntPtr resource,
        D3D12DxgiFormat format, PixelSize size, bool simultaneousAccess)
    {
        _graphics = graphics;
        _deviceGeneration = graphics.DeviceGeneration;
        _resource = resource;
        _simultaneousAccess = simultaneousAccess;
        Format = format;
        Size = size;
        graphics.BeforeDeviceRelease += Dispose;
    }

    public IntPtr ResourceHandle => _resource != IntPtr.Zero
        ? _resource
        : throw new ObjectDisposedException(nameof(D3D12ExternalImage));

    public D3D12DxgiFormat Format { get; }
    public PixelSize Size { get; }
    public int SampleCount => 1;
    public int LevelCount => 1;

    public void TransitionToCommon(uint resourceState)
    {
        using var current = _graphics.EnsureCurrent(_deviceGeneration);
        var resource = ResourceHandle;
        if (_simultaneousAccess)
            _lastUse = _graphics.Signal();
        else
        {
            _commands.EnsureCreated(_graphics);
            _lastUse = _graphics.ExecuteTransition(
                _commands.GetAllocator(_commandIndex), _commands.GetCommandList(_commandIndex),
                resource, resourceState, D3D12Interop.D3D12_RESOURCE_STATE_COMMON,
                _commands.AllocatorFenceValues[_commandIndex]);
            _commands.AllocatorFenceValues[_commandIndex] = _lastUse;
            _commandIndex = (_commandIndex + 1) % D3D12SwapchainBackBuffers.BufferCount;
        }
    }

    public void Dispose()
    {
        lock (_graphics.SyncRoot)
        {
            if (_resource == IntPtr.Zero)
                return;
            try
            {
                if (!_graphics.IsLost && _deviceGeneration == _graphics.DeviceGeneration)
                    _graphics.WaitForFenceValue(_lastUse);
            }
            finally
            {
                _graphics.BeforeDeviceRelease -= Dispose;
                _commands.Dispose();
                D3D12Interop.Release(_resource);
                _resource = IntPtr.Zero;
            }
        }
    }
}

internal class D3D12ExternalSemaphore : ID3D12PlatformGraphicsExternalSemaphore
{
    private readonly D3D12PlatformGraphics _graphics;
    private IntPtr _fence;

    public D3D12ExternalSemaphore(D3D12PlatformGraphics graphics, IntPtr fence)
    {
        _graphics = graphics;
        _fence = fence;
        graphics.BeforeDeviceRelease += Dispose;
    }

    public IntPtr FenceHandle => _fence != IntPtr.Zero
        ? _fence
        : throw new ObjectDisposedException(nameof(D3D12ExternalSemaphore));

    public void Dispose()
    {
        lock (_graphics.SyncRoot)
        {
            if (_fence != IntPtr.Zero)
            {
                _graphics.BeforeDeviceRelease -= Dispose;
                D3D12Interop.Release(_fence);
                _fence = IntPtr.Zero;
            }
        }
    }
}
