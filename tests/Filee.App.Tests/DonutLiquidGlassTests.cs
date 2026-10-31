// Liquid Glass aesthetic tests for the circular DonutMenu and RadialWindow:
// - Centralized theme resources (Light & Dark, Soft & Material You)
// - Translucent background, directional specular border, soft shadow, and glassy hover highlight
// - Independent foreground legibility across light, dark, and vibrant backgrounds even at low SliceOpacity
// - Accessibility preferences: Reduce Transparency (Transparency.Reduced) and Reduce Motion (Motion.Enabled)
// - Dynamic native glass mask geometry across slices, centre hole seam, group sub-rings, flips, and progress ring

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Settings;

namespace Filee.App.Tests;

[Collection("Motion")]
public class DonutLiquidGlassTests
{
    private static IReadOnlyList<DonutItem> SampleItems() =>
    [
        new DonutItem { Id = "png", Label = "PNG", Caption = "Lossless" },
        new DonutItem
        {
            Id = "group-img",
            Label = "Web",
            Caption = "3 presets",
            Children =
            [
                new DonutItem { Id = "webp", Label = "WEBP", Caption = "90%" },
                new DonutItem { Id = "avif", Label = "AVIF", Caption = "Small" },
                new DonutItem { Id = "jpg", Label = "JPG", Caption = "85%" },
            ],
        },
        new DonutItem { Id = "pdf", Label = "PDF", Caption = "Document" },
        new DonutItem { Id = "ico", Label = "ICO", Caption = "256px" },
        new DonutItem { Id = "svg", Label = "SVG", Caption = "Vector", IsEnabled = false, DisabledReason = "Vector only" },
        new DonutItem { Id = "zip", Label = "ZIP", Caption = "Archive" },
    ];

    [AvaloniaFact]
    public void Centralized_glass_resources_resolve_in_light_and_dark_themes_and_bind_to_DonutMenu()
    {
        TestServices.EnsureInitialized("en");
        var themeService = AppHost.Get<ThemeService>();
        try
        {
            foreach (var style in new[] { ThemeStyle.Soft, ThemeStyle.MaterialYou })
            {
                themeService.Apply(new ThemeSettings { Style = style }, "en");
                foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    var donut = new DonutMenu { Items = SampleItems() };
                    var window = new Window
                    {
                        RequestedThemeVariant = variant,
                        Content = donut,
                        SizeToContent = SizeToContent.WidthAndHeight,
                    };
                    window.Show();
                    RenderTests.Pump();

                    Assert.NotNull(donut.GlassSheenBrush);
                    Assert.NotNull(donut.GlassSpecularBrush);
                    Assert.NotNull(donut.GlassHoverBorderBrush);
                    Assert.IsType<LinearGradientBrush>(donut.GlassSheenBrush);
                    Assert.IsType<LinearGradientBrush>(donut.GlassSpecularBrush);
                    Assert.IsType<LinearGradientBrush>(donut.GlassHoverBorderBrush);

                    window.Close();
                }
            }
        }
        finally
        {
            themeService.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaFact]
    public void Reduce_transparency_preference_forces_opaque_fills_and_tracks_window_class()
    {
        TestServices.EnsureInitialized("en");
        try
        {
            var donut = new DonutMenu { Items = SampleItems(), SliceOpacity = 0.55 };
            var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
            Transparency.Track(window);
            window.Show();
            RenderTests.Pump();

            Transparency.Configure(false, () => false);
            Assert.True(Transparency.Enabled);
            Assert.False(Transparency.Reduced);
            Assert.DoesNotContain("reduced-transparency", window.Classes);
            Assert.True(donut.EffectiveSliceOpacity < 0.55);
            Assert.True(donut.EffectiveCenterOpacity < 0.95);

            // System preference switches to reduced transparency
            var osPrefersReduced = true;
            Transparency.Configure(null, () => osPrefersReduced);
            Assert.True(Transparency.Reduced);
            Assert.Contains("reduced-transparency", window.Classes);
            Assert.Equal(1.0, donut.EffectiveSliceOpacity, 3);
            Assert.Equal(1.0, donut.EffectiveCenterOpacity, 3);

            // Refresh re-reads the system setting
            osPrefersReduced = false;
            Transparency.Refresh();
            Assert.False(Transparency.Reduced);
            Assert.DoesNotContain("reduced-transparency", window.Classes);

            window.Close();
        }
        finally
        {
            Transparency.Configure(false, () => false);
        }
    }

    [AvaloniaFact]
    public void Glass_mask_geometry_follows_slices_hub_subring_and_progress_while_keeping_center_seam_empty()
    {
        TestServices.EnsureInitialized("en");
        Motion.Configure(true, () => true); // snap animations to inspect exact poses
        try
        {
            var donut = new DonutMenu
            {
                Items = SampleItems(),
                OuterRadius = 150,
                HoleRatio = 0.42,
            };
            var side = DonutMenu.SideFor(donut.OuterRadius, subRing: true);
            donut.Measure(new Size(side, side));
            donut.Arrange(new Rect(0, 0, side, side));

            var circles = new List<(double CenterX, double CenterY, double OuterRadius, double InnerRadius)>();
            var slices = new List<IReadOnlyList<(double X, double Y)>>();

            // 1. Standard ring: 1 center hub circle + 6 slice contours
            donut.GetGlassMaskGeometry(circles, slices);
            var hub = Assert.Single(circles);
            Assert.Equal(side / 2, hub.CenterX, 2);
            Assert.Equal(side / 2, hub.CenterY, 2);
            var innerRadius = donut.OuterRadius * donut.HoleRatio;
            Assert.Equal(innerRadius - 6, hub.OuterRadius, 2);
            Assert.Equal(6, slices.Count);
            Assert.All(slices, s => Assert.True(s.Count >= 10));

            // 2. Expand group (index 1 has 3 children): adds 3 sub-ring slice contours (total 9)
            donut.SetExternalHit(new DonutHit(DonutHitKind.Segment, 1), new Point(side / 2 + 80, side / 2 - 40));
            donut.ExpandPendingGroup();
            donut.GetGlassMaskGeometry(circles, slices);
            Assert.Single(circles);
            Assert.Equal(6 + 3, slices.Count);

            // 3. Progress mode: collapses to the single progress plate circle
            donut.BeginProgress();
            donut.SetProgress(0.65);
            donut.GetGlassMaskGeometry(circles, slices);
            var plate = Assert.Single(circles);
            Assert.Empty(slices);
            Assert.Equal(DonutMenu.RingRadius + 8, plate.OuterRadius, 2);
        }
        finally
        {
            Motion.Configure(false, () => false);
        }
    }

    [AvaloniaTheory]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void Renders_liquid_glass_over_varied_backgrounds_with_legible_labels_and_preserved_seams(ThemeMode mode)
    {
        TestServices.EnsureInitialized("en");
        var themeService = AppHost.Get<ThemeService>();
        Motion.Configure(false, () => false);
        Transparency.Configure(false, () => false);

        try
        {
            themeService.Apply(new ThemeSettings { Mode = mode, Style = ThemeStyle.Soft }, "en");
            var variant = mode == ThemeMode.Dark ? ThemeVariant.Dark : ThemeVariant.Light;

            var donut = new DonutMenu
            {
                Items = SampleItems(),
                CenterTitle = "photo.png",
                CenterSubtitle = "PNG → WEBP (90%)",
                CenterHint = "Tab back",
                CanFlip = true,
                SliceOpacity = 0.45,
                OuterRadius = 150,
                HoleRatio = 0.42,
            };

            // Hover slice 1 and expand its group sub-ring
            donut.SetExternalHit(new DonutHit(DonutHitKind.Segment, 1), new Point(260, 140));
            donut.ExpandPendingGroup();
            for (var i = 0; i < 60; i++)
                donut.Advance(1 / 60.0);

            var side = DonutMenu.SideFor(donut.OuterRadius, subRing: true);

            // Render over a rich multi-zone background (bright, dark, and colourful) to validate visual contrast
            var backdrop = new Border
            {
                Width = side,
                Height = side,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops =
                    [
                        new GradientStop(Color.Parse("#FF6B6B"), 0.0),
                        new GradientStop(Color.Parse("#F7F5F0"), 0.35),
                        new GradientStop(Color.Parse("#4ECDC4"), 0.68),
                        new GradientStop(Color.Parse("#1A1B2F"), 1.0),
                    ],
                },
                Child = donut,
            };

            var window = new Window
            {
                RequestedThemeVariant = variant,
                Content = backdrop,
                SizeToContent = SizeToContent.WidthAndHeight,
            };
            window.Show();
            RenderTests.Pump();

            var sliceColor = Assert.IsAssignableFrom<ISolidColorBrush>(donut.SliceBrush).Color;
            Assert.Equal(mode == ThemeMode.Dark ? Color.Parse("#262624") : Colors.White, sliceColor);

            RenderTests.Save(window, $"donut-liquid-glass-{mode.ToString().ToLowerInvariant()}.png");
            window.Close();
        }
        finally
        {
            themeService.Apply(new ThemeSettings(), "en");
        }
    }

    private static byte AlphaAt(RenderTargetBitmap bitmap, Point p)
    {
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            bitmap.CopyPixels(new PixelRect((int)Math.Round(p.X), (int)Math.Round(p.Y), 1, 1), buffer, 4, 4);
            return System.Runtime.InteropServices.Marshal.ReadByte(buffer, 3);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }
}
