using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hikari;

// 整数画像では黒を0、白を255とする。16ビット入力の端数や除算後の範囲外の値も保持する。
internal readonly record struct FloatPixel(float R, float G, float B, float A)
{
    public static byte Byte(float v) => (byte)Math.Clamp(MathF.Floor(v + 0.5f), 0, 255);
    public Color Display => Color.FromArgb(Byte(A), Byte(R), Byte(G), Byte(B));

    public FloatPixel Adjust(int percent)
    {
        float Change(float v) => percent >= 0 ? v + (255f-v)*(percent/100f) : v*((100+percent)/100f);
        return new(Change(R), Change(G), Change(B), A);
    }

    public FloatPixel Divide(FloatPixel divisor)
    {
        static float Channel(float v, float d) => d == 0 ? (v == 0 ? 0 : 255) : v/d*255f;
        return new(Channel(R,divisor.R), Channel(G,divisor.G), Channel(B,divisor.B), A);
    }
}

internal sealed record Frame(BitmapSource Original, FloatPixel[] Pixels, BitmapSource Bitmap)
{
    public int Width => Original.PixelWidth;
    public int Height => Original.PixelHeight;
    public FloatPixel Pixel(int x, int y) => Pixels[y*Width+x];

    internal static bool IsFloat(PixelFormat f) => f == PixelFormats.Gray32Float || f == PixelFormats.Rgb128Float
        || f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float || f.BitsPerPixel == 96;
    internal static bool IsWide(PixelFormat f) => f == PixelFormats.Gray16 || f == PixelFormats.Rgb48
        || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64 || f == PixelFormats.Bgr101010 || IsFloat(f);

    public static Frame Load(string path)
    {
        using var stream = ImageInput.OpenRead(path);
        var options = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
        var decoder = BitmapDecoder.Create(stream, options, BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];
        source.Freeze();
        var f = source.Format;
        var pixels = new FloatPixel[checked(source.PixelWidth*source.PixelHeight)];
        if (IsFloat(f))
        {
            bool gray = f == PixelFormats.Gray32Float;
            var image = gray ? source : Convert(source,PixelFormats.Rgba128Float);
            int channels = gray ? 1 : 4;
            var data = new float[checked(pixels.Length*channels)];
            image.CopyPixels(data, checked(image.PixelWidth*channels*4), 0);
            for (int i=0; i<pixels.Length; i++)
            {
                int j = i*channels;
                pixels[i] = gray ? new(data[j]*255,data[j]*255,data[j]*255,255)
                    : new(data[j]*255,data[j+1]*255,data[j+2]*255,data[j+3]*255);
            }
        }
        else if (IsWide(f))
        {
            bool gray = f == PixelFormats.Gray16;
            bool rgb = f == PixelFormats.Rgb48;
            var image = gray || rgb ? source : Convert(source,PixelFormats.Rgba64);
            int channels = gray ? 1 : rgb ? 3 : 4;
            var data = new ushort[checked(pixels.Length*channels)];
            image.CopyPixels(data, checked(image.PixelWidth*channels*2), 0);
            for (int i=0; i<pixels.Length; i++)
            {
                int j = i*channels;
                float r = data[j]/257f;
                pixels[i] = gray ? new(r,r,r,255)
                    : new(r,data[j+1]/257f,data[j+2]/257f,rgb ? 255 : data[j+3]/257f);
            }
        }
        else
        {
            var image = Convert(source,PixelFormats.Bgra32);
            var data = new byte[checked(pixels.Length*4)];
            image.CopyPixels(data,checked(image.PixelWidth*4),0);
            for (int i=0; i<pixels.Length; i++)
            {
                int j = i*4;
                pixels[i] = new(data[j+2],data[j+1],data[j],data[j+3]);
            }
        }
        var preview = ToBgra32(pixels,source.PixelWidth,source.PixelHeight);
        return new(source,pixels,preview);
    }

    internal static BitmapSource Convert(BitmapSource image, PixelFormat format)
    {
        if (image.Format == format) return image;
        var converted = new FormatConvertedBitmap(image,format,null,0);
        converted.Freeze();
        return converted;
    }

    internal static BitmapSource ToBgra32(FloatPixel[] pixels, int width, int height, double dpiX=96, double dpiY=96)
    {
        var bytes = new byte[checked(pixels.Length*4)];
        for (int i=0; i<pixels.Length; i++)
        {
            var c = pixels[i];
            int j = i*4;
            bytes[j] = FloatPixel.Byte(c.B);
            bytes[j+1] = FloatPixel.Byte(c.G);
            bytes[j+2] = FloatPixel.Byte(c.R);
            bytes[j+3] = FloatPixel.Byte(c.A);
        }
        var image = BitmapSource.Create(width,height,dpiX,dpiY,PixelFormats.Bgra32,null,bytes,checked(width*4));
        image.Freeze();
        return image;
    }

    public Division Divide(FloatPixel divisor)
    {
        var values = new FloatPixel[Pixels.Length];
        for (int i=0; i<values.Length; i++) values[i] = Pixels[i].Divide(divisor);
        return new(this,divisor,values,ToBgra32(values,Width,Height));
    }
}

internal sealed record Division(Frame Source, FloatPixel Divisor, FloatPixel[] Pixels, BitmapSource Preview)
{
    public BitmapSource ForSave(bool overwrite)
    {
        var original = Source.Original;
        var format = original.Format;
        int w = Source.Width, h = Source.Height;
        BitmapSource Create(PixelFormat f, Array data, int stride, BitmapPalette? palette = null)
        {
            var bitmap = BitmapSource.Create(w,h,original.DpiX,original.DpiY,f,palette,data,stride);
            bitmap.Freeze();
            return bitmap;
        }

        // パレットを同じ並びで除算すれば、元の索引と透明度をそのまま使える。
        if (original.Palette is {} palette && (format == PixelFormats.Indexed1 || format == PixelFormats.Indexed2
            || format == PixelFormats.Indexed4 || format == PixelFormats.Indexed8))
        {
            var colors = palette.Colors.Select(c => new FloatPixel(c.R,c.G,c.B,c.A).Divide(Divisor).Display).ToList();
            int stride = checked((w*format.BitsPerPixel+7)/8);
            var indices = new byte[checked(stride*h)];
            original.CopyPixels(indices,stride,0);
            return Create(format,indices,stride,new BitmapPalette(colors));
        }

        if (Frame.IsFloat(format) && overwrite)
            throw new NotSupportedException("浮動小数点TIFFの上書きには対応していません。「PNG に名前を付けて保存」で16ビットに書き出せます。");

        if (Frame.IsWide(format))
        {
            bool gray = format == PixelFormats.Gray16 || format == PixelFormats.Gray32Float;
            bool rgb = format == PixelFormats.Rgb48 || format == PixelFormats.Rgb128Float || format == PixelFormats.Bgr101010;
            int channels = gray ? 1 : rgb ? 3 : 4;
            var values = new ushort[checked(Pixels.Length*channels)];
            static ushort Word(float v) => (ushort)Math.Clamp(MathF.Floor(v*257f+0.5f),0,65535);
            for (int i=0; i<Pixels.Length; i++)
            {
                var c = Pixels[i];
                int j = i*channels;
                values[j] = Word(c.R);
                if (gray) continue;
                values[j+1] = Word(c.G);
                values[j+2] = Word(c.B);
                if (!rgb) values[j+3] = Word(c.A);
            }
            var bitmap = Create(gray ? PixelFormats.Gray16 : rgb ? PixelFormats.Rgb48 : PixelFormats.Rgba64,
                values,checked(w*channels*2));
            return overwrite ? Frame.Convert(bitmap,format) : bitmap;
        }

        if (format == PixelFormats.Gray8)
        {
            var gray = new byte[Pixels.Length];
            for (int i=0; i<gray.Length; i++) gray[i] = FloatPixel.Byte(Pixels[i].R);
            return Create(format,gray,w);
        }

        var bgra = Frame.ToBgra32(Pixels,w,h,original.DpiX,original.DpiY);
        // PNGにない形式（CMYK・RGB565等）は別名保存のときだけRGBへ変換する。
        if (!overwrite && (format == PixelFormats.Cmyk32 || format == PixelFormats.Bgr555 ||
            format == PixelFormats.Bgr565 || format == PixelFormats.Bgr32)) return Frame.Convert(bgra,PixelFormats.Bgr24);
        return Frame.Convert(bgra,format);
    }
}
