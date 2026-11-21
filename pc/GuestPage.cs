using System.Reflection;

namespace FeigeP2P;

/// <summary>
/// 浏览器访客端单页（零安装）。由 WebHostService 直接吐出，不依赖任何外部资源。
///
/// 页面本体是仓库里的 <c>shared/web/guest.html</c>，以嵌入资源方式编进 exe——
/// 安卓端通过 assets 目录读同一个文件，两端只有一份副本，改版式不会各改各的。
/// </summary>
internal static class GuestPage
{
    private const string ResourceName = "guest.html";

    private static readonly Lazy<string> _html = new(() =>
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"缺少嵌入资源 {ResourceName}（见 pc/FeigeP2P.csproj 的 EmbeddedResource）");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>访客页 HTML。首次访问时读入并缓存。</summary>
    public static string Html => _html.Value;
}
