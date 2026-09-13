using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace RGDSCapture.Core
{
    /// <summary>
    /// Which monitor a window is on, and how much of it is usable.
    ///
    /// The primary monitor is the wrong answer for this app more often than
    /// not — the capture window is typically parked on a second screen of a
    /// different size while the game is played on the first.
    ///
    /// The Windows build reaches for MonitorFromWindow/GetMonitorInfo to get
    /// this; Avalonia exposes the same information portably through
    /// <see cref="Screens"/>, so this is the whole of it.
    /// </summary>
    public static class ScreenMetrics
    {
        /// <summary>
        /// Usable area of the monitor <paramref name="window"/> sits on, in
        /// device-independent units. Falls back to the primary monitor before
        /// the window has a platform handle, and to a conservative 1280x720
        /// if the backend reports no screens at all (headless, or an X11
        /// server that answers no RandR query).
        /// </summary>
        public static Size WorkArea(Window window)
        {
            Screens? screens = window.Screens;

            Screen? screen = null;
            if (screens is not null)
            {
                // Null until the window is shown and has a handle.
                screen = screens.ScreenFromWindow(window) ?? screens.Primary;
            }

            if (screen is null) return new Size(1280, 720);

            // WorkingArea is in physical pixels; layout happens in DIPs.
            double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
            return new Size(
                screen.WorkingArea.Width / scale,
                screen.WorkingArea.Height / scale);
        }
    }
}
