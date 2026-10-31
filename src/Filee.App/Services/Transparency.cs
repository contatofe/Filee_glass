// Global "reduce transparency" switch. Mirrors Motion.cs: follows the user's explicit choice or
// the operating system's accessibility preference (accessibilityDisplayShouldReduceTransparency on macOS).

using Avalonia.Controls;

namespace Filee.App.Services;

/// <summary>Whether glass translucency and backdrop blur are enabled.</summary>
public static class Transparency
{
    private static readonly List<WeakReference<Window>> Windows = [];

    /// <summary>False when the user (or the OS) asked for reduced transparency.</summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>True when the user (or the OS) asked for reduced transparency.</summary>
    public static bool Reduced => !Enabled;

    public static event EventHandler? Changed;

    private static bool? _reduceTransparency;
    private static Func<bool>? _systemPrefersReduced;

    /// <summary>
    /// Configures the user's choice (<c>null</c> follows the system) and how to query the OS preference.
    /// </summary>
    public static void Configure(bool? reduceTransparency, Func<bool> systemPrefersReduced)
    {
        _reduceTransparency = reduceTransparency;
        _systemPrefersReduced = systemPrefersReduced;
        Set(!Current());
    }

    /// <summary>Reads the system's setting again when following it (call before showing a window).</summary>
    public static void Refresh()
    {
        if (_reduceTransparency is null && _systemPrefersReduced is not null && Enabled == Current())
            Set(!Current());
    }

    private static bool Current()
    {
        try
        {
            return _reduceTransparency ?? _systemPrefersReduced?.Invoke() ?? false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        Enabled = enabled;
        lock (Windows)
        {
            Windows.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var weak in Windows)
                if (weak.TryGetTarget(out var window))
                    Apply(window);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Call once per window so its styles follow the setting.</summary>
    public static void Track(Window window)
    {
        lock (Windows)
        {
            Windows.RemoveAll(w => !w.TryGetTarget(out _));
            Windows.Add(new WeakReference<Window>(window));
        }
        Apply(window);
    }

    private static void Apply(Window window) => window.Classes.Set("reduced-transparency", !Enabled);
}

