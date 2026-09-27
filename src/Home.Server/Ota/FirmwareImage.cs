using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Home.Server.Ota;

/// <summary>
/// Parses an ESP-IDF application image: image header, esp_app_desc_t (offset 32) and the Home
/// descriptor home_image_desc_t that the firmware places right after it (offset 288, section .rodata_custom_desc).
/// </summary>
public sealed record FirmwareImage(
    string Version, string Project, string IdfVersion, string BuildDate,
    int Model, int HwRevMask, int ProtoMajor, int ProtoMinor, string Sha256, long Size)
{
    public const uint AppDescMagic = 0xABCD5432;
    public const uint HomeDescMagic = 0x454D4F48; // "HOME" little-endian
    public const int AppDescOffset = 32;
    public const int HomeDescOffset = AppDescOffset + 256;

    public static FirmwareImage Parse(byte[] image)
    {
        if (image.Length < HomeDescOffset + 32) throw new InvalidDataException("file is too small to be an ESP32 image");
        if (image[0] != 0xE9) throw new InvalidDataException("not an ESP32 application image (magic 0xE9 missing)");
        var app = image.AsSpan(AppDescOffset);
        if (BinaryPrimitives.ReadUInt32LittleEndian(app) != AppDescMagic)
            throw new InvalidDataException("esp_app_desc_t not found — is this an application image (not bootloader/merged)?");
        var version = CStr(app.Slice(16, 32));
        var project = CStr(app.Slice(48, 32));
        var time = CStr(app.Slice(80, 16));
        var date = CStr(app.Slice(96, 16));
        var idf = CStr(app.Slice(112, 32));

        var home = image.AsSpan(HomeDescOffset);
        if (BinaryPrimitives.ReadUInt32LittleEndian(home) != HomeDescMagic)
            throw new InvalidDataException("Home descriptor not found — the image was not built with home_core");
        int model = BinaryPrimitives.ReadUInt16LittleEndian(home[4..]);
        int hwMask = BinaryPrimitives.ReadUInt16LittleEndian(home[6..]);
        int major = home[8], minor = home[9];

        var sha = Convert.ToHexStringLower(SHA256.HashData(image));
        return new FirmwareImage(version, project, idf, $"{date} {time}", model, hwMask, major, minor, sha, image.Length);
    }

    private static string CStr(ReadOnlySpan<byte> s)
    {
        var n = s.IndexOf((byte)0);
        return Encoding.UTF8.GetString(n < 0 ? s : s[..n]);
    }

    /// <summary>Builds a minimal fake image (for tests and the simulator).</summary>
    public static byte[] BuildFake(int model, string version, int size = 64 * 1024, string project = "fake")
    {
        var img = new byte[Math.Max(size, HomeDescOffset + 64)];
        new Random(version.GetHashCode()).NextBytes(img);
        img[0] = 0xE9;
        var app = img.AsSpan(AppDescOffset, 256);
        app.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(app, AppDescMagic);
        Encoding.UTF8.GetBytes(version).CopyTo(app[16..]);
        Encoding.UTF8.GetBytes(project).CopyTo(app[48..]);
        Encoding.UTF8.GetBytes("v5.4.2").CopyTo(app[112..]);
        var home = img.AsSpan(HomeDescOffset, 32);
        home.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(home, HomeDescMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(home[4..], (ushort)model);
        BinaryPrimitives.WriteUInt16LittleEndian(home[6..], 0xFFFF);
        home[8] = 1;
        return img;
    }
}
