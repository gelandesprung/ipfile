using QRCoder;

namespace FeigeP2P;

/// <summary>把配对载荷渲染成 PNG 字节（纯托管 PngByteQRCode，不依赖 System.Drawing）。</summary>
internal static class QrCode
{
    /// <summary>返回 PNG 字节；内容非法或生成失败时返回 null。</summary>
    public static byte[]? RenderPng(string text, int pixelsPerModule = 8)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data);
            return png.GetGraphic(pixelsPerModule);
        }
        catch
        {
            return null;
        }
    }
}
