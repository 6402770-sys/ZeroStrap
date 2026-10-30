using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace ZeroStrap.Extensions
{
    public static class IconEx
    {
        public static Icon GetSized(this Icon icon, int width, int height) => new(icon, new Size(width, height));

        public static ImageSource GetImageSource(this Icon icon, bool handleException = true)
        {
            using MemoryStream stream = new();
            icon.Save(stream);
            stream.Seek(0, SeekOrigin.Begin);

            if (handleException)
            {
                try
                {
                    return LargestFrame(stream);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("IconEx::GetImageSource", ex);
                    Frontend.ShowMessageBox(string.Format(Strings.Dialog_IconLoadFailed, ex.Message));
                    return BootstrapperIcon.IconZeroStrap.GetIcon().GetImageSource(false);
                }
            }
            else
            {
                return LargestFrame(stream);
            }
        }

        /// <summary>
        /// BitmapFrame.Create on an .ico returns its first entry (usually 16x16), which then gets
        /// upscaled and looks pixelated. Decode every entry and keep the biggest one instead.
        /// </summary>
        private static BitmapSource LargestFrame(Stream stream)
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            BitmapSource best = decoder.Frames[0];
            foreach (var frame in decoder.Frames)
            {
                if (frame.PixelWidth > best.PixelWidth)
                    best = frame;
            }

            best.Freeze();
            return best;
        }
    }
}
