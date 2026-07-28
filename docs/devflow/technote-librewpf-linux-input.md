# Technical Note: Mouse Injection on LibreWPF/Linux

> Audience: DevFlow maintainers. This documents how the WPF agent synthesizes
> pointer input when a LibreWPF app runs on Linux, why there are two independent
> routes, and what each one can and cannot reproduce.

## Background

The WPF agent drives real pointer gestures per host: `SendInput`/`mouse_event` on
Windows, CoreGraphics events (through the bundled CliclickSharp helper) on macOS.
On Linux there was no backend at all, so `POST /api/v1/ui/actions/drag` answered
*"native mouse injection is supported on Windows and macOS only"*, and the
decomposed `press` / `drag-move` / `release` endpoints required `cliclick`, which
is macOS-only. Every gesture-driven test — drag-to-dock, resize, tear-off —
was unreachable on Linux.

Two routes now cover it, tried in that order.

## Route 1: X11 XTEST (`LinuxNativeInput`)

`libXtst.so.6` posts motion and button events into the X server's own input
stream, so the pointer really moves and every client — including our own window —
sees them exactly as it would a physical mouse.

This is the preferred route because it reproduces gestures faithfully rather than
approximately:

- Code that polls global cursor or button state (the way a docking tear-off
  tracker does) observes the gesture, not just XAML routed events.
- The button state lives in the **server**, so a press survives across the
  separate agent requests that make up one decomposed drag. Nothing in the agent
  has to remember it.

Two implementation details are load-bearing:

- **`XSync`, not `XFlush`.** `XFlush` only queues the request locally, so a
  caller reading pointer state can observe the world before the server has acted
  on it — the whole gesture appears to lag one event behind. `XSync` round-trips,
  so by the time an injection call returns the event is really in the input
  stream.
- **One cached connection behind one lock.** A gesture spans several calls and
  several agent requests; interleaving them would scramble event order. (X11 is
  also only thread-safe after `XInitThreads`.)

Coordinates are absolute root-window pixels, top-left origin — the same space the
Windows and macOS backends use.

**Covers:** X11 sessions, and Wayland sessions running XWayland (where the app is
an X client).

**Does not cover:** a native Wayland session. An unprivileged client has no
equivalent injection path there; that needs libei or a compositor virtual-pointer
protocol. `LinuxNativeInput.IsMouseInjectionAvailable` reports false and
`MouseInjectionUnavailableReason` says so, and the agent quotes that reason back
in the response `note` instead of a generic failure.

## Route 2: LibreWPF's portable input pipeline

When XTEST is unavailable (native Wayland, or no display at all), the agent posts
input straight into LibreWPF's own pipeline via
`System.Windows.PortableWindowActivationService.ProcessInput(Window, PortableInputEventArgs)`.
The service is internal to the ported `PresentationFramework`, so the agent
reaches it by reflection, gated on its `IsEnabled` flag — which flips true only
once the ProGPU host has registered its activation callbacks. (A negative read is
therefore never cached; only a positive one is.)

This route is host-independent: it is the same one the macOS drag path already
used, and it works identically on Linux. Its limits are worth stating plainly:

- The **OS cursor does not move**. Anything reading global pointer state sees
  nothing. Responses carry a `note` saying so.
- Input is delivered to one of our own `Window`s, so it is not used for `global`
  gestures, which are screen-space by definition.
- A decomposed gesture's button state has to be tracked **in the agent** (which
  window was pressed), since no server is holding it. Moves keep going to the
  pressed window, matching what WPF capture does.
- Native popup windows are not targeted: the resolver walks `Application.Current.Windows`.
  AvalonDock's floating and overlay windows are real `Window`s, so docking
  gestures are covered; menu/tooltip popups are not.

## Response modes

The `mode` field says which route ran, so a failing test can be diagnosed without
guessing:

| `mode` | Meaning |
| --- | --- |
| `cliclick` | macOS, via the bundled CliclickSharp helper |
| `xtest` | Linux, real X11 pointer events |
| `native` / `native-global` | Windows `SendInput`, or macOS CoreGraphics |
| `portable-wpf` | in-process LibreWPF injection; OS cursor did not move |

## Testing

`LeXtudio.DevFlow.Agent.Core.Tests` covers the XTEST backend against a real X
server: it injects, then reads the pointer back over a *second* X connection with
`XQueryPointer`, asserting position and button mask. That verifies what the server
actually did rather than what the injection call claimed — it is what caught the
`XFlush`/`XSync` lag. The gesture tests skip when no display is present; the
diagnostics contract is asserted either way. CI runs them on Linux under Xvfb
(`libxtst6` is installed alongside the other GUI dependencies) and on Windows,
where the Linux-specific cases skip.
