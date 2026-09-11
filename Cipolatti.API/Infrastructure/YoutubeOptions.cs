namespace Cipolatti.API.Infrastructure;
public sealed class YoutubeOptions
{
    public string ClientId{get;set;}=string.Empty;
    public string ClientSecret{get;set;}=string.Empty;
    public string RedirectUri{get;set;}=string.Empty;
    public string FrontendReturnUrl{get;set;}="/admin/configuracoes/youtube";
}
