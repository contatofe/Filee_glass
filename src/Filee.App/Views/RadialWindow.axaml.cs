// Thin view for the live donut: forwards drag & drop and pointer events to RadialController.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Platform;

namespace Filee.App.Views;

public partial class RadialWindow : Window
{
    private Filee.Platform.MacOS.MacOSGlassBackdrop? _glassBackdrop;
    private readonly List<(double CenterX, double CenterY, double OuterRadius, double InnerRadius)> _maskCircles = [];
    private readonly List<IReadOnlyList<(double X, double Y)>> _maskSlices = [];

    public RadialWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        Transparency.Track(this);

        DragDrop.AddDragEnterHandler(Root, OnDragEnter);
        DragDrop.AddDragOverHandler(Root, OnDragOver);
        DragDrop.AddDragLeaveHandler(Root, OnDragLeave);
        DragDrop.AddDropHandler(Root, OnDrop);
        // Halfway through a flip (Tab) the donut shows the other face of the profile.
        Donut.FaceSwapping += (_, _) => (DataContext as RadialViewModel)?.ToggleFace();
        Donut.Rendered += SyncNativeGlass;
        Donut.SizeChanged += (_, _) => SyncNativeGlass();
        SizeChanged += (_, _) => SyncNativeGlass();
        ActualThemeVariantChanged += (_, _) => SyncNativeGlass();
        Closed += (_, _) =>
        {
            if (OperatingSystem.IsMacOS())
            {
                _glassBackdrop?.Dispose();
                _glassBackdrop = null;
            }
        };
    }

    internal Filee.Platform.MacOS.MacOSGlassBackdrop? GlassBackdrop => _glassBackdrop;

    internal void AttachMacOSGlass(Filee.Platform.MacOS.MacOSPlatformServices platform, nint windowHandle)
    {
        if (!OperatingSystem.IsMacOS() || windowHandle == 0)
            return;
        _glassBackdrop ??= platform.AttachGlassBackdrop(windowHandle);
        SyncNativeGlass();
    }

    private void SyncNativeGlass()
    {
        if (!OperatingSystem.IsMacOS() || _glassBackdrop is not { } backdrop)
            return;

        var width = Donut.Bounds.Width;
        var height = Donut.Bounds.Height;
        var visible = IsVisible && Donut.Opacity > 0.01 && width > 1 && height > 1;
        if (!visible || Transparency.Reduced)
        {
            backdrop.Update(width, height, visible: false, isDark: false, reducedTransparency: Transparency.Reduced,
                effectiveOpacity: 0, circles: [], slices: []);
            return;
        }

        Donut.GetGlassMaskGeometry(_maskCircles, _maskSlices);
        var circles = new Filee.Platform.MacOS.GlassCircle[_maskCircles.Count];
        for (var i = 0; i < _maskCircles.Count; i++)
        {
            var c = _maskCircles[i];
            circles[i] = new Filee.Platform.MacOS.GlassCircle(c.CenterX, c.CenterY, c.OuterRadius, c.InnerRadius);
        }

        var slices = new Filee.Platform.MacOS.GlassSliceContour[_maskSlices.Count];
        for (var i = 0; i < _maskSlices.Count; i++)
            slices[i] = new Filee.Platform.MacOS.GlassSliceContour(_maskSlices[i]);

        var isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        backdrop.Update(
            width: width,
            height: height,
            visible: true,
            isDark: isDark,
            reducedTransparency: Transparency.Reduced,
            effectiveOpacity: Donut.Opacity,
            circles: circles,
            slices: slices);
    }

    public DonutMenu DonutControl => Donut;

    /// <summary>A drag entered the window, with or without files (for the log: did the drag reach the donut at all).</summary>
    public event Action? DragEntered;

    /// <summary>Files entered the window during a drag.</summary>
    public event Action<IReadOnlyList<string>>? FilesEntered;

    /// <summary>A drag without files Filee reads entered the window: the formats it offered (for the log).</summary>
    public event Action<string>? DragWithoutFiles;

    /// <summary>A drop landed on the donut: (hit, what it brought). The handler owns the browser drop in it.</summary>
    internal event Action<DonutHit, DraggedItems>? Dropped;

    public event Action? DragLeft;

    /// <summary>
    /// After a drag ends (drop or leave): how many drag-over events came in, where the last one was, how far that
    /// slice was drawn lit up (0 to 1) and whether it ended in a drop.
    /// </summary>
    public event Action<int, DonutHit, double, bool>? DragSummary;

    private int _dragOverEvents;
    private bool _dragOffersFiles;
    private DonutHit _lastDragHit;
    private Point? _lastDragAt;

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        DragEntered?.Invoke();
        // Only the names count while dragging: a browser's files are read again from the drop.
        var dragged = DraggedFiles.Read(e.DataTransfer, forDrop: false);
        dragged.Web?.Dispose();
        var files = dragged.Files;
        _dragOffersFiles = files.Count > 0 || DraggedFiles.Offered(e.DataTransfer);
        if (files.Count == 0)
        {
            DragWithoutFiles?.Invoke(DraggedFiles.Describe(e.DataTransfer));
            e.DragEffects = DragDropEffects.None;
            return;
        }
        _dragOverEvents = 0;
        _lastDragAt = null;
        FilesEntered?.Invoke(files);
        OnDragOver(sender, e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var at = e.GetPosition(Donut);
        var hit = Donut.HitTest(at);
        _dragOverEvents++;
        _lastDragHit = hit;
        _lastDragAt = at;
        Donut.SetExternalHit(hit, at);

        // Always COPY. Reporting MOVE would make Explorer delete the source files after the drop.
        e.DragEffects = _dragOffersFiles && Donut.Converts(hit) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DragSummary?.Invoke(_dragOverEvents, _lastDragHit, Donut.LitAmount(_lastDragHit.Index), false);
        Donut.ExternalDragLeft(_lastDragAt);
        DragLeft?.Invoke();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var hit = Donut.HitTest(e.GetPosition(Donut));
        DragSummary?.Invoke(_dragOverEvents, hit, Donut.LitAmount(hit.Index), true);
        Donut.CollapseGroup();
        Donut.SetExternalHighlight(-1);
        e.DragEffects = Donut.Converts(hit) ? DragDropEffects.Copy : DragDropEffects.None;
        var dropped = DraggedFiles.Read(e.DataTransfer, forDrop: true);
        if (Dropped is { } handler)
            handler(hit, dropped);
        else
            dropped.Web?.Dispose();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Donut.HandleKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            EscapePressed?.Invoke();
            e.Handled = true;
        }
    }

    public event Action? EscapePressed;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Hidden, however that happened: a group waiting to open doesn't open on the hidden donut.
        if (change.Property == IsVisibleProperty)
        {
            if (!change.GetNewValue<bool>())
                Donut.CollapseGroup();
            SyncNativeGlass();
        }
    }
}
