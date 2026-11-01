using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ZeroStrap.UI.Utility
{
    /// <summary>Outcome of opening a media file with the in-box WPF media stack.</summary>
    public sealed record MediaProbeResult(bool Ok, string? Error, int Width, int Height, TimeSpan Duration);

    /// <summary>
    /// Opens candidate background media (without showing it) to find out whether WPF can play it, and captures a
    /// poster still from videos. All calls must be made on the UI thread; they are asynchronous and never block it.
    /// </summary>
    public static class MediaProbe
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        public const int MaxPosterWidth = 1280;

        public static readonly TimeSpan PosterTime = TimeSpan.FromSeconds(1);

        public static Task<MediaProbeResult> ProbeAsync(string path, bool video, TimeSpan? timeout = null) =>
            video ? ProbeVideoAsync(path, timeout ?? DefaultTimeout) : Task.FromResult(ProbeImage(path));

        private static MediaProbeResult ProbeImage(string path)
        {
            try
            {
                var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                    return new MediaProbeResult(false, "That image has no pixels.", 0, 0, TimeSpan.Zero);

                return new MediaProbeResult(true, null, frame.PixelWidth, frame.PixelHeight, TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or UnauthorizedAccessException or FileFormatException or ArgumentException)
            {
                return new MediaProbeResult(false, "That image could not be opened. It may be damaged.", 0, 0, TimeSpan.Zero);
            }
        }

        private static async Task<MediaProbeResult> ProbeVideoAsync(string path, TimeSpan timeout)
        {
            var player = new MediaPlayer { Volume = 0, IsMuted = true };
            try
            {
                var (opened, failure) = await OpenAsync(player, path, timeout);
                if (!opened)
                    return new MediaProbeResult(false, failure, 0, 0, TimeSpan.Zero);

                int w = player.NaturalVideoWidth, h = player.NaturalVideoHeight;
                if (w <= 0 || h <= 0)
                    return new MediaProbeResult(false, "That file has no video track that Windows can play.", 0, 0, TimeSpan.Zero);

                var duration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
                return new MediaProbeResult(true, null, w, h, duration);
            }
            finally
            {
                player.Close();
            }
        }

        /// <summary>Opens <paramref name="path"/> in the player and waits for MediaOpened, MediaFailed or the timeout.</summary>
        private static async Task<(bool Opened, string? Failure)> OpenAsync(MediaPlayer player, string path, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<(bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnOpened(object? s, EventArgs e) => tcs.TrySetResult((true, null));
            void OnFailed(object? s, ExceptionEventArgs e) => tcs.TrySetResult((false, "Windows could not play that video. It may be damaged or use a codec Windows doesn't include."));

            player.MediaOpened += OnOpened;
            player.MediaFailed += OnFailed;

            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = timeout };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                tcs.TrySetResult((false, "Opening that video took too long."));
            };

            try
            {
                timer.Start();
                player.Open(new Uri(path));
                return await tcs.Task;
            }
            catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or ArgumentException)
            {
                return (false, "That video could not be opened.");
            }
            finally
            {
                timer.Stop();
                player.MediaOpened -= OnOpened;
                player.MediaFailed -= OnFailed;
            }
        }

        /// <summary>
        /// Captures a still at about one second (or the middle of a shorter clip), scaled to at most
        /// <see cref="MaxPosterWidth"/> pixels wide, and writes it as a PNG. Returns false on any failure.
        /// </summary>
        public static async Task<bool> CapturePosterAsync(string videoPath, string posterPath, Action<string>? log = null)
        {
            var player = new MediaPlayer { Volume = 0, IsMuted = true, ScrubbingEnabled = true };
            string temp = posterPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                var (opened, failure) = await OpenAsync(player, videoPath, DefaultTimeout);
                if (!opened)
                {
                    log?.Invoke("Poster capture failed to open the video: " + failure);
                    return false;
                }

                int w = player.NaturalVideoWidth, h = player.NaturalVideoHeight;
                if (w <= 0 || h <= 0)
                    return false;

                var target = PosterTime;
                if (player.NaturalDuration.HasTimeSpan)
                {
                    var half = TimeSpan.FromTicks(player.NaturalDuration.TimeSpan.Ticks / 2);
                    if (half < target)
                        target = half;
                }

                int tw = Math.Min(w, MaxPosterWidth);
                int th = Math.Max(1, (int)Math.Round(h * (tw / (double)w)));

                // Scrubbing needs the clock started once; then pause and seek so a decoded frame is available to render.
                player.Play();
                await Task.Delay(120);
                player.Pause();
                player.Position = target;

                for (int attempt = 0; attempt < 4; attempt++)
                {
                    await Task.Delay(attempt == 0 ? 450 : 500);

                    var bitmap = Render(player, tw, th);
                    if (IsBlank(bitmap))
                        continue;

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));

                    Directory.CreateDirectory(Path.GetDirectoryName(posterPath)!);
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
                        encoder.Save(stream);

                    File.Move(temp, posterPath, overwrite: true);
                    return true;
                }

                log?.Invoke("Poster capture produced no usable frame.");
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                log?.Invoke("Poster capture failed: " + ex.Message);
                return false;
            }
            finally
            {
                player.Close();
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static RenderTargetBitmap Render(MediaPlayer player, int width, int height)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawVideo(player, new Rect(0, 0, width, height));

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>True when a sampled grid of pixels is entirely transparent or black (the frame was not decoded yet).</summary>
        private static bool IsBlank(BitmapSource bitmap)
        {
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            var pixel = new byte[4];

            for (int gy = 1; gy <= 4; gy++)
            {
                for (int gx = 1; gx <= 4; gx++)
                {
                    int x = Math.Min(w - 1, gx * w / 5), y = Math.Min(h - 1, gy * h / 5);
                    bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);

                    if (pixel[3] != 0 && (pixel[0] > 6 || pixel[1] > 6 || pixel[2] > 6))
                        return false;
                }
            }

            return true;
        }
    }
}
