using Avalonia.Win32.DComposition;
using Avalonia.Win32.WinRT.Composition;
using Xunit;

namespace Avalonia.IntegrationTests.Win32;

public class CompositionConnectionLifecycleTests
{
    [Fact]
    public void WinUi_Connections_Can_Be_Created_And_Stopped_Repeatedly()
    {
        Assert.SkipWhen(!WinUiCompositorConnection.IsSupported(), "WinUI composition is unavailable.");
        for (var i = 0; i < 3; i++)
        {
            var connection = WinUiCompositorConnection.TryCreate();
            Assert.NotNull(connection);
            connection.Stop();
        }
    }

    [Fact]
    public void DirectComposition_Connections_Can_Be_Created_And_Stopped_Repeatedly()
    {
        Assert.SkipWhen(!DirectCompositionConnection.IsSupported(), "DirectComposition is unavailable.");
        for (var i = 0; i < 3; i++)
        {
            var connection = DirectCompositionConnection.TryCreate();
            Assert.NotNull(connection);
            connection.Stop();
        }
    }
}
