using Cipolatti.API.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Cipolatti.API.Services;
using System.Text;

namespace Cipolatti.API.Tests;

public sealed class LocalFileStorageTests
{
    [Fact]
    public async Task Cover_is_decoded_and_saved_as_webp_in_server_defined_path()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalogo-storage-{Guid.NewGuid():N}");
        try
        {
            var storage = CreateStorage(root);
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            await using var input = new MemoryStream(png);

            var relativePath = await storage.SaveCoverAsWebpAsync(42, input, TestContext.Current.CancellationToken);
            await using var saved = await storage.OpenReadAsync(relativePath, TestContext.Current.CancellationToken);

            Assert.Equal("catalogo/temas/42/capa/capa.webp", relativePath);
            Assert.NotNull(saved);
            var header = new byte[12];
            Assert.Equal(12, await saved.ReadAsync(header, TestContext.Current.CancellationToken));
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
            Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(header, 8, 4));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Storage_rejects_path_traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalogo-storage-{Guid.NewGuid():N}");
        var storage = CreateStorage(root);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.OpenReadAsync("../../segredo", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pdf_pipeline_validates_signature_renders_and_stores_webp_page()
    {
        var root=Path.Combine(Path.GetTempPath(),$"catalogo-storage-{Guid.NewGuid():N}");
        try
        {
            var options=Options.Create(new CatalogoStorageOptions{RootPath=root,PdfRenderWidth=640,PdfRenderHeight=640});
            var storage=new LocalFileStorage(options,new FakeEnvironment{ContentRootPath=root});
            await using var input=new MemoryStream(CreateOnePagePdf());
            var original=await storage.SavePdfOriginalAsync(10,20,input,TestContext.Current.CancellationToken);
            var renderer=new PdfiumRenderer(storage,options);

            var pages=await renderer.RenderAsync(10,20,original.RelativePath,original.Version,TestContext.Current.CancellationToken);

            var page=Assert.Single(pages);
            Assert.Equal(1,page.PageNumber);
            Assert.EndsWith("/paginas/001.webp",page.RelativePath);
            Assert.True(page.Width>0);
            Assert.True(page.Height>0);
            Assert.True(page.SizeBytes>0);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    private static LocalFileStorage CreateStorage(string root) => new(
        Options.Create(new CatalogoStorageOptions { RootPath = root }),
        new FakeEnvironment { ContentRootPath = root });

    private static byte[] CreateOnePagePdf()
    {
        var objects=new[]{
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"};
        var builder=new StringBuilder("%PDF-1.4\n");var offsets=new List<int>();
        for(var index=0;index<objects.Length;index++){offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));builder.Append($"{index+1} 0 obj\n{objects[index]}\nendobj\n");}
        var xref=Encoding.ASCII.GetByteCount(builder.ToString());builder.Append($"xref\n0 {objects.Length+1}\n0000000000 65535 f \n");
        foreach(var offset in offsets)builder.Append($"{offset:0000000000} 00000 n \n");
        builder.Append($"trailer\n<< /Size {objects.Length+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private sealed class FakeEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
