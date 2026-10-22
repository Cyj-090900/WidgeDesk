using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WidgeDesk.Core.Models.Settings;
using WidgeDesk.UI.Theming;

namespace WidgeDesk.Dock;

public partial class DockWindow
{
    private DockRenderLoop? _entryHoverLoop;
    private readonly ScaleTransform _launcherHoverScale = new(1, 1);
    private readonly ScaleTransform _thisPcHoverScale = new(1, 1);
    private readonly ScaleTransform _desktopIconsHoverScale = new(1, 1);
    private readonly ScaleTransform _settingsHoverScale = new(1, 1);
    private bool _launcherHovered, _thisPcHovered, _desktopIconsHovered, _settingsHovered;

    private void InitializeEntryHover()
    {
        LaunchpadTile.RenderTransformOrigin = ThisPcTile.RenderTransformOrigin
            = DesktopIconsTile.RenderTransformOrigin = SettingsTile.RenderTransformOrigin = new Point(.5, .5);
        LaunchpadTile.RenderTransform = _launcherHoverScale;
        ThisPcTile.RenderTransform = _thisPcHoverScale;
        DesktopIconsTile.RenderTransform = _desktopIconsHoverScale;
        SettingsTile.RenderTransform = _settingsHoverScale;
        _entryHoverLoop = new DockRenderLoop(AnimateEntryHoverFrame);
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) ResetEntryHover(); };
        Closed += (_, _) => ResetEntryHover();
    }

    private void OnEntryMouseEnter(object sender, MouseEventArgs e) => SetEntryHovered(sender, true);
    private void OnEntryMouseLeave(object sender, MouseEventArgs e) => SetEntryHovered(sender, false);

    private void SetEntryHovered(object sender, bool hovered)
    {
        if (ReferenceEquals(sender, LauncherButton)) _launcherHovered = hovered;
        else if (ReferenceEquals(sender, ThisPcButton)) _thisPcHovered = hovered;
        else if (ReferenceEquals(sender, DesktopIconsButton)) _desktopIconsHovered = hovered;
        else if (ReferenceEquals(sender, SettingsButton)) _settingsHovered = hovered;
        if (IsVisible && !_performanceSleepRequested) _entryHoverLoop?.Start();
    }

    private bool AnimateEntryHoverFrame(double deltaSeconds)
    {
        var launcherTarget = _launcherHovered ? DockSettings.FixedMagnificationPeak : 1;
        var thisPcTarget = _thisPcHovered ? DockSettings.FixedMagnificationPeak : 1;
        var desktopIconsTarget = _desktopIconsHovered ? DockSettings.FixedMagnificationPeak : 1;
        var settingsTarget = _settingsHovered ? DockSettings.FixedMagnificationPeak : 1;
        if (ThemeService.Current?.AnimationsEnabled == false)
        {
            _launcherHoverScale.ScaleX = _launcherHoverScale.ScaleY = launcherTarget;
            _thisPcHoverScale.ScaleX = _thisPcHoverScale.ScaleY = thisPcTarget;
            _desktopIconsHoverScale.ScaleX = _desktopIconsHoverScale.ScaleY = desktopIconsTarget;
            _settingsHoverScale.ScaleX = _settingsHoverScale.ScaleY = settingsTarget;
            return true;
        }
        var launcherScale = DockHoverScale.Advance(_launcherHoverScale.ScaleX, launcherTarget, deltaSeconds);
        var thisPcScale = DockHoverScale.Advance(_thisPcHoverScale.ScaleX, thisPcTarget, deltaSeconds);
        var desktopIconsScale = DockHoverScale.Advance(_desktopIconsHoverScale.ScaleX, desktopIconsTarget, deltaSeconds);
        var settingsScale = DockHoverScale.Advance(_settingsHoverScale.ScaleX, settingsTarget, deltaSeconds);
        _launcherHoverScale.ScaleX = _launcherHoverScale.ScaleY = launcherScale;
        _thisPcHoverScale.ScaleX = _thisPcHoverScale.ScaleY = thisPcScale;
        _desktopIconsHoverScale.ScaleX = _desktopIconsHoverScale.ScaleY = desktopIconsScale;
        _settingsHoverScale.ScaleX = _settingsHoverScale.ScaleY = settingsScale;
        return launcherScale == launcherTarget && thisPcScale == thisPcTarget
            && desktopIconsScale == desktopIconsTarget && settingsScale == settingsTarget;
    }

    private void ResetEntryHover()
    {
        _entryHoverLoop?.Stop();
        _launcherHovered = _thisPcHovered = _desktopIconsHovered = _settingsHovered = false;
        _launcherHoverScale.ScaleX = _launcherHoverScale.ScaleY = 1;
        _thisPcHoverScale.ScaleX = _thisPcHoverScale.ScaleY = 1;
        _desktopIconsHoverScale.ScaleX = _desktopIconsHoverScale.ScaleY = 1;
        _settingsHoverScale.ScaleX = _settingsHoverScale.ScaleY = 1;
    }
}
