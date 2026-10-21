using Microsoft.AspNetCore.Http;

namespace Cipolatti.API.Models;

public sealed class FinalizarFotosRomaneioRequest
{
    public List<IFormFile> Fotos { get; set; } = [];
    public string? Observacao { get; set; }
}

public sealed record RomaneioFotoArquivoDto(string Name, long Size);

public sealed record FinalizarFotosRomaneioResponse(
    bool Success,
    int Romaneio,
    string Folder,
    IReadOnlyCollection<RomaneioFotoArquivoDto> Files,
    bool EmailSent,
    DateTimeOffset CompletedAt);

public sealed record CriarDispositivoRomaneioRequest(string Nome);

public sealed record CriarDispositivoRomaneioResponse(
    long DeviceId,
    string DeviceName,
    string ActivationCode,
    DateTimeOffset ExpiresAt);

public sealed record AtivarDispositivoRomaneioRequest(string ActivationCode);

public sealed record AtivarDispositivoRomaneioResponse(
    string DeviceToken,
    long DeviceId,
    string DeviceName);

