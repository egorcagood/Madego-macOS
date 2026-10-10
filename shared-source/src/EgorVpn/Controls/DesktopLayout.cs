using System;

namespace EgorVpn.Controls;

// WPF uses device-independent pixels. No network or persisted state is involved.
public static class DesktopLayout
{
    public const double CompactSidebarThreshold = 1180;
    public const double ExpandedSidebarWidth = 256;
    public const double CompactSidebarWidth = 72;
    public const double SideBySideThreshold = 960;
    public const double ServerPanelWidth = 368;
    public const double PanelGap = 32;

    public readonly record struct HomeLayout(bool Stacked, bool HeroStacked, bool DetailsStacked, double OverviewWidth);
    public readonly record struct WindowBounds(double Width, double Height, double MinWidth, double MinHeight);

    public static HomeLayout ForHome(double width, double height = double.PositiveInfinity)
    {
        width = SafeExtent(width, 960);
        bool stacked = width < SideBySideThreshold || (double.IsFinite(height) && height > 0 && height < 480);
        double overview = Math.Max(0, width - (stacked ? 0 : ServerPanelWidth + PanelGap));
        return new(stacked, overview < 610, overview < 650, overview);
    }

    public static WindowBounds ForWorkArea(double width, double height)
    {
        double availableWidth = Math.Max(1, SafeExtent(width, 1380) - 24);
        double availableHeight = Math.Max(1, SafeExtent(height, 900) - 24);
        return new(Math.Min(1380, availableWidth), Math.Min(900, availableHeight),
            Math.Min(760, availableWidth), Math.Min(520, availableHeight));
    }

    private static double SafeExtent(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? value : fallback;
}
