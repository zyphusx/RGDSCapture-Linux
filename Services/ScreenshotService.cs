using System.IO;
using Avalonia.Media.Imaging;
using RGDSCapture.Core;

namespace RGDSCapture.Services
{
    /// <summary>Saves the current frame of each screen as a timestamped PNG.</summary>
    public static class ScreenshotService
    {
        /// <summary>
        /// Saves any non-null bitmaps. Returns the number of files written.
        /// Must be called on the UI thread (reads the live WriteableBitmaps).
        /// </summary>
        public static int SaveAll(Bitmap? top, Bitmap? bottom)
        {
            Directory.CreateDirectory(AppPaths.ScreenshotsDir);
            string ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            int saved = 0;

            if (top != null)
            {
                SavePng(top, Path.Combine(AppPaths.ScreenshotsDir, $"top_{ts}.png"));
                saved++;
            }
            if (bottom != null)
            {
                SavePng(bottom, Path.Combine(AppPaths.ScreenshotsDir, $"bottom_{ts}.png"));
                saved++;
            }
            return saved;
        }

        // Avalonia's Bitmap.Save writes PNG, so there is no encoder to set up
        // the way the Windows build has to.
        private static void SavePng(Bitmap bmp, string path) => bmp.Save(path);
    }
}
