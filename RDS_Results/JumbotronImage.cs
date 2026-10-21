using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RDS_Results;

static class JumbotronImage
{
    const int MaxWidth = 1920;

    public static (string Mime, string Data) Encode(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return ("", "");

        try
        {
            using var input = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            BitmapSource source = frame;
            if (frame.PixelWidth > MaxWidth)
            {
                var scale = MaxWidth / (double)frame.PixelWidth;
                source = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            return ("image/jpeg", Convert.ToBase64String(output.ToArray()));
        }
        catch (Exception)
        {
            return ("", "");
        }
    }
}
