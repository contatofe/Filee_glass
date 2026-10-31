using Filee.Platform.MacOS;

namespace Filee.Platform.Tests;

public sealed class MacOSNativeTests
{
    [Fact]
    public void NativeFrameworksAndPermissionProbeLoadOnMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires macOS frameworks.");
            return;
        }

        var platform = new MacOSPlatformServices();
        _ = platform.HasAccessibilityPermission;
        _ = platform.PrefersReducedMotion;
        _ = platform.PrefersReducedTransparency;
        _ = platform.ProcessNameAt(0, 0);
        Assert.Equal(4, platform.SystemDragThreshold);
    }

    [Fact]
    public void GlassBackdropCreatesLiquidGlassOrFallbackWithShapeMaskAndPassthrough()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires macOS frameworks.");
            return;
        }

        Assert.True(MacOSGlassBackdrop.IsVisualEffectAvailable);
        Assert.NotEqual(MacOSGlassMaterialKind.None, MacOSGlassBackdrop.PreferredMaterialKind);

        foreach (var kind in new[] { MacOSGlassBackdrop.PreferredMaterialKind, MacOSGlassMaterialKind.VisualEffectFallback })
        {
            using var backdrop = MacOSGlassBackdrop.CreateDetached(320, 320, forceKind: kind);
            Assert.NotNull(backdrop);
            Assert.Equal(kind, backdrop.ActiveKind);
            Assert.NotEqual(0, backdrop.HostViewHandle);
            Assert.NotEqual(0, backdrop.MaterialViewHandle);
            Assert.NotEqual(0, backdrop.MaskLayerHandle);
            Assert.True(backdrop.IsHidden);

            // hitTest: must always return 0 (nil) so clicks and drags pass straight to Avalonia's AvnView.
            Assert.Equal(0, backdrop.HitTestHostView(160, 160));
            Assert.Equal(0, backdrop.HitTestHostView(100, 40));

            // Mask with a center hub (radius 50) and a top wedge (around (160, 50)), leaving the seam at radius 56 empty.
            GlassCircle[] circles = [new GlassCircle(160, 160, 50)];
            GlassSliceContour[] slices =
            [
                new GlassSliceContour([(130, 30), (190, 30), (180, 90), (140, 90)]),
            ];

            backdrop.Update(
                width: 320,
                height: 320,
                visible: true,
                isDark: true,
                reducedTransparency: false,
                effectiveOpacity: 0.78,
                circles: circles,
                slices: slices);

            Assert.False(backdrop.IsHidden);
            Assert.Equal(2, backdrop.MaskedContourCount);

            // Center hub is inside the mask
            Assert.True(backdrop.MaskContainsPoint(160, 160, 320));
            // Top wedge is inside the mask
            Assert.True(backdrop.MaskContainsPoint(160, 60, 320));
            // Seam between hub (r=50) and wedge (y=90 -> r=70), at (160, 102) -> r=58, is outside the mask!
            Assert.False(backdrop.MaskContainsPoint(160, 102, 320));
            // Corner of the bounding box (10, 10) is outside the mask (no blurred rectangle!)
            Assert.False(backdrop.MaskContainsPoint(10, 10, 320));

            // Switching to Light appearance and then Reduce Transparency hides the backdrop cleanly
            backdrop.Update(320, 320, visible: true, isDark: false, reducedTransparency: true, effectiveOpacity: 0.78, circles, slices);
            Assert.True(backdrop.IsHidden);
        }
    }
}
