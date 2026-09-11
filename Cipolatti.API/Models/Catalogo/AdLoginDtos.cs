namespace Cipolatti.API.Models.Catalogo;

public sealed record AdLoginRequest(string Usuario, string Senha);

public sealed record AdLoginResponse(string AccessToken, DateTimeOffset ExpiresAt, string Usuario, string Nome);
