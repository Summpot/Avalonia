using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Win32.Direct3D12;
using Avalonia.Win32.DirectX;
using Xunit;

namespace Avalonia.IntegrationTests.Win32;

public class D3D12DrawingSurfaceProbeTests
{
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);

    [Fact]
    public void Direct_Rendering_Is_Selected_When_Supported()
    {
        var calls = new List<(DXGI_FORMAT, bool)>();

        var capabilities = D3D12DrawingSurfaceProbe.SelectCapabilities((format, direct) => calls.Add((format, direct)));

        Assert.Equal(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, capabilities.Format);
        Assert.True(capabilities.DirectRendering);
        Assert.Equal([(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, true)], calls);
    }

    [Fact]
    public void Rgba8_Copy_Is_Selected_When_Direct_Rendering_Is_Unsupported()
    {
        var calls = new List<(DXGI_FORMAT, bool)>();

        var capabilities = D3D12DrawingSurfaceProbe.SelectCapabilities((format, direct) =>
        {
            calls.Add((format, direct));
            if (direct)
                throw new COMException("", E_INVALIDARG);
        });

        Assert.Equal(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, capabilities.Format);
        Assert.False(capabilities.DirectRendering);
        Assert.Equal(
            [
                (DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, true),
                (DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, false)
            ],
            calls);
    }

    [Fact]
    public void Bgra8_Copy_Is_Selected_When_Rgba8_Is_Unsupported()
    {
        var calls = new List<(DXGI_FORMAT, bool)>();

        var capabilities = D3D12DrawingSurfaceProbe.SelectCapabilities((format, direct) =>
        {
            calls.Add((format, direct));
            if (format == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM)
                throw new COMException("", E_INVALIDARG);
        });

        Assert.Equal(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, capabilities.Format);
        Assert.False(capabilities.DirectRendering);
        Assert.Equal(
            [
                (DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, true),
                (DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, false),
                (DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, false)
            ],
            calls);
    }

    [Fact]
    public void Bgra8_Failure_Is_Reported()
    {
        var exception = Assert.Throws<COMException>(() =>
            D3D12DrawingSurfaceProbe.SelectCapabilities((_, _) => throw new COMException("", E_INVALIDARG)));

        Assert.Equal(E_INVALIDARG, exception.HResult);
    }

    [Theory]
    [InlineData(E_INVALIDARG)]
    [InlineData(E_NOINTERFACE)]
    [InlineData(E_NOTIMPL)]
    [InlineData(DXGI_ERROR_UNSUPPORTED)]
    public void Unsupported_Results_Continue_Probing(int hresult)
    {
        var calls = 0;

        var capabilities = D3D12DrawingSurfaceProbe.SelectCapabilities((_, direct) =>
        {
            calls++;
            if (direct)
                throw new COMException("", hresult);
        });

        Assert.False(capabilities.DirectRendering);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(E_FAIL)]
    [InlineData(unchecked((int)0x887A0005))] // DXGI_ERROR_DEVICE_REMOVED
    public void Other_Failures_Stop_Probing(int hresult)
    {
        var calls = 0;

        var exception = Assert.Throws<COMException>(() =>
            D3D12DrawingSurfaceProbe.SelectCapabilities((_, _) =>
            {
                calls++;
                throw new COMException("", hresult);
            }));

        Assert.Equal(hresult, exception.HResult);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Non_Com_Failures_Stop_Probing()
    {
        var calls = 0;

        Assert.Throws<InvalidOperationException>(() =>
            D3D12DrawingSurfaceProbe.SelectCapabilities((_, _) =>
            {
                calls++;
                throw new InvalidOperationException();
            }));

        Assert.Equal(1, calls);
    }
}
