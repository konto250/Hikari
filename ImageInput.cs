using System.Buffers.Binary;
using System.IO;

namespace Hikari;

internal static class ImageInput
{
    public static Stream OpenRead(string path)
    {
        var file = File.OpenRead(path);
        try
        {
            long marker = FindInvalidXmpBegin(file);
            file.Position = 0;
            if (marker < 0) return file;

            // xpacket の begin="(FF)" を begin="" と空白に置換する。
            // APP1の長さ・後続メタデータ・JPEG圧縮データの位置はすべて維持する。
            var copy = new MemoryStream();
            try
            {
                file.CopyTo(copy);
                copy.Position = marker;
                copy.Write("\" "u8);
                copy.Position = 0;
                file.Dispose();
                return copy;
            }
            catch { copy.Dispose(); throw; }
        }
        catch { file.Dispose(); throw; }
    }

    static long FindInvalidXmpBegin(FileStream file)
    {
        if (file.ReadByte() != 0xFF || file.ReadByte() != 0xD8) return -1;
        ReadOnlySpan<byte> prefix = "http://ns.adobe.com/xap/1.0/\0<?xpacket begin=\""u8;
        Span<byte> probe = stackalloc byte[prefix.Length+2];
        Span<byte> lengthBytes = stackalloc byte[2];
        while (file.ReadByte() == 0xFF)
        {
            int kind;
            do { kind = file.ReadByte(); } while (kind == 0xFF);
            if (kind < 0 || kind is 0xDA or 0xD9) break;
            if (kind == 0x01 || kind is >= 0xD0 and <= 0xD7) continue;
            if (file.ReadAtLeast(lengthBytes,2,false) < 2) break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes)-2;
            long payload = file.Position;
            if (length < 0 || payload+length > file.Length) break;
            if (kind == 0xE1 && length >= probe.Length)
            {
                file.ReadExactly(probe);
                if (probe.StartsWith(prefix) && probe[^2] == 0xFF && probe[^1] == (byte)'"')
                    return payload+prefix.Length;
            }
            file.Position = payload+length;
        }
        return -1;
    }
}
