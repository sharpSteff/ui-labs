using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace LeXtudio.DevFlow.Agent.Core;

[SupportedOSPlatform("linux")]
public static class LinuxNativeInput
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXtst = "libXtst.so.6";

    private const uint XK_Return = 0xFF0D;
    private const uint XK_BackSpace = 0xFF08;
    private const uint XK_Shift_L = 0xFFE1;
    private const uint XK_a = 0x0061;

    public static bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && TryGetDisplay() != IntPtr.Zero;

    public static bool SendUnicodeText(string text)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || string.IsNullOrEmpty(text))
            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        var display = TryGetDisplay();
        if (display == IntPtr.Zero)
            return false;

        try
        {
            foreach (var ch in text)
            {
                if (!SendChar(display, ch))
                    return false;
            }

            XFlush(display);
            return true;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    public static bool SendReturn() => SendKeysym(XK_Return);

    public static bool SendBackspace() => SendKeysym(XK_BackSpace);

    public static bool SendSelectAll()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        var display = TryGetDisplay();
        if (display == IntPtr.Zero)
            return false;

        try
        {
            var ctrl = XStringToKeysym("Control_L");
            var a = XStringToKeysym("a");
            var ctrlCode = XKeysymToKeycode(display, ctrl);
            var aCode = XKeysymToKeycode(display, a);
            if (ctrlCode == 0 || aCode == 0)
                return false;

            XTestFakeKeyEvent(display, ctrlCode, true, 0);
            XTestFakeKeyEvent(display, aCode, true, 0);
            XTestFakeKeyEvent(display, aCode, false, 0);
            XTestFakeKeyEvent(display, ctrlCode, false, 0);
            XFlush(display);
            return true;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static bool SendKeysym(uint keysym)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        var display = TryGetDisplay();
        if (display == IntPtr.Zero)
            return false;

        try
        {
            var code = XKeysymToKeycode(display, (UIntPtr)keysym);
            if (code == 0)
                return false;

            XTestFakeKeyEvent(display, code, true, 0);
            XTestFakeKeyEvent(display, code, false, 0);
            XFlush(display);
            return true;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static bool SendChar(IntPtr display, char ch)
    {
        // ASCII / Latin-1 only for V1: keysym == codepoint for these ranges.
        // Arbitrary Unicode would require remapping a spare keycode via
        // XChangeKeyboardMapping (xdotool's approach) — TODO.
        if (ch > 0xFF)
            return false;

        UIntPtr keysym;
        bool needsShift = false;

        if (ch == ' ')
        {
            keysym = (UIntPtr)0x0020;
        }
        else if (ch is >= 'A' and <= 'Z')
        {
            keysym = (UIntPtr)(ch - 'A' + 'a');
            needsShift = true;
        }
        else if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
        {
            keysym = (UIntPtr)ch;
        }
        else
        {
            // Latin-1 fallback: keysym equals the codepoint for 0x20-0xFF.
            keysym = (UIntPtr)ch;
        }

        var code = XKeysymToKeycode(display, keysym);
        if (code == 0)
            return false;

        byte shiftCode = 0;
        if (needsShift)
        {
            shiftCode = XKeysymToKeycode(display, (UIntPtr)XK_Shift_L);
            if (shiftCode == 0)
                return false;
            XTestFakeKeyEvent(display, shiftCode, true, 0);
        }

        XTestFakeKeyEvent(display, code, true, 0);
        XTestFakeKeyEvent(display, code, false, 0);

        if (needsShift)
            XTestFakeKeyEvent(display, shiftCode, false, 0);

        return true;
    }

    private static IntPtr TryGetDisplay()
    {
        try
        {
            return XOpenDisplay(IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            return IntPtr.Zero;
        }
        catch (EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    // ── Mouse injection (XTEST pointer events) ─────────────────────────────
    //
    // Pointer events go through the same XTEST extension as the keyboard
    // helpers above, so they enter the X server's own input stream: the cursor
    // really moves and every client — including the agent's own window — sees
    // them exactly as it would a physical mouse. That is what the decomposed
    // press / drag-move / release gestures need, because the button state then
    // lives in the server and survives across the separate agent requests that
    // make up one drag.
    //
    // Coordinates are absolute root-window pixels with a top-left origin, the
    // same space WindowsNativeInput and MacOSNativeInput use.
    //
    // X11 sessions and Wayland sessions running XWayland (where the app is an
    // X client) are covered. A native Wayland session offers no equivalent
    // injection path to an unprivileged client — that needs libei or a
    // compositor virtual-pointer protocol — so IsMouseInjectionAvailable
    // reports false there and MouseInjectionUnavailableReason says why.

    // -1 asks XTEST to use the screen the pointer is currently on, which is the
    // right answer for both single-head and Xinerama-merged multi-head setups.
    private const int CurrentScreen = -1;
    private const uint LeftButton = 1;

    // Unlike the keyboard helpers, which open and close a connection per call,
    // a gesture spans several calls and several agent requests. One cached
    // connection behind one lock keeps those requests from interleaving
    // mid-gesture (X11 is only thread-safe after XInitThreads, and even then
    // interleaved requests would scramble the event order).
    private static readonly object _mouseGate = new();
    private static bool _mouseProbed;
    private static IntPtr _mouseDisplay;
    private static string? _mouseUnavailableReason;
    private static bool _buttonHeld;

    public static bool IsMouseInjectionAvailable
    {
        get
        {
            lock (_mouseGate)
            {
                EnsureMouseProbed();
                return _mouseDisplay != IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Why <see cref="IsMouseInjectionAvailable"/> is false, phrased for an
    /// agent response note; null when injection works or the host is not Linux.
    /// </summary>
    public static string? MouseInjectionUnavailableReason
    {
        get
        {
            lock (_mouseGate)
            {
                EnsureMouseProbed();
                return _mouseDisplay == IntPtr.Zero ? _mouseUnavailableReason : null;
            }
        }
    }

    /// <summary>True while a synthetic button-down issued here has not been released.</summary>
    public static bool IsButtonHeld
    {
        get
        {
            lock (_mouseGate)
            {
                return _buttonHeld;
            }
        }
    }

    public static bool TryMouseMove(double x, double y)
    {
        lock (_mouseGate)
        {
            return TryMoveUnsafe(x, y);
        }
    }

    public static bool TryMouseClick(double x, double y, int clickCount = 1)
    {
        if (clickCount < 1)
            clickCount = 1;

        lock (_mouseGate)
        {
            if (!TryMoveUnsafe(x, y))
                return false;

            for (var c = 0; c < clickCount; c++)
            {
                if (!TryButtonUnsafe(press: true))
                    return false;
                Sleep(50);
                if (!TryButtonUnsafe(press: false))
                    return false;
                if (c < clickCount - 1)
                    Sleep(80);
            }

            return true;
        }
    }

    public static bool TryMouseDrag(double fromX, double fromY, double toX, double toY,
        int steps = 24, int stepDelayMs = 16, int holdAfterDownMs = 200)
    {
        if (steps < 1)
            steps = 1;

        lock (_mouseGate)
        {
            if (!TryMoveUnsafe(fromX, fromY))
                return false;
            Sleep(stepDelayMs);

            if (!TryButtonUnsafe(press: true))
                return false;

            try
            {
                // Drag thresholds are time- as well as distance-based in most
                // toolkits (AvalonDock arms its tear-off on a held press), so
                // the hold after the down is load-bearing, not cosmetic.
                Sleep(holdAfterDownMs);

                for (var i = 1; i <= steps; i++)
                {
                    var t = (double)i / steps;
                    if (!TryMoveUnsafe(fromX + (toX - fromX) * t, fromY + (toY - fromY) * t))
                        return false;
                    Sleep(stepDelayMs);
                }

                return true;
            }
            finally
            {
                // Never leave the server with a stuck button, even on failure.
                TryButtonUnsafe(press: false);
            }
        }
    }

    public static bool TryPressDown(double x, double y)
    {
        lock (_mouseGate)
        {
            if (_buttonHeld)
                TryButtonUnsafe(press: false);

            return TryMoveUnsafe(x, y) && TryButtonUnsafe(press: true);
        }
    }

    public static bool TryDragMoveTo(double x, double y)
    {
        lock (_mouseGate)
        {
            if (!_buttonHeld)
                return false;

            return TryMoveUnsafe(x, y);
        }
    }

    public static bool TryRelease(double x, double y)
    {
        lock (_mouseGate)
        {
            if (!_buttonHeld)
                return false;

            var moved = TryMoveUnsafe(x, y);
            var released = TryButtonUnsafe(press: false);
            return moved && released;
        }
    }

    private static bool TryMoveUnsafe(double x, double y)
    {
        if (!TryGetMouseDisplayUnsafe(out var display))
            return false;

        try
        {
            if (XTestFakeMotionEvent(display, CurrentScreen, (int)Math.Round(x), (int)Math.Round(y), 0) == 0)
                return false;

            SyncUnsafe(display);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryButtonUnsafe(bool press)
    {
        if (!TryGetMouseDisplayUnsafe(out var display))
            return false;

        try
        {
            if (XTestFakeButtonEvent(display, LeftButton, press, 0) == 0)
                return false;

            SyncUnsafe(display);
            _buttonHeld = press;
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    // XFlush only queues the request locally, so the caller (and anything else
    // reading pointer state) can observe the world before the server has acted
    // on it — which shows up as a whole gesture lagging one event behind.
    // XSync round-trips, so by the time an injection call returns the event
    // really is in the server's input stream.
    private static void SyncUnsafe(IntPtr display) => XSync(display, discard: false);

    private static bool TryGetMouseDisplayUnsafe(out IntPtr display)
    {
        EnsureMouseProbed();
        display = _mouseDisplay;
        return display != IntPtr.Zero;
    }

    private static void EnsureMouseProbed()
    {
        if (_mouseProbed)
            return;

        _mouseProbed = true;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        var displayName = Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrWhiteSpace(displayName))
        {
            _mouseUnavailableReason = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
                ? "no X11 display: DISPLAY is not set."
                : "no X11 display: this is a native Wayland session. Native mouse injection needs XWayland (a DISPLAY the app is an X client of); Wayland-native injection via libei is not implemented yet.";
            return;
        }

        try
        {
            XInitThreads();
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                _mouseUnavailableReason = $"could not open X display '{displayName}'.";
                return;
            }

            if (XTestQueryExtension(display, out _, out _, out _, out _) == 0)
            {
                XCloseDisplay(display);
                _mouseUnavailableReason = $"the X server on '{displayName}' does not expose the XTEST extension.";
                return;
            }

            _mouseDisplay = display;
        }
        catch (DllNotFoundException)
        {
            _mouseUnavailableReason = "libXtst.so.6 / libX11.so.6 are not installed (Debian/Ubuntu: libxtst6).";
        }
        catch (EntryPointNotFoundException ex)
        {
            _mouseUnavailableReason = $"X11/XTEST entry point missing: {ex.Message}";
        }
    }

    private static void Sleep(int ms)
    {
        if (ms > 0)
            Thread.Sleep(ms);
    }

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XFlush(IntPtr display);

    [DllImport(LibX11)]
    private static extern UIntPtr XStringToKeysym([MarshalAs(UnmanagedType.LPStr)] string str);

    [DllImport(LibX11)]
    private static extern byte XKeysymToKeycode(IntPtr display, UIntPtr keysym);

    [DllImport(LibXtst)]
    private static extern int XTestFakeKeyEvent(IntPtr display, byte keycode, [MarshalAs(UnmanagedType.I1)] bool isPress, ulong delay);

    [DllImport(LibX11)]
    private static extern int XInitThreads();

    [DllImport(LibX11)]
    private static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);

    [DllImport(LibXtst)]
    private static extern int XTestQueryExtension(IntPtr display, out int eventBase, out int errorBase,
        out int majorVersion, out int minorVersion);

    [DllImport(LibXtst)]
    private static extern int XTestFakeMotionEvent(IntPtr display, int screenNumber, int x, int y, ulong delay);

    [DllImport(LibXtst)]
    private static extern int XTestFakeButtonEvent(IntPtr display, uint button,
        [MarshalAs(UnmanagedType.Bool)] bool isPress, ulong delay);
}
