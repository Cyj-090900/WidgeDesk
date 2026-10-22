using System.Reflection;
using WidgeDesk.Platform;

namespace WidgeDesk.Dock.Tests;

public sealed class ProductPlatformTests
{
    [Theory]
    // Windows 10 2004 is the floor: below it there is no WDA_EXCLUDEFROMCAPTURE.
    [InlineData(18362, false)]
    [InlineData(19041, true)]
    // Windows 10 22H2, the build the compatible capture path targets.
    [InlineData(19045, true)]
    // Windows 11 keeps working, with the richer capabilities layered on top.
    [InlineData(21999, true)]
    [InlineData(22000, true)]
    [InlineData(26300, true)]
    [InlineData(40000, true)]
    public void ProductFloorHasNoUpperBuildLimit(int build, bool supported)
    {
        var method = typeof(WindowsCapabilities).GetMethod("IsProductBuildSupported", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(supported, method.Invoke(null, [build]));
    }
}
