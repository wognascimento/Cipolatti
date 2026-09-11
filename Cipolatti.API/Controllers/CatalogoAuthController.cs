using Cipolatti.API.Models.Catalogo;
using Cipolatti.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cipolatti.API.Controllers;

[ApiController, Route("api/catalogo/auth")]
public sealed class CatalogoAuthController(IAdAuthenticationService authentication) : ControllerBase
{
    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("CatalogoLogin")]
    public ActionResult<AdLoginResponse> Login(AdLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Usuario) || string.IsNullOrWhiteSpace(request.Senha))
            return Unauthorized(new ProblemDetails { Status = 401, Title = "Credenciais inválidas." });
        try { return Ok(authentication.Authenticate(request.Usuario, request.Senha)); }
        catch (UnauthorizedAccessException) { return Unauthorized(new ProblemDetails { Status = 401, Title = "Credenciais inválidas ou usuário não autorizado." }); }
    }
}
