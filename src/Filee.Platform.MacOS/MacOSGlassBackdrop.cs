using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Filee.Platform.MacOS;

/// <summary>Which native macOS glass/blur material backs the radial window.</summary>
public enum MacOSGlassMaterialKind
{
    /// <summary>No native glass material available (or disabled).</summary>
    None = 0,

    /// <summary>macOS 10.14–15 <c>NSVisualEffectView</c> behind-window blur fallback.</summary>
    VisualEffectFallback = 1,

    /// <summary>macOS 26+ <c>NSGlassEffectView</c> Liquid Glass material backed by behind-window blur capture.</summary>
    LiquidGlass = 2,
}

/// <summary>A closed polygon contour in top-left control coordinates used to mask the native glass backdrop.</summary>
public readonly record struct GlassSliceContour(IReadOnlyList<(double X, double Y)> Points);

/// <summary>A circle or ring in top-left control coordinates used to mask the native glass backdrop.</summary>
public readonly record struct GlassCircle(double CenterX, double CenterY, double OuterRadius, double InnerRadius = 0);

/// <summary>
/// Manages a pass-through native macOS Liquid Glass backdrop attached to <c>AutoFitContentView</c> directly behind
/// Avalonia's <c>AvnView</c>. Combines <c>NSVisualEffectView</c> (<c>BehindWindow</c> + <c>setMaskImage:</c>, which
/// registers the window with WindowServer for behind-window desktop/app blur) with macOS 26+ <c>NSGlassEffectView</c>
/// (<c>_setPath:</c> SDF refractive lens), shaped to the exact geometry of the donut slices, centre hub, and sub-rings.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOSGlassBackdrop : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string QuartzCore = "/System/Library/Frameworks/QuartzCore.framework/QuartzCore";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private static readonly nint QuartzCoreLib = NativeLibrary.Load(QuartzCore);
    private static readonly nint AppKitLib = NativeLibrary.Load(AppKit);

    static MacOSGlassBackdrop()
    {
        _ = QuartzCoreLib;
        _ = AppKitLib;
    }

    private static readonly object ClassLock = new();
    private static nint _hostViewClass;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint HitTestDelegate(nint self, nint cmd, CGPoint point);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ReturnsNoDelegate(nint self, nint cmd);

    // Unmanaged callbacks kept alive for the lifetime of the process.
    private static readonly HitTestDelegate HitTestCallback = static (_, _, _) => 0;
    private static readonly ReturnsNoDelegate ReturnsNoCallback = static (_, _) => 0;

    /// <summary>True when macOS 26+ <c>NSGlassEffectView</c> is present in AppKit.</summary>
    public static bool IsLiquidGlassAvailable => objc_getClass("NSGlassEffectView") != 0;

    /// <summary>True when macOS 26+ <c>NSGlassEffectContainerView</c> is present in AppKit.</summary>
    public static bool IsGlassContainerAvailable => objc_getClass("NSGlassEffectContainerView") != 0;

    /// <summary>True when <c>NSVisualEffectView</c> is present in AppKit.</summary>
    public static bool IsVisualEffectAvailable => objc_getClass("NSVisualEffectView") != 0;

    /// <summary>The best native glass material supported by the current macOS system.</summary>
    public static MacOSGlassMaterialKind PreferredMaterialKind =>
        IsLiquidGlassAvailable ? MacOSGlassMaterialKind.LiquidGlass
        : IsVisualEffectAvailable ? MacOSGlassMaterialKind.VisualEffectFallback
        : MacOSGlassMaterialKind.None;

    public MacOSGlassMaterialKind ActiveKind { get; }
    public nint HostViewHandle { get; private set; }
    public nint MaterialViewHandle { get; private set; }
    public nint MaskLayerHandle { get; private set; }
    public bool IsHidden { get; private set; } = true;
    public int MaskedContourCount { get; private set; }

    private nint _currentPath;
    private bool _disposed;
    private bool? _lastIsDark;

    private MacOSGlassBackdrop(
        MacOSGlassMaterialKind kind,
        nint hostView,
        nint materialView,
        nint maskLayer)
    {
        ActiveKind = kind;
        HostViewHandle = hostView;
        MaterialViewHandle = materialView;
        MaskLayerHandle = maskLayer;
    }

    /// <summary>
    /// Attaches a pass-through glass backdrop view to <paramref name="nsWindow"/> inside Avalonia's
    /// <c>AutoFitContentView</c> (<c>contentView</c>) directly below <c>AvnView</c>.
    /// </summary>
    public static MacOSGlassBackdrop? TryAttach(nint nsWindow, MacOSGlassMaterialKind? forceKind = null)
    {
        if (nsWindow == 0)
            return null;

        using var pool = new AutoreleasePool();
        var contentView = Send(nsWindow, Sel("contentView"));
        if (contentView == 0)
            return null;

        var bounds = SendRectReturn(contentView, Sel("bounds"));
        var width = Math.Max(1, bounds.Width);
        var height = Math.Max(1, bounds.Height);

        var backdrop = CreateCore(width, height, forceKind);
        if (backdrop is null)
            return null;

        // Ensure the NSWindow itself is non-opaque, clear, and does not draw a rectangular window shadow.
        SendBoolArg(nsWindow, Sel("setOpaque:"), false);
        SendBoolArg(nsWindow, Sel("setHasShadow:"), false);
        var clearColor = Send(objc_getClass("NSColor"), Sel("clearColor"));
        if (clearColor != 0)
            SendVoid(nsWindow, Sel("setBackgroundColor:"), clearColor);

        // In Avalonia's libAvaloniaNative, nsWindow.contentView is AutoFitContentView (which has wantsLayer = YES
        // and contains AvnView as its top subview). Adding HostViewHandle to contentView with NSWindowBelow (-1)
        // places the native glass backdrop directly behind AvnView in the same layer tree.
        const nint NSWindowBelow = -1;
        SendBoolArg(contentView, Sel("setWantsLayer:"), true);
        SendAddSubview(contentView, Sel("addSubview:positioned:relativeTo:"), backdrop.HostViewHandle, NSWindowBelow, 0);

        return backdrop;
    }

    /// <summary>
    /// Creates a standalone glass backdrop hierarchy (for unit tests or off-window verification).
    /// </summary>
    public static MacOSGlassBackdrop? CreateDetached(double width, double height, MacOSGlassMaterialKind? forceKind = null)
    {
        using var pool = new AutoreleasePool();
        return CreateCore(Math.Max(1, width), Math.Max(1, height), forceKind);
    }

    private static MacOSGlassBackdrop? CreateCore(double width, double height, MacOSGlassMaterialKind? forceKind)
    {
        var kind = forceKind ?? PreferredMaterialKind;
        if (kind == MacOSGlassMaterialKind.LiquidGlass && !IsLiquidGlassAvailable)
            kind = IsVisualEffectAvailable ? MacOSGlassMaterialKind.VisualEffectFallback : MacOSGlassMaterialKind.None;
        else if (kind == MacOSGlassMaterialKind.VisualEffectFallback && !IsVisualEffectAvailable)
            kind = MacOSGlassMaterialKind.None;

        if (kind == MacOSGlassMaterialKind.None)
            return null;

        var hostClass = EnsureHostViewClass();
        if (hostClass == 0)
            return null;

        var frame = new CGRect(0, 0, width, height);
        var hostView = SendInitWithFrame(Send(hostClass, Sel("alloc")), Sel("initWithFrame:"), frame);
        if (hostView == 0)
            return null;

        // Do NOT use autoresizingMask on hostView or its subviews: RadialWindow's SizeToContent changes from the
        // sub-ring-reserved size (476x476) down to the fitted donut size (340x340), and AppKit's autoresizing
        // desynchronizes the subview origin from Donut.Bounds. Instead, Update() sets exact frames from Donut.Bounds.
        // Also do NOT set a layer.mask on hostView: an ancestor mask forces CoreAnimation offscreen group rendering,
        // which prevents CABackdropLayer from sampling the WindowServer behind-window buffer.
        SendInteger(hostView, Sel("setAutoresizingMask:"), 0);
        SendBoolArg(hostView, Sel("setWantsLayer:"), true);
        SendBoolArg(hostView, Sel("setHidden:"), true);

        // Single native material view inside hostView:
        // - On macOS 26+, use NSGlassEffectView (style = Clear, shaped via _setPath:, and with its internal
        //   CABackdropLayer upgraded to windowServerAware = true + NSCGSWindowBehindWindowCaptureBackdropGroup).
        // - On macOS 10.14–15, fall back to NSVisualEffectView (BehindWindow, Active, shaped via setMaskImage:).
        // Never instantiate both views simultaneously, so there is never a second ghost donut behind the primary one.
        nint materialView = 0;
        if (kind == MacOSGlassMaterialKind.LiquidGlass)
        {
            var glassClass = objc_getClass("NSGlassEffectView");
            var glassView = glassClass == 0 ? 0 : SendInitWithFrame(Send(glassClass, Sel("alloc")), Sel("initWithFrame:"), frame);
            if (glassView != 0)
            {
                // NSGlassEffectViewStyleClear = 1: clear Liquid Glass refractive SDF lens + backdrop blur
                if (Responds(glassView, Sel("setStyle:")))
                    SendInteger(glassView, Sel("setStyle:"), 1);
                SendInteger(glassView, Sel("setAutoresizingMask:"), 0);
                SendVoid(hostView, Sel("addSubview:"), glassView);
                materialView = glassView;
            }
            else
            {
                kind = IsVisualEffectAvailable ? MacOSGlassMaterialKind.VisualEffectFallback : MacOSGlassMaterialKind.None;
            }
        }

        if (kind == MacOSGlassMaterialKind.VisualEffectFallback)
        {
            var visualClass = objc_getClass("NSVisualEffectView");
            var visualEffectView = visualClass == 0 ? 0 : SendInitWithFrame(Send(visualClass, Sel("alloc")), Sel("initWithFrame:"), frame);
            if (visualEffectView != 0)
            {
                // NSVisualEffectMaterialMenu = 5, NSVisualEffectBlendingModeBehindWindow = 0, NSVisualEffectStateActive = 1
                SendInteger(visualEffectView, Sel("setMaterial:"), 5);
                SendInteger(visualEffectView, Sel("setBlendingMode:"), 0);
                SendInteger(visualEffectView, Sel("setState:"), 1);
                SendBoolArg(visualEffectView, Sel("setEmphasized:"), false);
                SendInteger(visualEffectView, Sel("setAutoresizingMask:"), 0);
                SendBoolArg(visualEffectView, Sel("setWantsLayer:"), true);
                SendVoid(hostView, Sel("addSubview:"), visualEffectView);
                materialView = visualEffectView;
            }
        }

        if (materialView == 0)
        {
            SendVoid(hostView, Sel("release"));
            return null;
        }

        // Retain a CAShapeLayer storing the CGPath for geometric inspection/tests without masking hostView's layer.
        var shapeLayerClass = objc_getClass("CAShapeLayer");
        var maskLayer = shapeLayerClass != 0 ? Send(shapeLayerClass, Sel("layer")) : 0;
        if (maskLayer != 0)
        {
            SendVoid(maskLayer, Sel("retain"));
            SendRect(maskLayer, Sel("setFrame:"), frame);
            var evenOdd = CFStringCreateWithCString(0, "even-odd", 0x08000100);
            if (evenOdd != 0)
            {
                SendVoid(maskLayer, Sel("setFillRule:"), evenOdd);
                CFRelease(evenOdd);
            }
        }

        return new MacOSGlassBackdrop(kind, hostView, materialView, maskLayer);
    }

    /// <summary>
    /// Updates the native glass view's visibility, appearance, and exact <c>CGPath</c> / <c>NSImage</c> shape mask.
    /// </summary>
    public void Update(
        double width,
        double height,
        bool visible,
        bool isDark,
        bool reducedTransparency,
        double effectiveOpacity,
        ReadOnlySpan<GlassCircle> circles,
        IReadOnlyList<GlassSliceContour> slices)
    {
        if (_disposed || HostViewHandle == 0 || MaterialViewHandle == 0)
            return;

        using var pool = new AutoreleasePool();

        // Hide the native glass backdrop when the donut is hidden, waiting for drag files (opacity 0),
        // when Reduce Transparency is active, or when there are no shapes to mask.
        if (!visible || reducedTransparency || effectiveOpacity <= 0.01 || (circles.Length == 0 && slices.Count == 0))
        {
            if (!IsHidden)
            {
                SendBoolArg(HostViewHandle, Sel("setHidden:"), true);
                IsHidden = true;
            }
            MaskedContourCount = 0;
            return;
        }

        var w = Math.Max(1, width);
        var h = Math.Max(1, height);
        var frame = new CGRect(0, 0, w, h);

        // If hostView is attached to AutoFitContentView (superview), align hostView's origin to AvnView's frame
        // inside AutoFitContentView so that even if RadialWindow resized from 476x476 down to 340x340,
        // the native glass matches AvnView pixel-for-pixel and never shifts to the top-right.
        var hostFrame = frame;
        var superview = Send(HostViewHandle, Sel("superview"));
        if (superview != 0)
        {
            var subviews = Send(superview, Sel("subviews"));
            var subCount = subviews == 0 ? 0 : Send(subviews, Sel("count"));
            for (nint i = 0; i < subCount; i++)
            {
                var sibling = Send(subviews, Sel("objectAtIndex:"), i);
                if (sibling != 0 && sibling != HostViewHandle)
                {
                    var siblingFrame = SendRectReturn(sibling, Sel("frame"));
                    if (siblingFrame.Width > 1 && siblingFrame.Height > 1)
                    {
                        hostFrame = new CGRect(siblingFrame.X, siblingFrame.Y, w, h);
                        break;
                    }
                }
            }
        }
        SendRect(HostViewHandle, Sel("setFrame:"), hostFrame);
        SendRect(MaterialViewHandle, Sel("setFrame:"), frame);

        if (_lastIsDark != isDark)
        {
            _lastIsDark = isDark;
            ApplyAppearance(isDark);
        }

        // Keep HostView alpha at 1.0 while the donut is open so CoreAnimation does not isolate CABackdropLayer
        // into an offscreen group (which would disable WindowServer behind-window blur sampling).
        var hostAlpha = effectiveOpacity >= 0.12 ? 1.0 : Math.Clamp(effectiveOpacity / 0.12, 0.0, 1.0);
        SendDouble(HostViewHandle, Sel("setAlphaValue:"), hostAlpha);

        // Build the Quartz CGPath (flipping Y from top-left Avalonia coords to bottom-left Quartz/AppKit coords).
        var path = CGPathCreateMutable();
        var count = 0;
        try
        {
            for (var i = 0; i < circles.Length; i++)
            {
                var c = circles[i];
                if (c.OuterRadius <= 0.5)
                    continue;
                var qy = h - c.CenterY;
                CGPathAddEllipseInRect(path, 0, new CGRect(
                    c.CenterX - c.OuterRadius,
                    qy - c.OuterRadius,
                    c.OuterRadius * 2,
                    c.OuterRadius * 2));
                if (c.InnerRadius > 0.5)
                {
                    CGPathAddEllipseInRect(path, 0, new CGRect(
                        c.CenterX - c.InnerRadius,
                        qy - c.InnerRadius,
                        c.InnerRadius * 2,
                        c.InnerRadius * 2));
                }
                count++;
            }

            for (var i = 0; i < slices.Count; i++)
            {
                var pts = slices[i].Points;
                if (pts is null || pts.Count < 3)
                    continue;
                CGPathMoveToPoint(path, 0, pts[0].X, h - pts[0].Y);
                for (var j = 1; j < pts.Count; j++)
                    CGPathAddLineToPoint(path, 0, pts[j].X, h - pts[j].Y);
                CGPathCloseSubpath(path);
                count++;
            }

            if (MaskLayerHandle != 0)
            {
                SendRect(MaskLayerHandle, Sel("setFrame:"), frame);
                SendVoid(MaskLayerHandle, Sel("setPath:"), path);
            }

            if (ActiveKind == MacOSGlassMaterialKind.LiquidGlass)
            {
                // Update macOS 26+ NSGlassEffectView's native Signed Distance Field path (_setPath:)
                var setPathSel = Sel("_setPath:");
                if (Responds(MaterialViewHandle, setPathSel))
                    SendVoid(MaterialViewHandle, setPathSel, path);
            }
            else if (ActiveKind == MacOSGlassMaterialKind.VisualEffectFallback)
            {
                // Update NSVisualEffectView's behind-window blur mask image on older macOS
                var maskImage = CreateMaskImage(path, (int)Math.Ceiling(w), (int)Math.Ceiling(h), w, h);
                if (maskImage != 0)
                {
                    SendVoid(MaterialViewHandle, Sel("setMaskImage:"), maskImage);
                    SendVoid(MaterialViewHandle, Sel("updateLayer"));
                    SendVoid(maskImage, Sel("release"));
                }
            }

            if (_currentPath != 0)
                CGPathRelease(_currentPath);
            _currentPath = CGPathCreateCopy(path);
        }
        finally
        {
            CGPathRelease(path);
        }

        MaskedContourCount = count;
        var shouldHide = count == 0;
        if (IsHidden != shouldHide)
        {
            SendBoolArg(HostViewHandle, Sel("setHidden:"), shouldHide);
            IsHidden = shouldHide;
        }

        if (!shouldHide)
        {
            SendVoid(HostViewHandle, Sel("layoutSubtreeIfNeeded"));
            if (ActiveKind == MacOSGlassMaterialKind.LiquidGlass)
            {
                SendVoid(MaterialViewHandle, Sel("layoutSubtreeIfNeeded"));
                EnableWindowServerAwareBackdrop(MaterialViewHandle);
            }
        }
    }

    private static nint CreateMaskImage(nint path, int pixelWidth, int pixelHeight, double pointWidth, double pointHeight)
    {
        if (path == 0 || pixelWidth <= 0 || pixelHeight <= 0)
            return 0;

        var colorSpace = CGColorSpaceCreateDeviceRGB();
        if (colorSpace == 0)
            return 0;

        try
        {
            // kCGImageAlphaPremultipliedLast = 1
            var ctx = CGBitmapContextCreate(0, (nuint)pixelWidth, (nuint)pixelHeight, 8, (nuint)(pixelWidth * 4), colorSpace, 1);
            if (ctx == 0)
                return 0;

            try
            {
                CGContextSetRGBFillColor(ctx, 0, 0, 0, 1);
                CGContextAddPath(ctx, path);
                CGContextEOFillPath(ctx);
                var cgImage = CGBitmapContextCreateImage(ctx);
                if (cgImage == 0)
                    return 0;

                try
                {
                    var nsImageClass = objc_getClass("NSImage");
                    if (nsImageClass == 0)
                        return 0;
                    return SendInitWithCGImage(
                        Send(nsImageClass, Sel("alloc")),
                        Sel("initWithCGImage:size:"),
                        cgImage,
                        new CGSize(pointWidth, pointHeight));
                }
                finally
                {
                    CGImageRelease(cgImage);
                }
            }
            finally
            {
                CGContextRelease(ctx);
            }
        }
        finally
        {
            CGColorSpaceRelease(colorSpace);
        }
    }

    private static void EnableWindowServerAwareBackdrop(nint glassView)
    {
        var subviews = Send(glassView, Sel("subviews"));
        var count = subviews == 0 ? 0 : Send(subviews, Sel("count"));
        for (nint i = 0; i < count; i++)
        {
            var subview = Send(subviews, Sel("objectAtIndex:"), i);
            var layer = subview == 0 ? 0 : Send(subview, Sel("layer"));
            if (layer != 0)
                UpgradeBackdropLayers(layer);
        }
    }

    private static void UpgradeBackdropLayers(nint layer)
    {
        if (layer == 0)
            return;

        var setWsSel = Sel("setWindowServerAware:");
        if (Responds(layer, setWsSel))
        {
            SendBoolArg(layer, setWsSel, true);
            var setNsSel = Sel("setGroupNamespace:");
            if (Responds(layer, setNsSel))
            {
                var nsStr = CFStringCreateWithCString(0, "hostingNamespacedContext", 0x08000100);
                if (nsStr != 0)
                {
                    SendVoid(layer, setNsSel, nsStr);
                    CFRelease(nsStr);
                }
            }
            var setGroupSel = Sel("setGroupName:");
            if (Responds(layer, setGroupSel))
            {
                var groupStr = CFStringCreateWithCString(0, "NSCGSWindowBehindWindowCaptureBackdropGroup", 0x08000100);
                if (groupStr != 0)
                {
                    SendVoid(layer, setGroupSel, groupStr);
                    CFRelease(groupStr);
                }
            }
        }

        var sublayers = Send(layer, Sel("sublayers"));
        var count = sublayers == 0 ? 0 : Send(sublayers, Sel("count"));
        for (nint i = 0; i < count; i++)
        {
            var sub = Send(sublayers, Sel("objectAtIndex:"), i);
            UpgradeBackdropLayers(sub);
        }
    }

    /// <summary>
    /// Verifies via <c>hitTest:</c> on the host view that pointer events at <paramref name="x"/>, <paramref name="y"/>
    /// pass straight through (<c>0</c> / <c>nil</c>).
    /// </summary>
    public nint HitTestHostView(double x, double y)
    {
        if (_disposed || HostViewHandle == 0)
            return 0;
        using var pool = new AutoreleasePool();
        return SendPoint(HostViewHandle, Sel("hitTest:"), new CGPoint(x, y));
    }

    /// <summary>
    /// Checks whether a point in top-left control coordinates lies inside the current glass mask path (using even-odd fill).
    /// </summary>
    public bool MaskContainsPoint(double controlX, double controlY, double controlHeight)
    {
        if (_disposed || _currentPath == 0)
            return false;
        if (CGPathIsEmpty(_currentPath))
            return false;
        return CGPathContainsPoint(_currentPath, 0, new CGPoint(controlX, controlHeight - controlY), true);
    }

    private void ApplyAppearance(bool isDark)
    {
        var appearanceName = isDark ? "NSAppearanceNameVibrantDark" : "NSAppearanceNameVibrantLight";
        var cfName = CFStringCreateWithCString(0, appearanceName, 0x08000100);
        if (cfName != 0)
        {
            var appearance = Send(objc_getClass("NSAppearance"), Sel("appearanceNamed:"), cfName);
            if (appearance != 0)
            {
                SendVoid(HostViewHandle, Sel("setAppearance:"), appearance);
                if (MaterialViewHandle != 0)
                {
                    SendVoid(MaterialViewHandle, Sel("setAppearance:"), appearance);
                    if (ActiveKind == MacOSGlassMaterialKind.VisualEffectFallback)
                    {
                        // Menu (5) in Dark mode and Popover (6) in Light mode for airy behind-window blur
                        SendInteger(MaterialViewHandle, Sel("setMaterial:"), isDark ? 5 : 6);
                    }
                }
            }
            CFRelease(cfName);
        }

        if (ActiveKind == MacOSGlassMaterialKind.LiquidGlass)
        {
            var setTintColorSel = Sel("setTintColor:");
            if (Responds(MaterialViewHandle, setTintColorSel))
            {
                var nsColorClass = objc_getClass("NSColor");
                var tint = isDark
                    ? SendRgba(nsColorClass, Sel("colorWithSRGBRed:green:blue:alpha:"), 0.16, 0.17, 0.20, 0.04)
                    : SendRgba(nsColorClass, Sel("colorWithSRGBRed:green:blue:alpha:"), 0.98, 0.99, 1.00, 0.05);
                if (tint != 0)
                    SendVoid(MaterialViewHandle, setTintColorSel, tint);
            }
        }
    }

    private static nint EnsureHostViewClass()
    {
        lock (ClassLock)
        {
            if (_hostViewClass != 0)
                return _hostViewClass;

            var existing = objc_getClass("FileeGlassHostView");
            if (existing != 0)
            {
                _hostViewClass = existing;
                return existing;
            }

            var nsViewClass = objc_getClass("NSView");
            if (nsViewClass == 0)
                return 0;

            var cls = objc_allocateClassPair(nsViewClass, "FileeGlassHostView", 0);
            if (cls == 0)
                return 0;

            var hitTestPtr = Marshal.GetFunctionPointerForDelegate(HitTestCallback);
            var returnsNoPtr = Marshal.GetFunctionPointerForDelegate(ReturnsNoCallback);

            // hitTest: -> nil (0) so all mouse clicks, hover, wheel, and drag-and-drop pass through to AvnView.
            class_addMethod(cls, Sel("hitTest:"), hitTestPtr, "@@:{CGPoint=dd}");
            class_addMethod(cls, Sel("acceptsFirstResponder"), returnsNoPtr, "c@:");
            class_addMethod(cls, Sel("isOpaque"), returnsNoPtr, "c@:");
            class_addMethod(cls, Sel("mouseDownCanMoveWindow"), returnsNoPtr, "c@:");

            objc_registerClassPair(cls);
            _hostViewClass = cls;
            return cls;
        }
    }

    private static bool Responds(nint target, nint selector) =>
        target != 0 && SendBoolWithSel(target, Sel("respondsToSelector:"), selector);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        using var pool = new AutoreleasePool();
        if (_currentPath != 0)
        {
            CGPathRelease(_currentPath);
            _currentPath = 0;
        }
        if (HostViewHandle != 0)
        {
            SendVoid(HostViewHandle, Sel("removeFromSuperview"));
            SendVoid(HostViewHandle, Sel("release"));
            HostViewHandle = 0;
        }
        if (MaterialViewHandle != 0)
        {
            SendVoid(MaterialViewHandle, Sel("release"));
            MaterialViewHandle = 0;
        }
        if (MaskLayerHandle != 0)
        {
            SendVoid(MaskLayerHandle, Sel("release"));
            MaskLayerHandle = 0;
        }
    }

    private static nint Sel(string name) => sel_registerName(name);

    private readonly struct AutoreleasePool : IDisposable
    {
        private readonly nint _pool = Send(Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc")), sel_registerName("init"));
        public AutoreleasePool() { }
        public void Dispose() => SendVoid(_pool, sel_registerName("drain"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGPoint(double X, double Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGSize(double Width, double Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);

    [DllImport(ObjC)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint objc_allocateClassPair(nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);

    [DllImport(ObjC)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(nint cls, nint name, nint imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport(ObjC)]
    private static extern void objc_registerClassPair(nint cls);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector, nint arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendInteger(nint receiver, nint selector, nint value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendDouble(nint receiver, nint selector, double value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendBoolArg(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBoolWithSel(nint receiver, nint selector, nint targetSelector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendInitWithFrame(nint receiver, nint selector, CGRect frame);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendInitWithCGImage(nint receiver, nint selector, nint cgImage, CGSize size);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendRect(nint receiver, nint selector, CGRect frame);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern CGRect SendRectReturn(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendPoint(nint receiver, nint selector, CGPoint point);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendAddSubview(nint receiver, nint selector, nint view, nint positioned, nint relativeTo);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendRgba(nint receiver, nint selector, double r, double g, double b, double a);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);

    [DllImport(CoreGraphics)]
    private static extern nint CGColorSpaceCreateDeviceRGB();

    [DllImport(CoreGraphics)]
    private static extern void CGColorSpaceRelease(nint space);

    [DllImport(CoreGraphics)]
    private static extern nint CGBitmapContextCreate(
        nint data,
        nuint width,
        nuint height,
        nuint bitsPerComponent,
        nuint bytesPerRow,
        nint space,
        uint bitmapInfo);

    [DllImport(CoreGraphics)]
    private static extern void CGContextSetRGBFillColor(nint c, double red, double green, double blue, double alpha);

    [DllImport(CoreGraphics)]
    private static extern void CGContextAddPath(nint c, nint path);

    [DllImport(CoreGraphics)]
    private static extern void CGContextEOFillPath(nint c);

    [DllImport(CoreGraphics)]
    private static extern nint CGBitmapContextCreateImage(nint context);

    [DllImport(CoreGraphics)]
    private static extern void CGContextRelease(nint c);

    [DllImport(CoreGraphics)]
    private static extern void CGImageRelease(nint image);

    [DllImport(CoreGraphics)]
    private static extern nint CGPathCreateMutable();

    [DllImport(CoreGraphics)]
    private static extern nint CGPathCreateCopy(nint path);

    [DllImport(CoreGraphics)]
    private static extern void CGPathMoveToPoint(nint path, nint m, double x, double y);

    [DllImport(CoreGraphics)]
    private static extern void CGPathAddLineToPoint(nint path, nint m, double x, double y);

    [DllImport(CoreGraphics)]
    private static extern void CGPathAddEllipseInRect(nint path, nint m, CGRect rect);

    [DllImport(CoreGraphics)]
    private static extern void CGPathCloseSubpath(nint path);

    [DllImport(CoreGraphics)]
    private static extern void CGPathRelease(nint path);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGPathIsEmpty(nint path);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGPathContainsPoint(nint path, nint m, CGPoint point, [MarshalAs(UnmanagedType.I1)] bool eoFill);
}
