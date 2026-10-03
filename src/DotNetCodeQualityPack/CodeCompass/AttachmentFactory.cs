using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;


namespace DotNetCodeQualityPack.CodeCompass
{
    /// <summary>Creates attachments from files, pasted images and the clipboard.</summary>
    internal static class AttachmentFactory
    {
        public const long MaxTextFileBytes = 256 * 1024;
        public const long MaxImageFileBytes = 15 * 1024 * 1024;
        private const int MaxImageSide = 1600;

        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
        };

        private static readonly HashSet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".pdb", ".zip", ".7z", ".rar", ".nupkg", ".vsix", ".msi", ".pdf", ".docx", ".xlsx", ".pptx", ".bin", ".obj"
        };

        public static string FileFilter
        {
            get
            {
                return "Code and text|*.cs;*.vb;*.xaml;*.xml;*.json;*.config;*.csproj;*.sln;*.sql;*.js;*.ts;*.cshtml;*.razor;*.html;*.css;*.md;*.txt;*.log;*.yml;*.yaml"
                       + "|Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp"
                       + "|All files|*.*";
            }
        }

        /// <summary>Returns an attachment, or throws <see cref="InvalidOperationException"/> with a user-friendly reason.</summary>
        public static Attachment FromFile(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new InvalidOperationException(info.Name + " no longer exists.");

            var ext = info.Extension;
            if (ImageExtensions.Contains(ext))
            {
                if (info.Length > MaxImageFileBytes) throw new InvalidOperationException(info.Name + " is larger than 15 MB.");
                var bitmap = LoadBitmap(path);
                return FromImage(bitmap, info.Name);
            }

            if (BinaryExtensions.Contains(ext))
            {
                throw new InvalidOperationException(info.Name + " is a binary file. Attach code, text or images.");
            }

            if (info.Length > MaxTextFileBytes)
            {
                throw new InvalidOperationException(info.Name + " is larger than 256 KB. Attach a smaller file or paste the relevant part.");
            }

            var text = File.ReadAllText(path);
            if (text.IndexOf('\0') >= 0) throw new InvalidOperationException(info.Name + " looks like a binary file.");

            return new Attachment
            {
                Name = info.Name,
                Kind = AttachmentKind.Text,
                TextContent = text,
                SizeBytes = info.Length
            };
        }

        public static Attachment FromImage(BitmapSource image, string name)
        {
            var scaled = Downscale(image);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(scaled));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return new Attachment
                {
                    Name = name ?? "pasted-image-" + DateTime.Now.ToString("HHmmss") + ".png",
                    Kind = AttachmentKind.Image,
                    Base64 = Convert.ToBase64String(ms.ToArray()),
                    SizeBytes = ms.Length
                };
            }
        }

        public static Attachment FromText(string text, string name)
        {
            return new Attachment
            {
                Name = name,
                Kind = AttachmentKind.Text,
                TextContent = text,
                SizeBytes = Encoding.UTF8.GetByteCount(text ?? string.Empty)
            };
        }

        /// <summary>True when the clipboard holds something we turn into attachments (an image or files).</summary>
        public static bool ClipboardHasAttachable()
        {
            try
            {
                return Clipboard.ContainsImage() || Clipboard.ContainsFileDropList();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Reads images and files from the clipboard. Problems are returned in <paramref name="errors"/>.</summary>
        public static List<Attachment> FromClipboard(List<string> errors)
        {
            var result = new List<Attachment>();
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    StringCollection files = Clipboard.GetFileDropList();
                    result.AddRange(FromPaths(files.Cast<string>(), errors));
                }
                else if (Clipboard.ContainsImage())
                {
                    var image = ReadClipboardImage();
                    if (image != null) result.Add(FromImage(image, null));
                }
            }
            catch (Exception ex)
            {
                errors.Add("Could not read the clipboard: " + ex.Message);
            }

            return result;
        }

        public static List<Attachment> FromPaths(IEnumerable<string> paths, List<string> errors)
        {
            var result = new List<Attachment>();
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    errors.Add(Path.GetFileName(path) + " is a folder. Attach individual files.");
                    continue;
                }

                try
                {
                    result.Add(FromFile(path));
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add(ex.Message);
                }
                catch (Exception ex)
                {
                    errors.Add(Path.GetFileName(path) + ": " + ex.Message);
                }
            }

            return result;
        }

        public static BitmapSource ToBitmap(Attachment attachment)
        {
            if (attachment == null || attachment.Kind != AttachmentKind.Image || string.IsNullOrEmpty(attachment.Base64)) return null;
            try
            {
                var bytes = Convert.FromBase64String(attachment.Base64);
                var image = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = ms;
                    image.EndInit();
                }

                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Prefers the "PNG" clipboard format (Snipping Tool, browsers, Teams) because WPF's
        /// Clipboard.GetImage() often returns screenshots with a broken alpha channel (all black).
        /// </summary>
        private static BitmapSource ReadClipboardImage()
        {
            try
            {
                if (Clipboard.ContainsData("PNG"))
                {
                    var stream = Clipboard.GetData("PNG") as Stream;
                    if (stream != null)
                    {
                        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        var frame = decoder.Frames[0];
                        frame.Freeze();
                        return frame;
                    }
                }
            }
            catch (Exception)
            {
                // Fall back to the DIB format below.
            }

            var dib = Clipboard.GetImage();
            if (dib == null) return null;

            // Drop the (often invalid) alpha channel.
            var opaque = new FormatConvertedBitmap(dib, PixelFormats.Bgr32, null, 0);
            opaque.Freeze();
            return opaque;
        }

        private static BitmapSource LoadBitmap(string path)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }

        private static BitmapSource Downscale(BitmapSource source)
        {
            var longest = Math.Max(source.PixelWidth, source.PixelHeight);
            if (longest <= MaxImageSide) return source;

            var scale = (double)MaxImageSide / longest;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            scaled.Freeze();
            return scaled;
        }
    }
}
