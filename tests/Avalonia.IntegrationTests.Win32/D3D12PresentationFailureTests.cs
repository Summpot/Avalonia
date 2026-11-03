using System;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia.Win32.Direct3D12;
using Xunit;

namespace Avalonia.IntegrationTests.Win32;

public class D3D12PresentationFailureTests
{
    [Fact]
    public void Ordinary_Failure_Corrupts_Only_The_Target()
    {
        var expected = new COMException("Presentation failed.", unchecked((int)0x80004005));
        var failure = new D3D12PresentationFailure();
        failure.Capture(expected);
        var corrupted = false;
        var lost = false;

        var actual = Assert.Throws<COMException>(() => failure.Throw(() => corrupted = true, () => lost = true));

        Assert.Same(expected, actual);
        Assert.True(corrupted);
        Assert.False(lost);
    }

    [Theory]
    [InlineData(unchecked((int)0x887A0005))] // DXGI_ERROR_DEVICE_REMOVED
    [InlineData(unchecked((int)0x887A0006))] // DXGI_ERROR_DEVICE_HUNG
    [InlineData(unchecked((int)0x887A0007))] // DXGI_ERROR_DEVICE_RESET
    public void Device_Loss_Invalidates_The_Target_And_Device(int result)
    {
        var failure = new D3D12PresentationFailure();
        var expected = Assert.Throws<PlatformGraphicsContextLostException>(() =>
            D3D12PresentationFailure.ThrowIfFailed(result));
        failure.Capture(expected);
        var corrupted = false;
        var lost = false;

        var actual = Assert.Throws<PlatformGraphicsContextLostException>(() =>
            failure.Throw(() => corrupted = true, () => lost = true));

        Assert.Same(expected, actual);
        Assert.True(corrupted);
        Assert.True(lost);
    }

    [Fact]
    public void Cleanup_Device_Loss_Does_Not_Hide_The_First_Failure()
    {
        var expected = new InvalidOperationException("Frame submission failed.");
        var failure = new D3D12PresentationFailure();
        failure.Capture(expected);
        failure.Capture(new COMException("Device removed during cleanup.", unchecked((int)0x887A0005)));
        var lost = false;

        var actual = Assert.Throws<InvalidOperationException>(() =>
            failure.Throw(() => { }, () => lost = true));

        Assert.Same(expected, actual);
        Assert.True(lost);
    }

    [Fact]
    public void Cleanup_Failure_Does_Not_Hide_The_First_Failure()
    {
        var expected = new InvalidOperationException("Frame submission failed.");
        var failure = new D3D12PresentationFailure();
        failure.Capture(expected);
        failure.Capture(new COMException("Commit failed.", unchecked((int)0x80004005)));
        var lost = false;

        var actual = Assert.Throws<InvalidOperationException>(() =>
            failure.Throw(() => { }, () => lost = true));

        Assert.Same(expected, actual);
        Assert.False(lost);
    }

    [Fact]
    public void Temporary_Unavailability_Does_Not_Lose_The_Device()
    {
        var failure = new D3D12PresentationFailure();
        failure.Capture(Assert.Throws<COMException>(() =>
            D3D12PresentationFailure.ThrowIfFailed(unchecked((int)0x887A0022))));
        var lost = false;

        Assert.Throws<COMException>(() => failure.Throw(() => { }, () => lost = true));

        Assert.False(lost);
    }
}
