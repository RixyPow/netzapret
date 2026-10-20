using System.IO.Compression;
using System.Text;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Маленькая страница докачивает свой скрипт или стиль, чтобы заморозка
/// на 12–20 КБ стала видна.
/// </summary>
/// <remarks>
/// 7tv.io: главная 148 Б проходит, ответ API на 112 КБ замерзает на 0–3,6 КБ
/// (замер 19.10) — проверка звала сайт доступным. Браузер за такой
/// страницей тянет её сборку скриптов, и обрыв настигает его там.
/// </remarks>
public sealed class PageAssetsTests
{
    /// <summary>
    /// Сборки раньше прочих скриптов, скрипты раньше стилей.
    /// </summary>
    /// <remarks>
    /// www.hetzner.com (20.10): в разметке первыми стоят matomo.js на 372 Б
    /// и его менеджер меток, а vendors.js на 154 КБ — восьмым. Проб две,
    /// и в порядке разметки заморозку было бы не поймать.
    /// </remarks>
    [Fact]
    public void Bundles_come_first_then_other_scripts_then_stylesheets()
    {
        const string html = """
            <!doctype html><html><head>
            <link rel="stylesheet" href="/assets/index-abc.css">
            <link href="https://7tv.io/assets/theme.css" rel="preload stylesheet">
            <script src="/dist/matomo.js?m=1"></script>
            <script type="module" crossorigin src="/assets/index-def.js"></script>
            <script src='https://7tv.io/dist/vendors.js'></script>
            </head><body></body></html>
            """;

        var assets = PageAssets.SameSite("7tv.io", html);

        Assert.Equal(
            ["/assets/index-def.js", "/dist/vendors.js", "/dist/matomo.js?m=1", "/assets/index-abc.css", "/assets/theme.css"],
            assets);
    }

    [Theory]
    [InlineData("/dist/vendors.js?m=1", true)]
    [InlineData("/_app/immutable/chunks/B3kq.js", true)]
    [InlineData("/static/main.0a1b.js", true)]
    [InlineData("/dist/matomo.js", false)]
    [InlineData("/app/ui/tracker.js", false)]
    [InlineData("/_resources/vendor/silverstripe/admin/client/dist/js/i18n.js?m=1", false)]
    public void A_bundle_is_told_by_its_file_name(string path, bool expected)
    {
        Assert.Equal(expected, PageAssets.LooksLikeBundle(path));
    }

    /// <summary>
    /// Чужой хост — другое имя со своим вердиктом, а не подресурс этого.
    /// </summary>
    [Theory]
    [InlineData("https://cdn.example.net/app.js")]
    [InlineData("//static.example.net/app.js")]
    [InlineData("http://example.com/app.js")]
    [InlineData("https://example.com:8443/app.js")]
    [InlineData("data:text/javascript;base64,AAAA")]
    [InlineData("javascript:void(0)")]
    [InlineData("")]
    public void Foreign_and_empty_links_are_left_out(string src)
    {
        var html = $"<script src=\"{src}\"></script>";

        Assert.Empty(PageAssets.SameSite("example.com", html));
    }

    [Theory]
    [InlineData("example.com", "/a/b.js?v=3", "/a/b.js?v=3")]
    [InlineData("example.com", "assets/app.js", "/assets/app.js")]
    [InlineData("example.com", "https://EXAMPLE.com/x.js", "/x.js")]
    [InlineData("example.com", "//example.com/x.js", "/x.js")]
    [InlineData("example.com", "/x.js&amp;y=1", "/x.js&y=1")]
    public void A_same_site_link_becomes_a_path(string host, string raw, string expected)
    {
        Assert.Equal(expected, PageAssets.SameSitePath(host, raw));
    }

    /// <summary>
    /// Предзагрузка модуля — скрипт: у 7tv.app своих <c>script src</c> нет
    /// вовсе, сборка объявлена через <c>modulepreload</c> (20.10).
    /// </summary>
    [Fact]
    public void A_module_preload_counts_as_a_script()
    {
        const string html = """
            <link href="./_app/immutable/assets/button.css" rel="stylesheet">
            <link href="./_app/immutable/entry/start.Cxj3GtQs.js" rel="modulepreload">
            <link href="./_app/immutable/chunks/DSGUz7_V.js" rel="modulepreload">
            """;

        Assert.Equal(
            ["/_app/immutable/entry/start.Cxj3GtQs.js", "/_app/immutable/chunks/DSGUz7_V.js", "/_app/immutable/assets/button.css"],
            PageAssets.SameSite("7tv.app", html));
    }

    [Fact]
    public void The_same_link_twice_is_one_asset()
    {
        const string html = "<script src=\"/a.js\"></script><script src=\"/a.js\"></script>";

        Assert.Equal(["/a.js"], PageAssets.SameSite("example.com", html));
    }

    /// <summary>
    /// Ссылка на стиль без rel — это не стиль: иконки и предзагрузки
    /// в подресурсы не идут.
    /// </summary>
    [Fact]
    public void A_link_without_stylesheet_rel_is_not_an_asset()
    {
        const string html = "<link rel=\"icon\" href=\"/favicon.ico\"><link rel=\"preload\" href=\"/font.woff2\" as=\"font\">";

        Assert.Empty(PageAssets.SameSite("example.com", html));
    }

    [Fact]
    public void A_chunked_body_is_glued_back()
    {
        var chunked = Encoding.ASCII.GetBytes("5\r\nhello\r\n6; ext=1\r\n world\r\n0\r\n\r\n");

        Assert.Equal("hello world", Encoding.ASCII.GetString(PageAssets.Unchunk(chunked)!));
    }

    /// <summary>
    /// Оборванное на полуслове тело всё равно читается: ссылки стоят
    /// в начале страницы, а дальше читать некому.
    /// </summary>
    [Fact]
    public void A_truncated_chunk_keeps_what_came()
    {
        var chunked = Encoding.ASCII.GetBytes("ff\r\nhead");

        Assert.Equal("head", Encoding.ASCII.GetString(PageAssets.Unchunk(chunked)!));
    }

    [Fact]
    public void Broken_chunk_markup_gives_nothing()
    {
        Assert.Null(PageAssets.Unchunk(Encoding.ASCII.GetBytes("zz\r\nabc")));
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("deflate")]
    [InlineData(null)]
    public void A_compressed_body_is_decoded_by_its_encoding(string? encoding)
    {
        const string html = "<script src=\"/bundle.js\"></script>";
        var plain = Encoding.UTF8.GetBytes(html);

        var body = encoding switch
        {
            "gzip" => Squeeze(plain, s => new GZipStream(s, CompressionLevel.Fastest, leaveOpen: true)),
            "br" => Squeeze(plain, s => new BrotliStream(s, CompressionLevel.Fastest, leaveOpen: true)),
            "deflate" => Squeeze(plain, s => new ZLibStream(s, CompressionLevel.Fastest, leaveOpen: true)),
            _ => plain,
        };

        Assert.Equal(html, PageAssets.Decode(body, chunked: false, encoding));
    }

    /// <summary>
    /// Часть серверов шлёт под именем deflate голый поток без заголовка zlib.
    /// </summary>
    [Fact]
    public void Bare_deflate_is_decoded_too()
    {
        var plain = Encoding.UTF8.GetBytes("<script src=\"/b.js\"></script>");
        var bare = Squeeze(plain, s => new DeflateStream(s, CompressionLevel.Fastest, leaveOpen: true));

        Assert.Equal(Encoding.UTF8.GetString(plain), PageAssets.Decode(bare, chunked: false, "deflate"));
    }

    [Fact]
    public void Garbage_under_a_known_encoding_gives_nothing()
    {
        Assert.Null(PageAssets.Decode([1, 2, 3, 4, 5], chunked: false, "gzip"));
        Assert.Null(PageAssets.Decode([1, 2, 3], chunked: false, "zstd"));
    }

    [Fact]
    public void Chunked_and_compressed_together_are_decoded_in_order()
    {
        var plain = Encoding.UTF8.GetBytes("<link rel=stylesheet href=/s.css>");
        var zipped = Squeeze(plain, s => new GZipStream(s, CompressionLevel.Fastest, leaveOpen: true));

        var chunked = new MemoryStream();
        chunked.Write(Encoding.ASCII.GetBytes($"{zipped.Length:x}\r\n"));
        chunked.Write(zipped);
        chunked.Write(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n"));

        var html = PageAssets.Decode(chunked.ToArray(), chunked: true, "gzip");

        Assert.Equal(["/s.css"], PageAssets.SameSite("example.com", html!));
    }

    /// <summary>
    /// Подресурс нужен только обычной маленькой странице. Отказ, проверка
    /// на робота и переадресация уже сказали своё, а большая страница
    /// показала бы заморозку сама.
    /// </summary>
    [Theory]
    [InlineData(200, false, false, null, 148, true)]
    [InlineData(200, false, false, null, 19_999, true)]
    [InlineData(200, false, false, null, 20_000, false)]
    [InlineData(200, true, false, null, 148, false)]
    [InlineData(200, false, true, null, 148, false)]
    [InlineData(200, false, false, "www.example.com", 148, false)]
    [InlineData(404, false, false, null, 148, false)]
    [InlineData(null, false, false, null, 148, false)]
    public void Only_a_plain_small_page_asks_for_an_asset(
        int? status, bool refused, bool challenged, string? moved, int total, bool expected)
    {
        Assert.Equal(expected, BlockCheck.NeedsAsset(status, refused, challenged, moved, total));
    }

    private static byte[] Squeeze(byte[] plain, Func<Stream, Stream> wrap)
    {
        var output = new MemoryStream();

        using (var packer = wrap(output))
        {
            packer.Write(plain);
        }

        return output.ToArray();
    }
}
