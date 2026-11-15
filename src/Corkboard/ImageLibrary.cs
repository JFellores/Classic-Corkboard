using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Corkboard;

internal sealed class ImageLibrary(string folder)
{
    private const int LongestEdge = 1200;

    public string Import(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 35 * 1024 * 1024) throw new InvalidDataException("Choose an image smaller than 35 MB.");
        using var original = Image.FromStream(input);
        if ((long)original.Width * original.Height > 60_000_000)
            throw new InvalidDataException("Choose an image under 60 megapixels.");
        bool jpeg = original.RawFormat.Guid == ImageFormat.Jpeg.Guid;
        ApplyOrientation(original);

        double scale = Math.Min(1.0, (double)LongestEdge / Math.Max(original.Width, original.Height));
        int width = Math.Max(1, (int)Math.Round(original.Width * scale));
        int height = Math.Max(1, (int)Math.Round(original.Height * scale));
        using var copy = new Bitmap(width, height, jpeg ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(copy))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(original, new Rectangle(0, 0, width, height));
        }

        Directory.CreateDirectory(folder);
        string file = Guid.NewGuid().ToString("N") + (jpeg ? ".jpg" : ".png");
        string destination = Path.Combine(folder, file);
        string temporary = destination + ".tmp";
        try
        {
            if (jpeg)
            {
                var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
                using var quality = new EncoderParameters(1);
                quality.Param[0] = new EncoderParameter(Encoder.Quality, 87L);
                copy.Save(temporary, encoder, quality);
            }
            else copy.Save(temporary, ImageFormat.Png);
            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return file;
    }

    public Image? Load(string? file)
    {
        if (file is null || file.Length != 36 || !Guid.TryParseExact(file[..32], "N", out _)
            || file[32..] is not (".jpg" or ".png")) return null;
        try
        {
            using var input = File.OpenRead(Path.Combine(folder, file));
            using var source = Image.FromStream(input);
            if (Math.Max(source.Width, source.Height) > LongestEdge) return null;
            return new Bitmap(source);
        }
        catch (Exception error) when (IsImageError(error)) { return null; }
    }

    public static bool IsImageError(Exception error) => error is IOException or UnauthorizedAccessException
        or InvalidDataException or ArgumentException or ExternalException or NotSupportedException or OutOfMemoryException;

    private static void ApplyOrientation(Image image)
    {
        const int orientationTag = 0x0112;
        if (!image.PropertyIdList.Contains(orientationTag)) return;
        byte[]? value = image.GetPropertyItem(orientationTag)?.Value;
        if (value is not { Length: >= 2 }) return;
        RotateFlipType transform = BitConverter.ToUInt16(value, 0) switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone
        };
        if (transform != RotateFlipType.RotateNoneFlipNone) image.RotateFlip(transform);
    }
}
