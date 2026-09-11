using System.Reflection;
using Cipolatti.API.Services;

namespace Cipolatti.API.Tests;

public sealed class CatalogoVideoTests
{
    [Theory]
    [InlineData("dQw4w9WgXcQ","dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ","dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=12","dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ","dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/embed/dQw4w9WgXcQ","dQw4w9WgXcQ")]
    public void ExtractYoutubeId_accepts_supported_formats(string input,string expected)
    {
        Assert.Equal(expected,Invoke(input));
    }

    [Theory]
    [InlineData("http://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=invalid")]
    [InlineData("../../video")]
    public void ExtractYoutubeId_rejects_unsafe_or_invalid_values(string input)
    {
        var error=Assert.Throws<TargetInvocationException>(()=>Invoke(input));
        Assert.IsType<ArgumentException>(error.InnerException);
    }

    private static string Invoke(string value)
    {
        var method=typeof(CatalogoVideoAdminService).GetMethod("ExtractYoutubeId",BindingFlags.Static|BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(null,[value]));
    }
}
