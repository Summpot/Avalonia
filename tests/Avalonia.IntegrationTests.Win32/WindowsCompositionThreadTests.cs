using System;
using System.Threading;
using Avalonia.Win32;
using Xunit;

namespace Avalonia.IntegrationTests.Win32;

public class WindowsCompositionThreadTests
{
    [Fact]
    public void Stop_Waits_For_Cleanup_On_The_Owning_Thread()
    {
        Thread? owner = null;
        Thread? cleanupThread = null;
        var cleanupCount = 0;
        var lifetime = WindowsCompositionThread.Start(
            thread =>
            {
                owner = Thread.CurrentThread;
                return thread;
            },
            thread => thread.CancellationToken.WaitHandle.WaitOne(),
            _ =>
            {
                cleanupThread = Thread.CurrentThread;
                cleanupCount++;
            },
            _ => { });

        lifetime.Stop();
        lifetime.Stop();

        Assert.NotNull(owner);
        Assert.False(owner.IsAlive);
        Assert.Same(owner, cleanupThread);
        Assert.Equal(1, cleanupCount);
    }

    [Fact]
    public void Creation_Failure_Releases_Partial_Resources_And_Exits_The_Thread()
    {
        var expected = new InvalidOperationException("Connection initialization failed.");
        Thread? owner = null;
        var released = false;

        var actual = Assert.Throws<InvalidOperationException>(() => WindowsCompositionThread.Start<object>(
            _ =>
            {
                owner = Thread.CurrentThread;
                throw expected;
            },
            _ => Assert.Fail("A failed connection cannot enter its render loop."),
            connection =>
            {
                Assert.Null(connection);
                released = true;
            },
            _ => { }));

        Assert.Same(expected, actual);
        Assert.True(released);
        Assert.NotNull(owner);
        Assert.False(owner.IsAlive);
    }

    [Fact]
    public void Cleanup_Failure_Preserves_The_Initialization_Exception()
    {
        var expected = new InvalidOperationException("Connection initialization failed.");

        var actual = Assert.Throws<InvalidOperationException>(() => WindowsCompositionThread.Start<object>(
            _ => throw expected,
            _ => { },
            _ => throw new Exception("Resource cleanup failed."),
            _ => { }));

        Assert.Same(expected, actual);
    }
}
