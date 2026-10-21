using WidgeDesktop.Core.Storage;

namespace WidgeDesktop.Core.Tests;

public sealed class DataDirectoryGuardTests
{
    [Fact]
    public void DetectsSameDirectory()
    {
        var protectedPath = Path.Combine(Path.GetTempPath(), "WidgeDesktop", "user-data");
        Assert.True(DataDirectoryGuard.Overlaps(protectedPath, protectedPath));
    }

    [Fact]
    public void DetectsChildInEitherArgumentOrder()
    {
        var protectedPath = Path.Combine(Path.GetTempPath(), "WidgeDesktop", "user-data");
        Assert.True(DataDirectoryGuard.Overlaps(Path.Combine(protectedPath, "tests"), protectedPath));
        Assert.True(DataDirectoryGuard.Overlaps(Path.GetDirectoryName(protectedPath)!, protectedPath));
    }

    [Fact]
    public void DoesNotTreatSiblingAsOverlap()
    {
        var protectedPath = Path.Combine(Path.GetTempPath(), "WidgeDesktop", "user-data");
        Assert.False(DataDirectoryGuard.Overlaps(protectedPath + "-test", protectedPath));
    }
}
