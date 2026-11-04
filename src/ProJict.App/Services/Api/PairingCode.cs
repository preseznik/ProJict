using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace ProJict.App.Services.Api;

/// <summary>The QR code ProJict on your phone scans to pair (#1bbc8a8d): what <see cref="Core.Api.ApiPairing"/> puts in it, drawn.</summary>
public static class PairingCode
{
    public static BitmapSource Draw(string payload, int pixelsPerModule = 6)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
