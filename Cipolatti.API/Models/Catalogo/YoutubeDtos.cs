namespace Cipolatti.API.Models.Catalogo;
public sealed record YoutubeStatusDto(bool Configurado,bool Conectado,string? CanalId,string? CanalNome,string? ContaEmail,DateTimeOffset? DataAutorizacao);
public sealed record YoutubeConnectDto(string AuthorizationUrl);
