using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia.Win32.DirectX;

namespace Avalonia.Win32.Direct3D12;

/// <summary>Preserves a frame's first failure while recording device loss during any cleanup step.</summary>
internal struct D3D12PresentationFailure
{
    private Exception? _primary;
    private bool _deviceLost;

    public bool HasFailure => _primary is not null;

    public void Capture(Exception exception)
    {
        _primary ??= exception;
        _deviceLost |= exception is PlatformGraphicsContextLostException
                       || IsDeviceLostResult(exception.HResult);
    }

    public static void ThrowIfFailed(int result)
    {
        if (IsDeviceLostResult(result))
            throw new PlatformGraphicsContextLostException();
        Marshal.ThrowExceptionForHR(result);
    }

    private static bool IsDeviceLostResult(int result) =>
        (DXGI_ERROR)unchecked((uint)result) is DXGI_ERROR.DXGI_ERROR_DEVICE_REMOVED
            or DXGI_ERROR.DXGI_ERROR_DEVICE_HUNG or DXGI_ERROR.DXGI_ERROR_DEVICE_RESET;

    public void Throw(Action markCorrupted, Action markLost)
    {
        if (_primary is null)
            return;
        markCorrupted();
        if (_deviceLost)
            markLost();
        ExceptionDispatchInfo.Capture(_primary).Throw();
    }
}
