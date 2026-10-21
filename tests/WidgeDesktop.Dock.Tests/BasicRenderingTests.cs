using System.Reflection;
using WidgeDesktop.Platform.Displays;
using WidgeDesktop.UI.Controls;

namespace WidgeDesktop.Dock.Tests;

public sealed class BasicRenderingTests
{
    [Fact]
    public void RendererFailureProvidesReadablePixelsWithoutWaitingForCapture()
    {
        var method = typeof(WidgeDesktop.UI.BackdropRecovery).GetMethod("UseBasicRendering");
        Assert.NotNull(method);
        try
        {
            method.Invoke(null, null);
            using var background = new GlassBackgroundLayer();
            var result = background.Capture(new PixelRect(0, 0, 40, 20), 40, 20, 5, true);
            Assert.NotNull(result); Assert.Equal(GlassBackdropSource.Basic, result.Source);
            Assert.True(background.RequiresBasicBackground);
            Assert.Equal(255, result.Pixels[3]); Assert.True(result.Pixels[0] > 0);
        }
        finally { WidgeDesktop.UI.BackdropRecovery.RetryBackground(); }
    }
}
