// The donut toolbar: a ring completely divided into equal slices, one per preset.
// Used both as the live toolbar (RadialWindow) and as the editor in Settings → Donut toolbar (IsEditMode).
//
// Drawn by hand (no child controls) so it stays fast on low-end PCs and so every slice can be hit-tested by
// its exact wedge shape. Animations are tiny springs advanced on each rendered frame; with
// Motion.Enabled == false every value jumps straight to its target.
//
// After a drop the donut turns into a progress ring: see DonutMenu.Progress.cs. Group slices, which fan out a ring
// of presets, and the keyboard: DonutMenu.Groups.cs. Tab (or the wheel) flips the donut over: DonutMenu.Flip.cs.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Services;

namespace Filee.App.Controls;

/// <summary>One slice of the donut.</summary>
public sealed class DonutItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }

    /// <summary>Small second line, e.g. the target format. Hidden when equal to <see cref="Label"/>.</summary>
    public string? Caption { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>Tooltip shown for disabled slices.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>
    /// A group slice: converts nothing itself; hovering it fans these out in a small ring outside the donut, where they
    /// are clicked or dropped on like ordinary slices.
    /// </summary>
    public IReadOnlyList<DonutItem>? Children { get; init; }

    /// <summary>A group slice (<see cref="Children"/> set), even one left with nothing to fan out.</summary>
    public bool IsGroup => Children is not null;

    /// <summary>A group with something to fan out.</summary>
    public bool CanExpand => Children is { Count: > 0 };
}

/// <summary>Donut-shaped menu control.</summary>
public sealed partial class DonutMenu : Control
{
    public static readonly StyledProperty<IReadOnlyList<DonutItem>?> ItemsProperty =
        AvaloniaProperty.Register<DonutMenu, IReadOnlyList<DonutItem>?>(nameof(Items));
    public static readonly StyledProperty<double> OuterRadiusProperty =
        AvaloniaProperty.Register<DonutMenu, double>(nameof(OuterRadius), 150);
    public static readonly StyledProperty<double> HoleRatioProperty =
        AvaloniaProperty.Register<DonutMenu, double>(nameof(HoleRatio), 0.42);
    public static readonly StyledProperty<double> SliceOpacityProperty =
        AvaloniaProperty.Register<DonutMenu, double>(nameof(SliceOpacity), 0.96);
    public static readonly StyledProperty<string?> CenterTitleProperty =
        AvaloniaProperty.Register<DonutMenu, string?>(nameof(CenterTitle));
    public static readonly StyledProperty<string?> CenterSubtitleProperty =
        AvaloniaProperty.Register<DonutMenu, string?>(nameof(CenterSubtitle));
    public static readonly StyledProperty<bool> IsEditModeProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(IsEditMode));
    public static readonly StyledProperty<bool> AllowContextEditProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(AllowContextEdit));
    public static readonly StyledProperty<bool> ReserveSubRingProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(ReserveSubRing));

    /// <summary>The theme's softness (Styles/Controls.axaml binds SoftRadius): rounds the corners of the slices.</summary>
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<DonutMenu, CornerRadius>(nameof(CornerRadius));

    public static readonly StyledProperty<IBrush?> SliceBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(SliceBrush), Brushes.White);
    public static readonly StyledProperty<IBrush?> SliceBorderBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(SliceBorderBrush));
    public static readonly StyledProperty<IBrush?> GlassSheenBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(GlassSheenBrush));
    public static readonly StyledProperty<IBrush?> GlassSpecularBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(GlassSpecularBrush));
    public static readonly StyledProperty<IBrush?> GlassHoverBorderBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(GlassHoverBorderBrush));
    public static readonly StyledProperty<IBrush?> CenterBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(CenterBrush), Brushes.White);
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);
    public static readonly StyledProperty<IBrush?> OnAccentBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(OnAccentBrush), Brushes.White);
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(Foreground), Brushes.Black);
    public static readonly StyledProperty<IBrush?> SecondaryForegroundProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(SecondaryForeground), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> DangerBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(DangerBrush), Brushes.IndianRed);

    /// <summary>Gap between slices in pixels (keeps the donut "full" while separating slices).</summary>
    private const double GapPixels = 3;
    private const double PopOut = 7;
    private const double OpenDuration = 0.30;
    private const double OpenStagger = 0.012;
    private double[] _closeFrom = [];
    private const double DragStartDistance = 5;

    // Pencil (Material "edit") path in a 24×24 box.
    private static readonly Geometry PencilIcon = Geometry.Parse(
        "M3,17.25V21h3.75L17.81,9.94l-3.75-3.75L3,17.25z M20.71,7.04c0.39-0.39,0.39-1.02,0-1.41l-2.34-2.34c-0.39-0.39-1.02-0.39-1.41,0l-1.83,1.83l3.75,3.75L20.71,7.04z");

    private Spring[] _hover = [];
    private double[] _open = [];
    private Spring _centerPulse;
    private double _openClock = double.MaxValue;
    private TimeSpan? _lastFrame;
    private bool _animating;
    private Action? _closeCallback;
    private bool _closing;

    private int _hoverIndex = -1;
    private int _externalHighlight = -1;
    private bool _externalCenterHot;
    private int _dimmedIndex = -1;
    private int _dropTargetIndex = -1;

    // Edit mode drop preview: the slices make room where a dragged preset would land and a translucent slice shows
    // it there. Every slice's centre angle and width is a spring, so the others slide aside instead of jumping.
    private Spring[] _mid = [];
    private Spring[] _span = [];
    private Spring _previewMid;
    private Spring _previewSpan;
    private int _previewSlot = -1;
    private int _previewRemoved = -1;
    private string? _previewLabel;
    private (int Index, Point Start, PointerPressedEventArgs Args)? _press;

    static DonutMenu()
    {
        AffectsMeasure<DonutMenu>(OuterRadiusProperty, ReserveSubRingProperty);
        AffectsRender<DonutMenu>(HoleRatioProperty, SliceOpacityProperty, CenterTitleProperty, CenterSubtitleProperty,
            IsEditModeProperty, SliceBrushProperty, SliceBorderBrushProperty, GlassSheenBrushProperty,
            GlassSpecularBrushProperty, GlassHoverBorderBrushProperty, CenterBrushProperty, AccentBrushProperty,
            OnAccentBrushProperty, ForegroundProperty, SecondaryForegroundProperty, SuccessBrushProperty, CornerRadiusProperty,
            CenterHintProperty, CenterHintWheelProperty);
        ItemsProperty.Changed.AddClassHandler<DonutMenu>((d, _) => d.OnItemsChanged());
    }

    public DonutMenu()
    {
        // A circle reads the same either way: slices keep their clockwise order in Arabic too, their labels are
        // shaped right to left (TextDirection).
        FlowDirection = FlowDirection.LeftToRight;
        ClipToBounds = false;
        Cursor = new Cursor(StandardCursorType.Hand);
        // The live click-mode donut handles Tab, arrows, Enter and Escape itself. It must be the focused control
        // after a context-menu/shortcut launch; otherwise Avalonia has no focused target to which the key events can
        // be routed. It is focused explicitly by RadialController, so it must not participate in normal Tab order.
        Focusable = true;
        IsTabStop = false;
    }

    public IReadOnlyList<DonutItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public double OuterRadius { get => GetValue(OuterRadiusProperty); set => SetValue(OuterRadiusProperty, value); }
    public double HoleRatio { get => GetValue(HoleRatioProperty); set => SetValue(HoleRatioProperty, value); }
    public double SliceOpacity { get => GetValue(SliceOpacityProperty); set => SetValue(SliceOpacityProperty, value); }
    public string? CenterTitle { get => GetValue(CenterTitleProperty); set => SetValue(CenterTitleProperty, value); }
    public string? CenterSubtitle { get => GetValue(CenterSubtitleProperty); set => SetValue(CenterSubtitleProperty, value); }

    /// <summary>Settings editor mode: shows ✎ on hover, raises drag and edit events instead of invoking slices.</summary>
    public bool IsEditMode { get => GetValue(IsEditModeProperty); set => SetValue(IsEditModeProperty, value); }

    /// <summary>Live toolbar in click mode: right-click on a slice raises <see cref="SegmentContextRequested"/>.</summary>
    public bool AllowContextEdit { get => GetValue(AllowContextEditProperty); set => SetValue(AllowContextEditProperty, value); }

    /// <summary>
    /// Leave room for a group's ring even before the slices are known (the drag window is placed before files arrive),
    /// so the window doesn't grow, and the donut move, when they do.
    /// </summary>
    public bool ReserveSubRing { get => GetValue(ReserveSubRingProperty); set => SetValue(ReserveSubRingProperty, value); }

    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    public IBrush? SliceBrush { get => GetValue(SliceBrushProperty); set => SetValue(SliceBrushProperty, value); }
    public IBrush? SliceBorderBrush { get => GetValue(SliceBorderBrushProperty); set => SetValue(SliceBorderBrushProperty, value); }
    public IBrush? GlassSheenBrush { get => GetValue(GlassSheenBrushProperty); set => SetValue(GlassSheenBrushProperty, value); }
    public IBrush? GlassSpecularBrush { get => GetValue(GlassSpecularBrushProperty); set => SetValue(GlassSpecularBrushProperty, value); }
    public IBrush? GlassHoverBorderBrush { get => GetValue(GlassHoverBorderBrushProperty); set => SetValue(GlassHoverBorderBrushProperty, value); }
    public IBrush? CenterBrush { get => GetValue(CenterBrushProperty); set => SetValue(CenterBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? OnAccentBrush { get => GetValue(OnAccentBrushProperty); set => SetValue(OnAccentBrushProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? SecondaryForeground { get => GetValue(SecondaryForegroundProperty); set => SetValue(SecondaryForegroundProperty, value); }
    public IBrush? DangerBrush { get => GetValue(DangerBrushProperty); set => SetValue(DangerBrushProperty, value); }

    /// <summary>True while an external drag hovers the hole (drop = cancel).</summary>
    public bool IsCenterHot => _externalCenterHot;

    /// <summary>Currently emphasised slice (pointer hover or external highlight), or -1.</summary>
    public int ActiveIndex => _externalHighlight >= 0 ? _externalHighlight : _hoverIndex;

    /// <summary>A slice was clicked (live toolbar, click mode).</summary>
    public event EventHandler<int>? SegmentInvoked;

    /// <summary>A slice of a group's ring was clicked: (group, slice in the group).</summary>
    public event EventHandler<(int Group, int Sub)>? SubSegmentInvoked;

    /// <summary>The hovered slice of a group's ring, or -1. <see cref="ActiveIndexChanged"/> is raised when it changes.</summary>
    public int ActiveSubIndex => _subHover;

    /// <summary>Right-click or ✎ on a slice.</summary>
    public event EventHandler<int>? SegmentContextRequested;

    /// <summary>Right-click on a slice of a group's ring (live toolbar, click mode): (group, slice in the group).</summary>
    public event EventHandler<(int Group, int Sub)>? SubSegmentContextRequested;

    /// <summary>The hole was clicked.</summary>
    public event EventHandler? CenterInvoked;

    /// <summary>Edit mode: the user started dragging slice <c>Index</c>.</summary>
    public event EventHandler<(int Index, PointerEventArgs Args)>? SegmentDragStarted;

    /// <summary>The emphasised slice changed (-1 = none). Useful to update the centre text.</summary>
    public event EventHandler<int>? ActiveIndexChanged;

    /// <summary>Raised after each <see cref="Render"/> pass so native glass backdrops can sync their shape mask.</summary>
    internal event Action? Rendered;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Transparency.Changed += OnTransparencyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Transparency.Changed -= OnTransparencyChanged;
    }

    private void OnTransparencyChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>
    /// Effective background opacity for glass slices: respects <see cref="Transparency.Reduced"/> (forcing 1.0 opaque)
    /// while modulating <see cref="SliceOpacity"/> for Liquid Glass translucency when styled.
    /// On macOS, where a real behind-window blur + Liquid Glass SDF backdrop sits behind the slices, the Avalonia
    /// surface fill acts as a light glass tint (0.14..0.58) so the blurred background shines through.
    /// </summary>
    internal double EffectiveSliceOpacity =>
        Transparency.Reduced ? 1.0
        : SliceBorderBrush is null ? SliceOpacity
        : OperatingSystem.IsMacOS()
            ? Math.Clamp(SliceOpacity * 0.56, 0.14, 0.58)
            : SliceOpacity >= 0.995 ? 0.92
            : Math.Clamp(SliceOpacity * 0.86, 0.08, 0.92);

    /// <summary>
    /// Effective background opacity for the centre hub and progress plate: translucent glass when enabled,
    /// opaque when <see cref="Transparency.Reduced"/> is active or in raw borderless tests.
    /// </summary>
    internal double EffectiveCenterOpacity =>
        Transparency.Reduced || SliceBorderBrush is null ? 1.0
        : OperatingSystem.IsMacOS()
            ? Math.Clamp(SliceOpacity * 0.62, 0.18, 0.64)
            : SliceOpacity >= 0.995 ? 0.92
            : Math.Clamp(SliceOpacity * 0.86, 0.18, 0.92);

    private int Count => Items?.Count ?? 0;
    private Point Center => new(Bounds.Width / 2, Bounds.Height / 2);
    private double InnerRadius => OuterRadius * Math.Clamp(HoleRatio, 0.2, 0.75);

    // ───────────────────────── Public API ─────────────────────────

    /// <summary>Plays the "pop out from the centre" animation.</summary>
    public void PlayOpenAnimation()
    {
        ResetProgress();
        _closing = false;
        _closeCallback = null;
        if (!Motion.Enabled)
        {
            Array.Fill(_open, 1);
            InvalidateVisual();
            return;
        }
        Array.Fill(_open, 0);
        _lastFrame = null;
        _openClock = 0;
        StartAnimation();
    }

    /// <summary>Shrinks the donut back into the centre, then calls <paramref name="done"/>.</summary>
    public void PlayCloseAnimation(Action done)
    {
        if (IsFlipping)
            FinishFlip();
        if (!Motion.Enabled || Count == 0)
        {
            done();
            return;
        }
        _closeFrom = (double[])_open.Clone();
        _closing = true;
        _closeCallback = done;
        _openClock = 0;
        StartAnimation();
    }

    /// <summary>Short "boing" of the centre, e.g. after a drop.</summary>
    public void PulseCenter()
    {
        if (!Motion.Enabled)
            return;
        _centerPulse.Velocity = 6;
        StartAnimation();
    }

    /// <summary>Highlights a slice from outside (drag-over in the live toolbar). -1 clears.</summary>
    public void SetExternalHighlight(int index, bool centerHot = false)
    {
        if (_externalHighlight == index && _externalCenterHot == centerHot)
            return;
        _externalHighlight = index;
        _externalCenterHot = centerHot;
        ActiveIndexChanged?.Invoke(this, ActiveIndex);
        StartAnimation();
    }

    /// <summary>How far slice <paramref name="index"/> is drawn lit up right now (0 to 1), for the drag log.</summary>
    public double LitAmount(int index) => index >= 0 && index < _hover.Length ? _hover[index].Value : 0;

    /// <summary>Edit mode: the slice being dragged is drawn faded.</summary>
    public void SetDimmedIndex(int index)
    {
        _dimmedIndex = index;
        InvalidateVisual();
    }

    /// <summary>Edit mode: highlights where a dragged item would land (-1 none, Count = empty ring).</summary>
    public void SetDropTarget(int index)
    {
        if (_dropTargetIndex == index)
            return;
        _dropTargetIndex = index;
        InvalidateVisual();
    }

    /// <summary>
    /// Edit mode: opens a gap at <paramref name="slot"/> (the position in the list after the drop) and shows a
    /// translucent slice labelled <paramref name="label"/> there. <paramref name="removed"/> is the slice being moved,
    /// which leaves its place. -1 closes the gap.
    /// </summary>
    public void SetDropPreview(int slot, string? label = null, int removed = -1)
    {
        if (_previewSlot == slot && _previewRemoved == removed && _previewLabel == label)
            return;
        if (_previewSlot < 0 && slot >= 0)
        {
            // The new slice grows out of nothing where it appears.
            _previewMid = new Spring { Value = DonutGeometry.MidAngle(slot, SlotCount(removed)) };
            _previewSpan = default;
        }
        _previewSlot = slot;
        _previewRemoved = removed;
        _previewLabel = label;
        StartAnimation();
    }

    /// <summary>The slot under <paramref name="p"/> when the donut has <paramref name="slots"/> slices; -1 off the ring.</summary>
    public int SlotAt(Point p, int slots)
    {
        var hit = DonutGeometry.HitTest(Center, p, InnerRadius, OuterRadius, Math.Max(slots, 1), PopOut);
        return hit.Kind == DonutHitKind.Segment ? hit.Index : -1;
    }

    /// <summary>Slices while previewing: the items, minus the one being moved, plus the preview.</summary>
    private int SlotCount(int removed) => Count + 1 - (removed >= 0 ? 1 : 0);

    /// <summary>Where slice <paramref name="i"/> belongs: its usual place, or shifted to make room for the preview.</summary>
    private (double Mid, double Span) LayoutTarget(int i)
    {
        var n = Count;
        if (_previewSlot < 0)
            return (DonutGeometry.MidAngle(i, n), DonutGeometry.Span(n));
        if (i == _previewRemoved)
            return (i < _mid.Length ? _mid[i].Value : DonutGeometry.MidAngle(i, n), 0); // it becomes the preview
        var slot = _previewRemoved >= 0 && i > _previewRemoved ? i - 1 : i;
        if (slot >= _previewSlot)
            slot++;
        var m = SlotCount(_previewRemoved);
        return (DonutGeometry.MidAngle(slot, m), DonutGeometry.Span(m));
    }

    /// <summary>Moves the slices and the preview towards their places. Returns true while they are still moving.</summary>
    private bool StepLayout(double dt, bool snap = false)
    {
        snap |= !Motion.Enabled;
        var active = false;
        for (var i = 0; i < _mid.Length; i++)
        {
            var (mid, span) = LayoutTarget(i);
            if (snap)
            {
                _mid[i] = new Spring { Value = mid };
                _span[i] = new Spring { Value = span };
                continue;
            }
            active |= _mid[i].Step(mid, dt, 420, 36);
            active |= _span[i].Step(span, dt, 420, 36);
        }
        var m = SlotCount(_previewRemoved);
        var previewMid = _previewSlot >= 0 ? DonutGeometry.MidAngle(_previewSlot, m) : _previewMid.Value;
        var previewSpan = _previewSlot >= 0 ? DonutGeometry.Span(m) : 0;
        if (snap)
        {
            _previewMid = new Spring { Value = previewMid };
            _previewSpan = new Spring { Value = previewSpan };
            return false;
        }
        active |= _previewMid.Step(previewMid, dt, 420, 36);
        active |= _previewSpan.Step(previewSpan, dt, 420, 36);
        return active;
    }

    /// <summary>Hit-tests a point in this control's coordinates.</summary>
    public DonutHit HitTest(Point p)
    {
        if (_progressMode)
            return HitTestProgress(p);
        if (IsEditMode && _hoverIndex >= 0 && _hoverIndex < Count &&
            Distance(p, EditButtonCenter(_hoverIndex)) <= 13)
            return new DonutHit(DonutHitKind.EditButton, _hoverIndex);
        var hit = DonutGeometry.HitTest(Center, p, InnerRadius, OuterRadius, Count, PopOut);
        if (ExpandedChildren is { } children)
        {
            var (inner, outer) = SubRadii;
            var parentMid = DonutGeometry.MidAngle(_expanded, Count);
            var subSpan = SubSpanOf(children.Count);
            var sub = DonutGeometry.SubIndexAt(Center, p, inner, outer, parentMid, children.Count, subSpan);
            if (sub >= 0)
                return new DonutHit(DonutHitKind.SubSegment, _expanded, sub);
            // On the way out to the ring the pointer crosses the gap before it: there it still belongs to the group, or
            // the ring would fold away (or the hint and the drag cursor flicker) just before it is reached. A neighbour's
            // outer half stays that neighbour, so a click or a drop there converts with it; TrackGroups keeps the ring
            // open over it (OnWayToRing).
            if (hit.Kind == DonutHitKind.Outside && Distance(p, Center) < inner && InFan(p, _expanded))
                return new DonutHit(DonutHitKind.Segment, _expanded);
        }
        return hit;
    }

    // ───────────────────────── Layout ─────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = SideFor(OuterRadius, ReserveSubRing || (!IsEditMode && Items?.Any(i => i.IsGroup) == true));
        return new Size(side, side);
    }

    private void OnItemsChanged()
    {
        var n = Count;
        _hover = new Spring[n];
        _open = Enumerable.Repeat(1.0, n).ToArray();
        _hoverIndex = -1;
        _externalHighlight = -1;
        _dimmedIndex = -1;
        _dropTargetIndex = -1;
        // A drop changes the items into exactly what the preview showed: start there, without a jump.
        _previewSlot = -1;
        _previewRemoved = -1;
        _previewSpan = default;
        _mid = Enumerable.Range(0, n).Select(i => new Spring { Value = DonutGeometry.MidAngle(i, n) }).ToArray();
        _span = Enumerable.Range(0, n).Select(_ => new Spring { Value = DonutGeometry.Span(n) }).ToArray();
        _expanded = _shownExpanded = _subHover = -1;
        ClearPending();
        _expand = default;
        // New files (or none): a flip of the old ones is over. A flip's own swap keeps going (SwapFaces).
        if (!_swapping)
            CancelFlip();
        _lastExternalAt = null;
        // The window is reused: a keyboard hold from the last time it was open must not swallow the first moves now.
        _keyboardHold = _ringFromKeys = false;
        _keyAnchor = _lastPointer = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    // ───────────────────────── Input ─────────────────────────

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_progressMode)
        {
            OnProgressPointerMoved(p);
            return;
        }

        if (_press is { } press && IsEditMode && Distance(p, press.Start) > DragStartDistance && press.Index >= 0)
        {
            _press = null;
            SegmentDragStarted?.Invoke(this, (press.Index, e));
            return;
        }

        if (_keyboardHold)
        {
            if (_keyAnchor is not { } anchor)
                _keyAnchor = anchor = p;
            if (Distance(p, anchor) < KeyboardJitter)
                return;
            _keyboardHold = false;
        }
        _lastPointer = p;
        var hit = HitTest(p);
        if (IsFlipping)
            PointAtAngle(DonutGeometry.AngleOf(Center, p), hit.Kind is DonutHitKind.Segment or DonutHitKind.SubSegment);
        TrackGroups(hit, p);
        SetHover(hit.Kind switch
        {
            DonutHitKind.Segment or DonutHitKind.EditButton or DonutHitKind.SubSegment => hit.Index,
            _ => -1,
        });
        // On a group's ring, why that slice (not the group) is disabled.
        var hovered = hit.Kind == DonutHitKind.SubSegment ? Items![hit.Index].Children![hit.Sub]
            : _hoverIndex >= 0 ? Items![_hoverIndex] : null;
        ToolTip.SetTip(this, hovered is { IsEnabled: false } ? hovered.DisabledReason : null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_progressMode)
        {
            OnProgressPointerMoved(new Point(double.MaxValue / 4, 0));
            return;
        }
        if (_press is null && !LeftThroughSeam(e.GetPosition(this)))
        {
            CollapseGroup();
            SetHover(-1);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var hit = HitTest(point.Position);

        if (_progressMode)
        {
            if (point.Properties.IsLeftButtonPressed)
            {
                _press = (-1, point.Position, e);
                e.Handled = true;
            }
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            if (hit.Kind is DonutHitKind.Segment or DonutHitKind.EditButton && (IsEditMode || AllowContextEdit))
            {
                SegmentContextRequested?.Invoke(this, hit.Index);
                e.Handled = true;
            }
            else if (hit.Kind == DonutHitKind.SubSegment && AllowContextEdit)
            {
                SubSegmentContextRequested?.Invoke(this, (hit.Index, hit.Sub));
                e.Handled = true;
            }
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _press = (hit.Kind == DonutHitKind.Segment ? hit.Index : -1, point.Position, e);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_press is null)
            return;
        _press = null;

        if (_progressMode)
        {
            OnProgressPointerReleased(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        var hit = HitTest(e.GetPosition(this));
        switch (hit.Kind)
        {
            case DonutHitKind.EditButton:
                SegmentContextRequested?.Invoke(this, hit.Index);
                break;
            case DonutHitKind.Segment when !IsEditMode && Items![hit.Index] is { IsEnabled: true, IsGroup: false }:
                SegmentInvoked?.Invoke(this, hit.Index);
                break;
            case DonutHitKind.Segment when !IsEditMode && Items![hit.Index].Children is { Count: > 0 }:
                Expand(hit.Index); // no waiting when clicked; a tap has no hover to rest on at all
                break;
            case DonutHitKind.Outside when !IsEditMode && _expanded >= 0:
                CollapseGroup(); // clicked past the ring
                break;
            case DonutHitKind.SubSegment when !IsEditMode && Items![hit.Index].Children![hit.Sub].IsEnabled:
                SubSegmentInvoked?.Invoke(this, (hit.Index, hit.Sub));
                break;
            case DonutHitKind.Center when !IsEditMode:
                CenterInvoked?.Invoke(this, EventArgs.Empty);
                break;
        }
        e.Handled = true;
    }

    private void SetHover(int index)
    {
        if (_hoverIndex == index)
            return;
        _hoverIndex = index;
        ActiveIndexChanged?.Invoke(this, ActiveIndex);
        StartAnimation();
    }

    // ───────────────────────── Animation ─────────────────────────

    /// <summary>Damping at which a spring of stiffness 320 settles without overshooting (2·√320).</summary>
    private const double CriticalDamping = 36;

    /// <summary>Underdamped spring: gives the soft "boing" when a slice pops out.</summary>
    private struct Spring
    {
        public double Value;
        public double Velocity;

        /// <summary>
        /// Longest time one integration step covers. With these stiffnesses a single step of a late frame (50 ms)
        /// overshoots more each frame instead of settling: slices stayed lit and flew apart on a busy machine.
        /// </summary>
        private const double MaxStep = 1 / 120.0;

        public bool Step(double target, double dt, double stiffness = 320, double damping = 20)
        {
            var steps = Math.Max(1, (int)Math.Ceiling(dt / MaxStep));
            var h = dt / steps;
            for (var i = 0; i < steps; i++)
            {
                var force = stiffness * (target - Value) - damping * Velocity;
                Velocity += force * h;
                Value += Velocity * h;
            }
            var settled = Math.Abs(target - Value) < 0.001 && Math.Abs(Velocity) < 0.01;
            if (settled)
            {
                Value = target;
                Velocity = 0;
            }
            return !settled;
        }
    }

    /// <summary>
    /// A frame loop that got no frame for this long is treated as stopped: a frame request can be lost (the window
    /// hidden in between, for one), and every later animation would wait for it, so the slice under a dragged file
    /// would never light up.
    /// </summary>
    internal const long StalledLoopMs = 250;

    private int _loop;           // the current frame loop; frames of an older one are ignored
    private long _lastTick;      // Environment.TickCount64 of the last frame or frame request

    /// <summary>Frame requests asked again before the animation is finished at once (see <see cref="CheckLoop"/>).</summary>
    internal const int FrameRetries = 2;

    private int _stalls; // times the current animation's frames stopped coming

    private void StartAnimation()
    {
        InvalidateVisual();
        if (_animating && Environment.TickCount64 - _lastTick < StalledLoopMs)
            return;
        _stalls = 0;
        StartLoop();
    }

    private void StartLoop()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            _animating = false;
            return;
        }
        _animating = true;
        _lastFrame = null;
        _lastTick = Environment.TickCount64;
        var loop = ++_loop;
        top.RequestAnimationFrame(time => OnFrame(time, loop));
        Watch(loop);
    }

    private void Watch(int loop) =>
        DispatcherTimer.RunOnce(() =>
        {
            if (loop == _loop)
                CheckLoop();
        }, TimeSpan.FromMilliseconds(StalledLoopMs + 50));

    /// <summary>
    /// Watches the frame loop without waiting for another animation call. When its frames stop (a request lost
    /// while the window was hidden, or the renderer waking up after a long idle), the frame is asked for again; after
    /// <see cref="FrameRetries"/> stalls the animations jump to their end, so an opening donut always appears. Stalls
    /// are counted per animation: frames that trickle in once per retry would otherwise open the donut over seconds.
    /// </summary>
    internal void CheckLoop()
    {
        if (!_animating)
            return;
        if (Environment.TickCount64 - _lastTick < StalledLoopMs)
        {
            Watch(_loop); // frames are coming: keep watching until the loop ends
            return;
        }
        if (++_stalls <= FrameRetries)
        {
            StartLoop();
            return;
        }
        _animating = false;
        _loop++;
        FinishAnimations();
    }

    /// <summary>
    /// Sets the opening, the highlights and the slice layout to where they end (a closing donut closes). The progress
    /// ring keeps its own timers.
    /// </summary>
    internal void FinishAnimations()
    {
        for (var i = 0; i < _hover.Length; i++)
            _hover[i] = new Spring { Value = i == ActiveIndex ? 1 : 0 };
        _centerPulse = default;
        _expand = new Spring { Value = _expanded >= 0 ? 1 : 0 };
        if (_expanded < 0)
            _shownExpanded = -1;
        StepLayout(0, snap: true);
        FinishFlip();
        if (_openClock < double.MaxValue)
        {
            _openClock = double.MaxValue;
            Array.Fill(_open, _closing ? 0 : 1);
            if (_closing)
                FinishClose();
        }
        InvalidateVisual();
    }

    private void OnFrame(TimeSpan time, int loop)
    {
        if (loop != _loop)
            return; // a loop that was given up on as stalled, replaced by a newer one
        _lastTick = Environment.TickCount64;
        var elapsed = _lastFrame is { } last ? Math.Max(0, (time - last).TotalSeconds) : 0;
        var dt = Math.Min(elapsed, 0.05);
        _lastFrame = time;
        var active = Advance(dt, elapsed);

        InvalidateVisual();
        if (active && TopLevel.GetTopLevel(this) is { } top)
            top.RequestAnimationFrame(next => OnFrame(next, loop));
        else
            _animating = false;
    }

    /// <summary>
    /// Advances every animation by <paramref name="dt"/> seconds. Returns true while more frames are needed.
    /// Internal so tests can step time deterministically.
    /// </summary>
    internal bool Advance(double dt, double? elapsed = null)
    {
        var active = false;

        if (!Motion.Enabled)
        {
            for (var i = 0; i < _hover.Length; i++)
            {
                _hover[i].Value = i == ActiveIndex ? 1 : 0;
                _hover[i].Velocity = 0;
            }
            Array.Fill(_open, _closing ? 0 : 1);
            _centerPulse = default;
            _expand = new Spring { Value = _expanded >= 0 ? 1 : 0 };
            if (_expanded < 0)
                _shownExpanded = -1;
            StepLayout(dt);
            FinishFlip(); // animations switched off halfway through a flip
            if (_closing)
                FinishClose();
            active |= StepProgress(dt); // hold timers still run without animation
        }
        else
        {
            // Popping out bounces; letting go settles without a bounce: one below zero would make the faded accent
            // flash once more on its way back.
            for (var i = 0; i < _hover.Length; i++)
                active |= i == ActiveIndex ? _hover[i].Step(1, dt) : _hover[i].Step(0, dt, 320, CriticalDamping);
            active |= _centerPulse.Step(0, dt, 260, 12);
            // A soft bounce opening, like the slices popping out; folding away settles without one. The bounce grows the
            // ring by about a pixel, well inside the room kept for it (SubRingSpace).
            active |= _expanded >= 0 ? _expand.Step(1, dt, 320, 24) : _expand.Step(0, dt, 320, CriticalDamping);
            if (_expanded < 0 && _expand.Value <= 0.001)
                _shownExpanded = -1;
            active |= StepProgress(dt);
            active |= StepLayout(dt);
            active |= StepFlip(elapsed ?? dt);

            if (_openClock < double.MaxValue)
            {
                _openClock += elapsed ?? dt;
                var done = true;
                for (var i = 0; i < _open.Length; i++)
                {
                    // Keep the wave brief even with many slices; a late frame must not stretch its duration.
                    var delay = _closing ? 0 : Math.Min(i * OpenStagger, 0.072);
                    var t = Math.Clamp((_openClock - delay) / (_closing ? 0.14 : OpenDuration), 0, 1);
                    _open[i] = _closing
                        ? (i < _closeFrom.Length ? _closeFrom[i] : 1) * (1 - EaseInCubic(t))
                        : EaseOutBack(t);
                    done &= t >= 1;
                }
                if (done)
                {
                    _openClock = double.MaxValue;
                    if (_closing)
                        FinishClose();
                }
                else
                {
                    active = true;
                }
            }
        }

        return active;
    }

    private void FinishClose()
    {
        _closing = false;
        var callback = _closeCallback;
        _closeCallback = null;
        callback?.Invoke();
    }

    private static double EaseOutBack(double t)
    {
        const double c1 = 0.8;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    private static double EaseInCubic(double t) => t * t * t;

    // ───────────────────────── Rendering ─────────────────────────

    public override void Render(DrawingContext context)
    {
        var center = Center;
        var r2 = OuterRadius;
        var r1 = InnerRadius;
        var n = Count;
        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, FontWeight.SemiBold);
        var typefaceRegular = new Typeface(GetValue(TextElement.FontFamilyProperty));

        if (_progressMode)
        {
            RenderProgress(context, center, r1, r2, typeface, typefaceRegular);
            Rendered?.Invoke();
            return;
        }

        RenderRing(context, center, r1, r2, n, typeface, typefaceRegular);

        var reaching = _expanded >= 0 ? _expanded : _pendingGroup;
        if (!IsEditMode && reaching >= 0 && reaching < n && Items![reaching].Children is { Count: > 0 } reachable)
            DrawHitBackdrop(context, center, reaching, reachable.Count);
        if (!IsEditMode && _shownExpanded >= 0 && _shownExpanded < n && _expand.Value > 0.01)
            DrawSubRing(context, center, typeface, typefaceRegular);
        if (HasOldGroupRing)
            DrawOldSubRing(context, center, typeface, typefaceRegular);

        DrawCenter(context, center, r1, typeface, typefaceRegular);

        if (IsEditMode && _hoverIndex >= 0 && _hoverIndex < n && _dimmedIndex < 0 && !IsFlipping)
            DrawEditButton(context, EditButtonCenter(_hoverIndex));

        Rendered?.Invoke();
    }

    /// <summary>The shadow, the slices and the edit mode's drop preview.</summary>
    private void RenderRing(DrawingContext context, Point center, double r1, double r2, int n, Typeface typeface, Typeface typefaceRegular)
    {
        DrawSoftShadow(context, center, r2);

        if (n == 0)
        {
            var ring = CreateRing(center, r1, r2);
            DrawGlassWedge(context, ring, enabled: true, emphasis: _dropTargetIndex >= 0 ? 0.42 : 0);
        }

        for (var i = 0; i < n; i++)
        {
            if (IsFlipping && SliceFlipWidth(false, i) == 0)
                continue;
            var item = Items![i];
            var open = i < _open.Length ? _open[i] : 1;
            if (open <= 0.001)
                continue;

            var previewing = IsEditMode && i < _mid.Length;
            var hover = i < _hover.Length && _previewSlot < 0 ? _hover[i].Value : 0;
            var mid = previewing ? _mid[i].Value : DonutGeometry.MidAngle(i, n);
            var span = previewing ? _span[i].Value : DonutGeometry.Span(n);
            if (span < 0.01)
                continue;
            var offset = DonutGeometry.PointAt(new Point(0, 0), hover * PopOut, mid);

            // Open animation: scale up from the centre with a small twist.
            var scale = 0.78 + 0.22 * open;
            var rotate = (1 - Math.Min(open, 1)) * -0.12;
            var transform =
                Matrix.CreateTranslation(-center.X, -center.Y) *
                Matrix.CreateRotation(rotate) *
                Matrix.CreateScale(scale * (1 + hover * 0.025), scale * (1 + hover * 0.025)) *
                Matrix.CreateTranslation(center.X + offset.X, center.Y + offset.Y) *
                SliceFlipTransform(false, i);

            using (context.PushTransform(transform))
            using (context.PushOpacity(Math.Clamp(open, 0, 1) * (i == _dimmedIndex ? 0.3 : 1)))
            {
                var wedge = CreateWedge(center, r1, r2, mid, span);
                var emphasis = i == _dropTargetIndex ? 1.0 : hover;
                DrawGlassWedge(context, wedge, item.IsEnabled, emphasis);

                var hot = (i == ActiveIndex || i == _dropTargetIndex) && item.IsEnabled;
                DrawLabel(context, item, center, (r1 + r2) / 2, mid, span * (r1 + r2) / 2 - 10,
                    r2 - r1 - 16, typeface, typefaceRegular, hot);
            }
        }

        if (_oldFace is { } old)
            for (var i = 0; i < old.Length; i++)
            {
                var slice = old[i];
                if (SliceFlipWidth(true, i) == 0 || slice.Open <= 0.001)
                    continue;
                var offset = DonutGeometry.PointAt(new Point(0, 0), slice.Hover * PopOut, slice.Mid);
                var scale = 0.78 + 0.22 * slice.Open;
                var rotate = (1 - Math.Min(slice.Open, 1)) * -0.12;
                var transform = Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateRotation(rotate) *
                                Matrix.CreateScale(scale * (1 + slice.Hover * 0.025), scale * (1 + slice.Hover * 0.025)) *
                                Matrix.CreateTranslation(center.X + offset.X, center.Y + offset.Y) *
                                SliceFlipTransform(true, i);
                using (context.PushTransform(transform))
                using (context.PushOpacity(Math.Clamp(slice.Open, 0, 1) * (slice.Dimmed ? 0.3 : 1)))
                {
                    var wedge = CreateWedge(center, r1, r2, slice.Mid, slice.Span);
                    DrawGlassWedge(context, wedge, slice.Item.IsEnabled, slice.Emphasis);
                    DrawLabel(context, slice.Item, center, (r1 + r2) / 2, slice.Mid,
                        slice.Span * (r1 + r2) / 2 - 10, r2 - r1 - 16, typeface, typefaceRegular,
                        slice.Hot && slice.Item.IsEnabled);
                }
            }

        if (IsEditMode && _previewSpan.Value > 0.01)
            DrawPreview(context, center, r1, r2, typeface, typefaceRegular);
    }

    /// <summary>
    /// Draws a slice or ring with Liquid Glass layers: translucent tinted surface, top-lit refractive sheen,
    /// inner lens bevel, directional specular rim light, and luminous glass hover highlight.
    /// </summary>
    internal void DrawGlassWedge(DrawingContext context, Geometry wedge, bool enabled, double emphasis)
    {
        var enabledFactor = enabled ? 1.0 : 0.55;
        context.DrawGeometry(WithOpacity(SliceBrush, EffectiveSliceOpacity * enabledFactor), BorderPen(), wedge);

        var hasGlassChrome = !Transparency.Reduced && SliceBorderBrush is not null;
        if (hasGlassChrome)
        {
            // Keep refractive sheen and lens bevel strong even when the user lowers the surface tint slider,
            // so low-opacity slices still read as physical glass lenses rather than empty outlines.
            var sheenWeight = enabledFactor * Math.Clamp(EffectiveSliceOpacity + 0.42, 0.62, 0.96);
            if (GlassSheenBrush is { } sheen)
            {
                using (context.PushOpacity(sheenWeight))
                    context.DrawGeometry(sheen, null, wedge);
            }
            if (BevelPen() is { } bevelPen)
            {
                using (context.PushOpacity(enabledFactor * 0.58))
                    context.DrawGeometry(null, bevelPen, wedge);
            }
            if (SpecularPen() is { } specularPen)
            {
                using (context.PushOpacity(enabledFactor))
                    context.DrawGeometry(null, specularPen, wedge);
            }
        }

        // The accent fades in and out with the highlight (no floor: a floor made it vanish with a jump).
        // When Liquid Glass chrome is active, keep the accent fill translucent (0.42) with a glowing accent
        // rim so the native blur/refraction behind the slice shines through like coloured glass.
        if (emphasis > 0.01 && enabled)
        {
            var e = Math.Clamp(emphasis, 0, 1);
            var accentAlpha = hasGlassChrome ? 0.42 : 0.82;
            context.DrawGeometry(WithOpacity(AccentBrush, accentAlpha * e), null, wedge);

            if (hasGlassChrome)
            {
                using (context.PushOpacity(e))
                {
                    if (AccentBrush is not null)
                        context.DrawGeometry(null, new Pen(WithOpacity(AccentBrush, 0.52), 3.0), wedge);
                    if (GlassSheenBrush is { } hoverSheen)
                        context.DrawGeometry(hoverSheen, null, wedge);
                    if (HoverBorderPen() is { } hoverPen)
                        context.DrawGeometry(null, hoverPen, wedge);
                }
            }
        }
    }

    /// <summary>Draws the centre hub or progress plate with Liquid Glass translucency, sheen and directional rim.</summary>
    internal void DrawGlassDisc(DrawingContext context, Point center, double radius, bool cancelHot = false)
    {
        context.DrawEllipse(WithOpacity(CenterBrush, EffectiveCenterOpacity), BorderPen(), center, radius, radius);
        if (!Transparency.Reduced && SliceBorderBrush is not null)
        {
            var sheenWeight = Math.Clamp(EffectiveCenterOpacity + 0.36, 0.58, 0.94);
            if (GlassSheenBrush is { } sheen)
            {
                using (context.PushOpacity(sheenWeight))
                    context.DrawEllipse(sheen, null, center, radius, radius);
            }
            if (BevelPen() is { } bevelPen)
            {
                using (context.PushOpacity(0.55))
                    context.DrawEllipse(null, bevelPen, center, radius, radius);
            }
            if (SpecularPen() is { } specularPen)
                context.DrawEllipse(null, specularPen, center, radius, radius);
        }
        if (cancelHot)
            context.DrawEllipse(WithOpacity(DangerBrush, 0.22), null, center, radius, radius);
    }

    private void DrawSoftShadow(DrawingContext context, Point center, double r2)
    {
        // A radial gradient ring fakes a soft drop shadow without blur effects (cheap on any GPU).
        // Strictly 0 alpha inside (r2 - 18) so the inner hole seam remains 100% click-through.
        var shadowRadius = r2 + 14;
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), (r2 - 18) / shadowRadius),
                new GradientStop(Color.FromArgb(22, 0, 0, 0), (r2 - 9) / shadowRadius),
                new GradientStop(Color.FromArgb(42, 0, 0, 0), (r2 - 1) / shadowRadius),
                new GradientStop(Color.FromArgb(14, 0, 0, 0), (r2 + 6) / shadowRadius),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1),
            },
        };
        var opening = _open.Length == 0 ? 1 : Math.Clamp(_open.Average(), 0, 1);
        using (context.PushOpacity(opening * ShadowCoverage))
            context.DrawEllipse(brush, null, new Point(center.X, center.Y + 4), shadowRadius, shadowRadius);
    }

    private void DrawCenter(DrawingContext context, Point center, double r1, Typeface bold, Typeface regular)
    {
        var pulse = 1 + _centerPulse.Value * 0.06;
        var radius = (r1 - 6) * pulse;
        var cancelHot = _externalCenterHot;
        DrawGlassDisc(context, center, radius, cancelHot);

        var maxWidth = radius * 1.6;
        var title = CachedText(CenterTitle ?? "", bold, 14, Foreground, maxWidth, maxLines: 2);
        var subtitle = CachedText(CenterSubtitle ?? "", regular, 11.5, cancelHot ? DangerBrush : SecondaryForeground, maxWidth, maxLines: 2);
        // "⟳ Tab back": small, under the rest, a little narrower (the hub is round), with room for its icons.
        var hintIcons = FlipArrowSize + (CenterHintWheel ? MouseWidth + MouseGap : 0) + IconGap;
        var hint = string.IsNullOrEmpty(CenterHint) ? null : CachedText(CenterHint, regular, 10.5, SecondaryForeground, maxWidth * 0.85 - hintIcons);
        var total = title.Height + (string.IsNullOrEmpty(CenterSubtitle) ? 0 : subtitle.Height + 2) + (hint is null ? 0 : hint.Height + 5);
        var y = center.Y - total / 2;
        var addHalo = !Transparency.Reduced && SliceBorderBrush is not null && SliceOpacity < 0.85;
        if (addHalo && !string.IsNullOrEmpty(CenterTitle))
        {
            var haloAlpha = Math.Clamp((0.90 - SliceOpacity) * 0.72, 0.15, 0.58);
            var titleHalo = CachedText(CenterTitle, bold, 14, CenterBrush, maxWidth, maxLines: 2);
            using (context.PushOpacity(haloAlpha))
                DrawCentered(context, titleHalo, center.X, y + 0.8);
        }
        DrawCentered(context, title, center.X, y);
        y += title.Height;
        if (!string.IsNullOrEmpty(CenterSubtitle))
        {
            if (addHalo && !cancelHot)
            {
                var haloAlpha = Math.Clamp((0.90 - SliceOpacity) * 0.68, 0.14, 0.54);
                var subHalo = CachedText(CenterSubtitle, regular, 11.5, CenterBrush, maxWidth, maxLines: 2);
                using (context.PushOpacity(haloAlpha))
                    DrawCentered(context, subHalo, center.X, y + 2.8);
            }
            DrawCentered(context, subtitle, center.X, y + 2);
            y += subtitle.Height + 2;
        }
        if (hint is not null)
        {
            using (context.PushOpacity(0.85))
            {
                // The flip arrow is drawn, not typed: a font without "↻" showed a box in its place (#554). A small mouse
                // comes first when the wheel flips and no key does. The icons stand at the start of the line: on its
                // left, or on its right in a right-to-left language, in reading order there too.
                var left = center.X - (hintIcons + hint.Width) / 2;
                var rtl = TextDirection == FlowDirection.RightToLeft;
                var textLeft = rtl ? left : left + hintIcons;
                var middle = y + 5 + hint.Height / 2;
                var x = rtl ? left + hint.Width + IconGap : left;
                var arrowFirst = rtl && CenterHintWheel;
                if (CenterHintWheel && !arrowFirst)
                {
                    DrawMouse(context, new Rect(x, middle - 5, MouseWidth, 10), SecondaryForeground);
                    x += MouseWidth + MouseGap;
                }
                DrawFlipArrow(context, new Point(x + FlipArrowSize / 2, middle), FlipArrowSize / 2, SecondaryForeground);
                x += FlipArrowSize + (arrowFirst ? MouseGap : IconGap);
                if (arrowFirst)
                    DrawMouse(context, new Rect(x, middle - 5, MouseWidth, 10), SecondaryForeground);
                DrawCentered(context, hint, textLeft + hint.Width / 2, y + 5);
            }
        }
    }

    // The hub hint's icons: the flip arrow, the mouse (wheel only) and the room between them and before the text. The
    // arrow's head reaches a little past its circle towards the mouse, so that gap is wider.
    private const double FlipArrowSize = 9, MouseWidth = 7, IconGap = 4, MouseGap = 6;

    /// <summary>
    /// A small arrow turning clockwise round <paramref name="center"/>, the "flip" in the hub hint: most of a circle with
    /// its head at the end.
    /// </summary>
    internal static void DrawFlipArrow(DrawingContext context, Point center, double radius, IBrush? brush)
    {
        var pen = new Pen(brush, 1.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        // Angles in radians from 3 o'clock, growing clockwise on screen (y points down). From 1 o'clock almost round to
        // 11 o'clock, leaving a gap at the top where the head points into.
        const double start = -Math.PI / 3, sweep = Math.PI * 5 / 3;
        Point At(double angle) => new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        var end = start + sweep;
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(At(start), false);
            g.ArcTo(At(end), new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, arc);

        // The head: two strokes back from the end, along the circle's tangent there and either side of it.
        var tip = At(end);
        var along = new Vector(-Math.Sin(end), Math.Cos(end)); // clockwise
        var outward = new Vector(Math.Cos(end), Math.Sin(end));
        var head = radius * 0.75;
        var back = tip - along * head;
        context.DrawLine(pen, back + outward * head * 0.6, tip);
        context.DrawLine(pen, back - outward * head * 0.6, tip);
    }

    /// <summary>An outline of a mouse with its wheel, filling <paramref name="box"/>.</summary>
    private static void DrawMouse(DrawingContext context, Rect box, IBrush? brush)
    {
        var pen = new Pen(brush, 1.1);
        context.DrawRectangle(null, pen, box, box.Width / 2, box.Width / 2);
        context.DrawLine(pen, new Point(box.Center.X, box.Top + 2), new Point(box.Center.X, box.Top + 4.5));
    }

    private void DrawLabel(DrawingContext context, DonutItem item, Point center, double radius, double angle,
        double maxWidth, double maxHeight, Typeface bold, Typeface regular, bool hot)
    {
        var foreground = hot ? OnAccentBrush : item.IsEnabled ? Foreground : SecondaryForeground;
        var width = Math.Clamp(maxWidth, 30, 120);
        var text = KeepKoreanWords(item.Label);
        var label = CachedText(text, bold, LabelSize(text, bold, width), foreground, width, maxLines: 2);
        var showCaption = !string.IsNullOrEmpty(item.Caption) &&
                          !string.Equals(item.Caption, item.Label, StringComparison.OrdinalIgnoreCase) &&
                          label.Height + 14 < maxHeight;
        var caption = showCaption
            ? CachedText(item.Caption!, regular, 10.5, hot ? OnAccentBrush : SecondaryForeground, width,
                maxLines: label.Height + 28 < maxHeight ? 2 : 1)
            : null;

        var total = label.Height + (caption?.Height ?? 0);
        var anchor = DonutGeometry.PointAt(center, radius, angle);
        var y = anchor.Y - total / 2;

        // Subtle contrast halo when background glass is highly translucent so labels stay readable over any background.
        if (!hot && !Transparency.Reduced && SliceBorderBrush is not null && SliceOpacity < 0.85 && item.IsEnabled)
        {
            var haloAlpha = Math.Clamp((0.90 - SliceOpacity) * 0.72, 0.15, 0.58);
            var haloText = CachedText(text, bold, LabelSize(text, bold, width), SliceBrush, width, maxLines: 2);
            using (context.PushOpacity(haloAlpha))
                DrawCentered(context, haloText, anchor.X, y + 0.8);
            if (caption is not null)
            {
                var capHalo = CachedText(item.Caption!, regular, 10.5, SliceBrush, width,
                    maxLines: label.Height + 28 < maxHeight ? 2 : 1);
                using (context.PushOpacity(haloAlpha))
                    DrawCentered(context, capHalo, anchor.X, y + label.Height + 0.8);
            }
        }

        DrawCentered(context, label, anchor.X, y);
        if (caption is not null)
            DrawCentered(context, caption, anchor.X, y + label.Height);
    }

    /// <summary>
    /// A slice's caption under its name: one line, or two where the slice is deep enough, so a caption wider than the
    /// slice ("Same format" on a small donut) wraps instead of ending in "Same for…".
    /// </summary>
    internal static FormattedText MakeCaption(string caption, Typeface typeface, IBrush? brush, double width,
        double labelHeight, double maxHeight) =>
        MakeText(caption, typeface, 10.5, brush, width, maxLines: labelHeight + 28 < maxHeight ? 2 : 1);

    private const double LabelFontSize = 13;
    private const double MinLabelFontSize = 10;
    private readonly Dictionary<(string Text, Typeface Typeface, double Width), double> _labelSizes = new();

    /// <summary>
    /// The size a slice's name is drawn at: 13, or smaller (down to 10) when one of its words is wider than the slice
    /// (<paramref name="width"/>), which would otherwise break in the middle ("Grayscal|e" on a thin slice). Words in
    /// Chinese or Japanese are left out: those break between any two characters.
    /// </summary>
    internal double LabelSize(string text, Typeface typeface, double width)
    {
        var line = Math.Round(width);
        var key = (text, typeface, line);
        if (_labelSizes.TryGetValue(key, out var size))
            return size;
        if (_labelSizes.Count >= MaxCachedWedges)
            _labelSizes.Clear();
        var widest = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !word.Any(IsHanOrKana))
            .Select(word => new FormattedText(word, CultureInfo.CurrentUICulture, TextDirection, typeface, LabelFontSize, null).Width)
            .DefaultIfEmpty(0)
            .Max();
        // A pixel to spare: a word exactly as wide as the line still wraps.
        size = widest < line ? LabelFontSize : Math.Max(MinLabelFontSize, LabelFontSize * (line - 1) / widest);
        return _labelSizes[key] = size;
    }

    /// <summary>Chinese characters, kana and the CJK symbols and full-width forms: a line may break between any two.</summary>
    private static bool IsHanOrKana(char c) =>
        c is >= '\u2E80' and <= '\u312F' or >= '\u3190' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' or >= '\uFF00' and <= '\uFFEF';

    /// <summary>
    /// Korean is written with spaces between words and wraps there, but the text layout also breaks between any two
    /// Hangul syllables ("ZIP 하나|로"). A word joiner between them keeps each word on one line.
    /// </summary>
    internal static string KeepKoreanWords(string text)
    {
        if (!text.Any(IsHangul))
            return text;
        var kept = new System.Text.StringBuilder(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            kept.Append(text[i]);
            if (i + 1 < text.Length && IsHangul(text[i]) && IsHangul(text[i + 1]))
                kept.Append('\u2060');
        }
        return kept.ToString();
    }

    private static bool IsHangul(char c) => c is >= '\uAC00' and <= '\uD7A3';

    private Point EditButtonCenter(int index)
    {
        var mid = DonutGeometry.MidAngle(index, Count);
        return DonutGeometry.PointAt(Center, OuterRadius - 14 + PopOut, mid);
    }

    private void DrawEditButton(DrawingContext context, Point at)
    {
        context.DrawEllipse(AccentBrush, new Pen(CenterBrush, 2), at, 12, 12);
        var bounds = PencilIcon.Bounds;
        var s = 13 / Math.Max(bounds.Width, bounds.Height);
        var matrix = Matrix.CreateTranslation(-bounds.Center.X, -bounds.Center.Y) * Matrix.CreateScale(s, s) * Matrix.CreateTranslation(at.X, at.Y);
        using (context.PushTransform(matrix))
            context.DrawGeometry(OnAccentBrush, null, PencilIcon);
    }

    /// <summary>
    /// Draws text horizontally centred on <paramref name="centerX"/>.
    /// The text is laid out with <see cref="TextAlignment.Center"/> inside a box of <see cref="FormattedText.MaxTextWidth"/>,
    /// so the box (not the measured <see cref="FormattedText.Width"/>) must be centred — otherwise short labels
    /// drift right by (box − text) / 2.
    /// </summary>
    internal static void DrawCentered(DrawingContext context, FormattedText text, double centerX, double top) =>
        context.DrawText(text, new Point(centerX - text.MaxTextWidth / 2, top));

    private Pen? BorderPen() => SliceBorderBrush is null ? null : new Pen(SliceBorderBrush, 1);

    private Pen? BevelPen() => GlassSheenBrush is null ? null : new Pen(GlassSheenBrush, 2.8);

    private Pen? SpecularPen() => GlassSpecularBrush is null ? null : new Pen(GlassSpecularBrush, 1.25);

    private Pen? HoverBorderPen() => (GlassHoverBorderBrush ?? GlassSpecularBrush) is { } brush ? new Pen(brush, 1.45) : null;

    // Text shaping is much more expensive than the slice transforms. Keep layouts across frames,
    // with all layout/paint inputs in the key so language, theme and hover changes remain correct.
    private readonly Dictionary<(string Text, Typeface Typeface, double Size, IBrush? Brush, double Width, int Lines, string Culture), FormattedText> _textLayouts = new();

    private FormattedText CachedText(string text, Typeface typeface, double size, IBrush? brush, double maxWidth, int maxLines = 1)
    {
        var key = (text, typeface, size, brush, maxWidth, maxLines, CultureInfo.CurrentUICulture.Name);
        if (_textLayouts.TryGetValue(key, out var layout)) return layout;
        if (_textLayouts.Count >= 512) _textLayouts.Clear();
        return _textLayouts[key] = MakeText(text, typeface, size, brush, maxWidth, maxLines);
    }

    private static FormattedText MakeText(string text, Typeface typeface, double size, IBrush? brush, double maxWidth, int maxLines = 1) =>
        new(text, CultureInfo.CurrentUICulture, TextDirection, typeface, size, brush)
        {
            MaxTextWidth = Math.Max(10, maxWidth),
            MaxLineCount = maxLines,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };

    private static FlowDirection TextDirection => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
        ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    private static IBrush? WithOpacity(IBrush? brush, double opacity) => brush switch
    {
        ISolidColorBrush solid => new SolidColorBrush(solid.Color, solid.Opacity * opacity),
        _ => brush,
    };

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>The translucent slice where a dragged preset would land, with its name.</summary>
    private void DrawPreview(DrawingContext context, Point center, double r1, double r2, Typeface bold, Typeface regular)
    {
        var mid = _previewMid.Value;
        var span = _previewSpan.Value;
        var wedge = CreateWedge(center, r1, r2, mid, span);
        var outline = new Pen(WithOpacity(AccentBrush, 0.9), 1.5, new DashStyle([4, 3], 0));
        context.DrawGeometry(WithOpacity(AccentBrush, 0.22), outline, wedge);
        if (_previewLabel is null || span < 0.25)
            return;
        using (context.PushOpacity(Math.Clamp((span - 0.25) / 0.3, 0, 1) * 0.9))
            DrawLabel(context, new DonutItem { Id = "", Label = _previewLabel }, center, (r1 + r2) / 2, mid,
                span * (r1 + r2) / 2 - 10, r2 - r1 - 16, bold, regular, hot: false);
    }

    /// <summary>
    /// How round the slices' corners are: half the theme's softness (0 to 28), so the largest setting still leaves the
    /// slices clearly apart. <see cref="DonutGeometry.Wedge"/> shrinks it for slices too thin for it.
    /// </summary>
    internal double SliceCornerRadius => Math.Clamp(CornerRadius.TopLeft, 0, 28) * 0.5;

    private StreamGeometry CreateWedge(Point c, double r1, double r2, int index, int count) =>
        count == 1 ? CreateRing(c, r1, r2) : CreateWedge(c, r1, r2, DonutGeometry.MidAngle(index, count), DonutGeometry.Span(count));

    /// <summary>A rounded slice of <paramref name="span"/> radians centred on <paramref name="mid"/> (a ring when it is all of it).</summary>
    private StreamGeometry CreateWedge(Point c, double r1, double r2, double mid, double span) =>
        CreateWedge(c, r1, r2, mid, span, SliceCornerRadius);

    // Slices are drawn every frame while the donut animates, but their shapes only change with the size, the layout
    // or the softness: the shapes are kept and reused. Animations move them with transforms; only the edit mode's
    // sliding slices make new ones, and the cache starts over when it fills up.
    private readonly Dictionary<(Point C, double R1, double R2, double Mid, double Span, double Corner), StreamGeometry> _wedges = new();
    private const int MaxCachedWedges = 256;

    private StreamGeometry CreateWedge(Point c, double r1, double r2, double mid, double span, double cornerRadius)
    {
        var key = (c, r1, r2, mid, span, cornerRadius);
        if (_wedges.TryGetValue(key, out var cached))
            return cached;
        if (_wedges.Count >= MaxCachedWedges)
            _wedges.Clear();
        return _wedges[key] = BuildWedge(c, r1, r2, mid, span, cornerRadius);
    }

    /// <summary>A slice with a constant-width gap to its neighbours and corners rounded by <paramref name="cornerRadius"/>.</summary>
    private static StreamGeometry BuildWedge(Point c, double r1, double r2, double mid, double span, double cornerRadius)
    {
        if (span >= 2 * Math.PI - 0.001)
            return CreateRing(c, r1, r2);

        var outline = DonutGeometry.Wedge(c, r1, r2, mid, span, GapPixels, cornerRadius);
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(outline.Start, true);
        foreach (var step in outline.Steps)
        {
            if (step.IsArc)
                ctx.ArcTo(step.To, new Size(step.ArcRadius, step.ArcRadius), 0, step.IsLargeArc,
                    step.Clockwise ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
            else
                ctx.LineTo(step.To);
        }
        ctx.EndFigure(true);
        return geometry;
    }

    /// <summary>Full ring (used for a single slice and for the empty donut).</summary>
    private static StreamGeometry CreateRing(Point c, double r1, double r2)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.SetFillRule(FillRule.EvenOdd);
        foreach (var r in new[] { r2, r1 })
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), true);
            ctx.ArcTo(new Point(c.X, c.Y + r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.ArcTo(new Point(c.X, c.Y - r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }
        return geometry;
    }

    // ───────────────────────── Native Glass Mask Contours ─────────────────────────

    /// <summary>
    /// Populates <paramref name="circles"/> and <paramref name="slices"/> with the exact active geometry of the donut
    /// (hub, visible slices on current and flipping faces, expanded sub-rings, or progress plate) in control coordinates.
    /// The inner seam between the hub (<c>r1 - 6</c>) and slices (<c>r1</c>), inter-slice gaps, and outer empty space
    /// are excluded so the native macOS glass backdrop never blurs a rectangle behind the menu.
    /// </summary>
    internal void GetGlassMaskGeometry(
        List<(double CenterX, double CenterY, double OuterRadius, double InnerRadius)> circles,
        List<IReadOnlyList<(double X, double Y)>> slices)
    {
        circles.Clear();
        slices.Clear();

        var center = Center;
        var r2 = OuterRadius;
        var r1 = InnerRadius;
        var n = Count;

        if (_progressMode)
        {
            var collapseAll = CollapseEnd <= 0 ? 1 : Math.Clamp(_collapseClock / CollapseEnd, 0, 1);
            if (collapseAll < 1)
            {
                for (var i = 0; i < n; i++)
                {
                    var t = Math.Clamp((_collapseClock - i * CollapseStagger) / CollapseSeconds, 0, 1);
                    var c = EaseInCubic(t);
                    if (c >= 0.999)
                        continue;
                    var scale = 1 - 0.8 * c;
                    var transform =
                        Matrix.CreateTranslation(-center.X, -center.Y) *
                        Matrix.CreateRotation(c * 0.25) *
                        Matrix.CreateScale(scale, scale) *
                        Matrix.CreateTranslation(center.X, center.Y);
                    AddWedgeContour(circles, slices, center, r1, r2, DonutGeometry.MidAngle(i, n), DonutGeometry.Span(n), SliceCornerRadius, transform);
                }
                var plate = RingRadius + RingStroke + 2;
                var holeRadius = Lerp(r1 - 6, plate, EaseInCubic(collapseAll));
                if (holeRadius > 0.5)
                    circles.Add((center.X, center.Y, holeRadius, 0));
                return;
            }

            var fade = _fadeClock >= 0 ? Math.Clamp(_fadeClock / FadeSeconds, 0, 1) : 0;
            var handOff = _handOffClock >= 0 ? Math.Clamp(_handOffClock / HandOffSeconds, 0, 1) : 0;
            var ringScale = Math.Max(0, _ring.Value) * (1 - 0.15 * fade) * (1 - 0.7 * EaseInCubic(handOff));
            var opacity = (1 - fade) * (1 - handOff);
            if (ringScale > 0.01 && opacity > 0.01)
            {
                var plateRadius = (RingRadius + RingStroke + 2) * ringScale;
                circles.Add((center.X, center.Y, plateRadius, 0));
            }
            return;
        }

        // Centre hub circle (leaving the seam between r1 - 6 and r1 unmasked)
        var pulse = 1 + _centerPulse.Value * 0.06;
        var hubRadius = (r1 - 6) * pulse;
        if (hubRadius > 0.5)
            circles.Add((center.X, center.Y, hubRadius, 0));

        if (n == 0)
        {
            circles.Add((center.X, center.Y, r2, r1));
            return;
        }

        for (var i = 0; i < n; i++)
        {
            if (IsFlipping && SliceFlipWidth(false, i) <= 0.01)
                continue;
            var open = i < _open.Length ? _open[i] : 1;
            if (open <= 0.001)
                continue;
            var previewing = IsEditMode && i < _mid.Length;
            var hover = i < _hover.Length && _previewSlot < 0 ? _hover[i].Value : 0;
            var mid = previewing ? _mid[i].Value : DonutGeometry.MidAngle(i, n);
            var span = previewing ? _span[i].Value : DonutGeometry.Span(n);
            if (span < 0.01)
                continue;
            var offset = DonutGeometry.PointAt(new Point(0, 0), hover * PopOut, mid);
            var scale = 0.78 + 0.22 * open;
            var rotate = (1 - Math.Min(open, 1)) * -0.12;
            var transform =
                Matrix.CreateTranslation(-center.X, -center.Y) *
                Matrix.CreateRotation(rotate) *
                Matrix.CreateScale(scale * (1 + hover * 0.025), scale * (1 + hover * 0.025)) *
                Matrix.CreateTranslation(center.X + offset.X, center.Y + offset.Y) *
                SliceFlipTransform(false, i);
            AddWedgeContour(circles, slices, center, r1, r2, mid, span, SliceCornerRadius, transform);
        }

        if (_oldFace is { } old)
        {
            for (var i = 0; i < old.Length; i++)
            {
                var slice = old[i];
                if (SliceFlipWidth(true, i) <= 0.01 || slice.Open <= 0.001 || slice.Span < 0.01)
                    continue;
                var offset = DonutGeometry.PointAt(new Point(0, 0), slice.Hover * PopOut, slice.Mid);
                var scale = 0.78 + 0.22 * slice.Open;
                var rotate = (1 - Math.Min(slice.Open, 1)) * -0.12;
                var transform =
                    Matrix.CreateTranslation(-center.X, -center.Y) *
                    Matrix.CreateRotation(rotate) *
                    Matrix.CreateScale(scale * (1 + slice.Hover * 0.025), scale * (1 + slice.Hover * 0.025)) *
                    Matrix.CreateTranslation(center.X + offset.X, center.Y + offset.Y) *
                    SliceFlipTransform(true, i);
                AddWedgeContour(circles, slices, center, r1, r2, slice.Mid, slice.Span, SliceCornerRadius, transform);
            }
        }

        if (!IsEditMode && _shownExpanded >= 0 && _shownExpanded < n && _expand.Value > 0.01 &&
            Items![_shownExpanded].Children is { Count: > 0 } children)
        {
            AddSubRingContours(slices, center, _shownExpanded, children.Count, DonutGeometry.MidAngle(_shownExpanded, n),
                SubSpanOf(children.Count), _expand.Value, false);
        }

        if (_oldGroup is { } oldGroup && SliceFlipWidth(true, oldGroup.Index) > 0.01 && _oldFace is not null)
        {
            AddSubRingContours(slices, center, oldGroup.Index, oldGroup.Children.Count, _oldFace[oldGroup.Index].Mid,
                oldGroup.Span, oldGroup.Expansion, true);
        }
    }

    private void AddSubRingContours(List<IReadOnlyList<(double X, double Y)>> slices, Point center, int group,
        int childCount, double parentMid, double subSpan, double expansion, bool oldFace)
    {
        var (inner, outer) = SubRadii;
        var t = Math.Max(0, expansion);
        var anchor = DonutGeometry.PointAt(center, inner, parentMid);
        var scale = 0.8 + 0.2 * t;
        var transform = Matrix.CreateTranslation(-anchor.X, -anchor.Y) * Matrix.CreateScale(scale, scale) *
                        Matrix.CreateTranslation(anchor.X, anchor.Y) * SliceFlipTransform(oldFace, group);
        for (var k = 0; k < childCount; k++)
        {
            var mid = DonutGeometry.SubMidAngle(parentMid, k, childCount, subSpan);
            slices.Add(SampleWedgeOutline(center, inner, outer, mid, subSpan, SliceCornerRadius, transform));
        }
    }

    private static void AddWedgeContour(
        List<(double CenterX, double CenterY, double OuterRadius, double InnerRadius)> circles,
        List<IReadOnlyList<(double X, double Y)>> slices,
        Point center, double r1, double r2, double mid, double span, double cornerRadius, Matrix transform)
    {
        if (span >= 2 * Math.PI - 0.001 && transform.IsIdentity)
        {
            circles.Add((center.X, center.Y, r2, r1));
            return;
        }
        slices.Add(SampleWedgeOutline(center, r1, r2, mid, span, cornerRadius, transform));
    }

    private static IReadOnlyList<(double X, double Y)> SampleWedgeOutline(
        Point center, double r1, double r2, double mid, double span, double cornerRadius, Matrix transform)
    {
        static (double X, double Y) Apply(Point p, Matrix m)
        {
            var tp = p.Transform(m);
            return (tp.X, tp.Y);
        }

        var outline = DonutGeometry.Wedge(center, r1, r2, mid, Math.Min(span, 2 * Math.PI - 0.002), GapPixels, cornerRadius);
        var pts = new List<(double X, double Y)>(36);
        var cur = outline.Start;
        pts.Add(Apply(cur, transform));

        foreach (var step in outline.Steps)
        {
            if (!step.IsArc)
            {
                cur = step.To;
                pts.Add(Apply(cur, transform));
                continue;
            }

            var dx = step.To.X - cur.X;
            var dy = step.To.Y - cur.Y;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            var r = step.ArcRadius;
            if (chord < 0.001 || r < 0.001)
            {
                cur = step.To;
                pts.Add(Apply(cur, transform));
                continue;
            }

            // Reconstruct the circle centre of the SVG-style arc from cur -> step.To
            var mx = (cur.X + step.To.X) * 0.5;
            var my = (cur.Y + step.To.Y) * 0.5;
            var h = Math.Sqrt(Math.Max(0, r * r - chord * chord * 0.25));
            var sign = step.Clockwise != step.IsLargeArc ? 1.0 : -1.0;
            var cx = mx + sign * (-dy / chord) * h;
            var cy = my + sign * (dx / chord) * h;

            var a0 = Math.Atan2(cur.Y - cy, cur.X - cx);
            var a1 = Math.Atan2(step.To.Y - cy, step.To.X - cx);
            var da = a1 - a0;
            if (step.Clockwise)
            {
                while (da <= 0)
                    da += 2 * Math.PI;
            }
            else
            {
                while (da >= 0)
                    da -= 2 * Math.PI;
            }

            var subdivisions = r >= r1 - 1 ? 12 : 4;
            for (var s = 1; s <= subdivisions; s++)
            {
                var a = a0 + da * s / subdivisions;
                pts.Add(Apply(new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a)), transform));
            }
            cur = step.To;
        }

        return pts;
    }
}
