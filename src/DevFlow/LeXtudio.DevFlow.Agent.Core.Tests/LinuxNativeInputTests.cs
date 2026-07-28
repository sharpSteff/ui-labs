using System.Runtime.InteropServices;
using LeXtudio.DevFlow.Agent.Core;
using Xunit;

namespace LeXtudio.DevFlow.Agent.Core.Tests;

/// <summary>
/// Covers the X11/XTEST mouse-injection backend. The gesture assertions read
/// the pointer back from the X server through a second connection, so they
/// verify what the server actually did rather than what the injection call
/// claimed. On a host without an X display (or off Linux) the suite falls back
/// to asserting the diagnostics contract, which is what agent responses quote.
/// </summary>
public class LinuxNativeInputTests
{
    private const uint Button1Mask = 0x100;

    private static bool CanInject =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && LinuxNativeInput.IsMouseInjectionAvailable;

    [Fact]
    public void UnavailableReason_ExplainsWhyInjectionIsOff()
    {
        if (CanInject)
        {
            Assert.Null(LinuxNativeInput.MouseInjectionUnavailableReason);
            return;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Off Linux the backend is simply not the one in play, and the
            // agent picks a different note.
            Assert.Null(LinuxNativeInput.MouseInjectionUnavailableReason);
            return;
        }

        var reason = LinuxNativeInput.MouseInjectionUnavailableReason;
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void DragMoveAndRelease_AreRejectedWithoutAPress()
    {
        // No press has been issued, so the decomposed follow-ups must refuse
        // rather than silently synthesise a move with no button down.
        Assert.False(LinuxNativeInput.TryDragMoveTo(10, 10));
        Assert.False(LinuxNativeInput.TryRelease(10, 10));
        Assert.False(LinuxNativeInput.IsButtonHeld);
    }

    [Fact]
    public void MouseMove_MovesTheServerPointer()
    {
        Assert.SkipUnless(CanInject, "no X11 display with XTEST available");

        Assert.True(LinuxNativeInput.TryMouseMove(120, 90));

        var (x, y, _) = QueryPointer();
        Assert.Equal(120, x);
        Assert.Equal(90, y);
    }

    [Fact]
    public void PressDragMoveRelease_HoldsTheButtonAcrossCalls()
    {
        Assert.SkipUnless(CanInject, "no X11 display with XTEST available");

        try
        {
            Assert.True(LinuxNativeInput.TryPressDown(60, 70));
            var pressed = QueryPointer();
            Assert.Equal((60, 70), (pressed.X, pressed.Y));
            Assert.True((pressed.Mask & Button1Mask) != 0, "button 1 should be down after press");
            Assert.True(LinuxNativeInput.IsButtonHeld);

            // The point of the decomposed path: the button survives into the
            // next, separate request.
            Assert.True(LinuxNativeInput.TryDragMoveTo(240, 260));
            var dragged = QueryPointer();
            Assert.Equal((240, 260), (dragged.X, dragged.Y));
            Assert.True((dragged.Mask & Button1Mask) != 0, "button 1 should stay down while dragging");

            Assert.True(LinuxNativeInput.TryRelease(250, 270));
            var released = QueryPointer();
            Assert.Equal((250, 270), (released.X, released.Y));
            Assert.True((released.Mask & Button1Mask) == 0, "button 1 should be up after release");
            Assert.False(LinuxNativeInput.IsButtonHeld);
        }
        finally
        {
            if (LinuxNativeInput.IsButtonHeld)
                LinuxNativeInput.TryRelease(250, 270);
        }
    }

    [Fact]
    public void MouseDrag_LandsOnTargetAndLeavesNoHeldButton()
    {
        Assert.SkipUnless(CanInject, "no X11 display with XTEST available");

        Assert.True(LinuxNativeInput.TryMouseDrag(40, 40, 300, 200, steps: 8, stepDelayMs: 4, holdAfterDownMs: 20));

        var (x, y, mask) = QueryPointer();
        Assert.Equal(300, x);
        Assert.Equal(200, y);
        Assert.True((mask & Button1Mask) == 0, "a completed drag must not leave the button down");
        Assert.False(LinuxNativeInput.IsButtonHeld);
    }

    [Fact]
    public void MouseClick_EndsWithTheButtonReleased()
    {
        Assert.SkipUnless(CanInject, "no X11 display with XTEST available");

        Assert.True(LinuxNativeInput.TryMouseClick(180, 160, clickCount: 2));

        var (x, y, mask) = QueryPointer();
        Assert.Equal(180, x);
        Assert.Equal(160, y);
        Assert.True((mask & Button1Mask) == 0, "a completed click must not leave the button down");
    }

    private static (int X, int Y, uint Mask) QueryPointer()
    {
        var display = XOpenDisplay(IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, display);
        try
        {
            XQueryPointer(display, XDefaultRootWindow(display), out _, out _,
                out var rootX, out var rootY, out _, out _, out var mask);
            return (rootX, rootY, mask);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);
}
