using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeCodeVS.Cli;

namespace ClaudeCodeVS.UI
{
    /// <summary>A pasted or dropped image, encoded for the model plus a small thumbnail for the UI.</summary>
    internal sealed class ImageAttachment
    {
        private const int MaxSide = 1568;            // larger images are downscaled by the API anyway
        private const int MaxEncodedBytes = 3500000; // stay well under the per-image request limit

        public ImageContent Content { get; private set; }

        public BitmapSource Thumbnail { get; private set; }

        public string Name { get; private set; }

        public int Bytes { get; private set; }

        public static ImageAttachment FromBitmap(BitmapSource source, string name)
        {
            // Clipboard bitmaps often carry a bogus alpha channel; drop it.
            BitmapSource image = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
            double scale = Math.Min(1.0, (double)MaxSide / Math.Max(image.PixelWidth, image.PixelHeight));
            if (scale < 1.0)
            {
                image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            }

            var bytes = Encode(new PngBitmapEncoder(), image);
            var mediaType = "image/png";
            if (bytes.Length > MaxEncodedBytes)
            {
                bytes = Encode(new JpegBitmapEncoder { QualityLevel = 85 }, image);
                mediaType = "image/jpeg";
            }

            double thumbScale = Math.Min(1.0, 96.0 / Math.Max(image.PixelWidth, image.PixelHeight));
            var thumbnail = new TransformedBitmap(image, new ScaleTransform(thumbScale, thumbScale));
            thumbnail.Freeze();

            return new ImageAttachment
            {
                Content = new ImageContent(mediaType, Convert.ToBase64String(bytes)),
                Thumbnail = thumbnail,
                Name = name,
                Bytes = bytes.Length,
            };
        }

        public static ImageAttachment FromFile(string path)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return FromBitmap(bitmap, Path.GetFileName(path));
        }

        public static bool IsImageFile(string path)
        {
            var extension = Path.GetExtension(path)?.ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".gif" || extension == ".bmp";
        }

        private static byte[] Encode(BitmapEncoder encoder, BitmapSource image)
        {
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }
    }
}
