using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using Cipolatti.API.ExPonto;
using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Dapper;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Cipolatti.API.Services;

public sealed class RomaneioFotosOptions
{
    public string RootPath { get; set; } = "";
    public string DisplayRootPath { get; set; } = "";
    public string Recipient { get; set; } = "";
    public int MaxFiles { get; set; } = 20;
    public long MaxFileBytes { get; set; } = 15 * 1024 * 1024;
}

public interface IRomaneioFotosService
{
    Task<CriarDispositivoRomaneioResponse> CreateDeviceAsync(string name, CancellationToken ct);
    Task<AtivarDispositivoRomaneioResponse?> ActivateDeviceAsync(string activationCode, CancellationToken ct);
    Task<bool> RevokeDeviceAsync(long deviceId, CancellationToken ct);
    Task<FinalizarFotosRomaneioResponse> FinalizeAsync(
        int romaneio, long deviceId, string idempotencyKey, FinalizarFotosRomaneioRequest request, CancellationToken ct);
}

public sealed class RomaneioFotosService(
    IDbConnectionFactory db,
    IOptions<RomaneioFotosOptions> photoOptions,
    IOptions<EmailOptions> emailOptions,
    ILogger<RomaneioFotosService> logger) : IRomaneioFotosService
{
    private readonly RomaneioFotosOptions photos = photoOptions.Value;
    private readonly EmailOptions email = emailOptions.Value;

    public async Task<CriarDispositivoRomaneioResponse> CreateDeviceAsync(string name, CancellationToken ct)
    {
        var normalizedName = name.Trim();
        if (normalizedName.Length is < 3 or > 100) throw new InvalidDataException("Informe um nome de dispositivo entre 3 e 100 caracteres.");
        var activationCode = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        var activationHash = Hash(activationCode);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await using var connection = await db.OpenConnectionAsync(ct);
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO expedicao.app_dispositivos_fotos
                (nome, codigo_ativacao_hash, codigo_ativacao_expira_em)
            VALUES (@normalizedName, @activationHash, @expiresAt)
            RETURNING id
            """, new { normalizedName, activationHash, expiresAt }, cancellationToken: ct));
        return new(id, normalizedName, activationCode, expiresAt);
    }

    public async Task<AtivarDispositivoRomaneioResponse?> ActivateDeviceAsync(string activationCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(activationCode) || activationCode.Length > 200) return null;
        var activationHash = Hash(activationCode.Trim());
        var deviceToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Hash(deviceToken);
        await using var connection = await db.OpenConnectionAsync(ct);
        var device = await connection.QuerySingleOrDefaultAsync<DeviceRow>(new CommandDefinition("""
            UPDATE expedicao.app_dispositivos_fotos
               SET token_hash = @tokenHash,
                   codigo_ativacao_hash = NULL,
                   codigo_ativacao_expira_em = NULL,
                   ativado_em = now(),
                   ativo = true
             WHERE codigo_ativacao_hash = @activationHash
               AND codigo_ativacao_expira_em > now()
               AND ativado_em IS NULL
               AND revogado_em IS NULL
            RETURNING id, nome
            """, new { activationHash, tokenHash }, cancellationToken: ct));
        return device is null ? null : new(deviceToken, device.Id, device.Nome);
    }

    public async Task<bool> RevokeDeviceAsync(long deviceId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE expedicao.app_dispositivos_fotos
               SET ativo = false, revogado_em = now(), token_hash = NULL
             WHERE id = @deviceId AND revogado_em IS NULL
            """, new { deviceId }, cancellationToken: ct)) > 0;
    }

    public async Task<FinalizarFotosRomaneioResponse> FinalizeAsync(
        int romaneio, long deviceId, string idempotencyKey, FinalizarFotosRomaneioRequest request, CancellationToken ct)
    {
        ValidateConfiguration();
        if (!Guid.TryParse(idempotencyKey, out var operationId))
            throw new InvalidDataException("O cabeçalho Idempotency-Key deve conter um UUID válido.");
        if (request.Fotos.Count is 0 || request.Fotos.Count > 20 || request.Fotos.Count > photos.MaxFiles)
            throw new InvalidDataException($"Informe entre 1 e {Math.Min(20, photos.MaxFiles)} fotos.");
        if (request.Observacao?.Length > 1000) throw new InvalidDataException("A observação deve possuir no máximo 1000 caracteres.");

        await using var connection = await db.OpenConnectionAsync(ct);
        var existing = await connection.QuerySingleOrDefaultAsync<FinalizationRow>(new CommandDefinition("""
            SELECT id, codigo_romaneio AS Romaneio, pasta AS Folder, arquivos_json AS FilesJson,
                   observacao AS Observation, email_enviado AS EmailSent, concluido_em AS CompletedAt
              FROM expedicao.romaneio_foto_finalizacoes
             WHERE dispositivo_id = @deviceId AND chave_idempotencia = @operationId
            """, new { deviceId, operationId }, cancellationToken: ct));
        if (existing is not null)
        {
            var prior = ToResponse(existing);
            if (!prior.EmailSent)
            {
                var priorData = await QueryRomaneioAsync(connection, prior.Romaneio, ct)
                    ?? throw new KeyNotFoundException("Romaneio não encontrado.");
                await SendEmailAsync(prior, existing.Observation, ct, priorData);
                await MarkEmailSentAsync(connection, existing.Id, ct);
                prior = prior with { EmailSent = true };
            }
            return prior;
        }

        var romaneioData = await QueryRomaneioAsync(connection, romaneio, ct)
            ?? throw new KeyNotFoundException("Romaneio não encontrado.");

        var completedAt = DateTimeOffset.Now;
        var relativeFolder = Path.Combine($"romaneio-{romaneio}", $"{completedAt:yyyyMMdd-HHmmss}-{operationId:N}"[..30]);
        var storedFiles = await SaveFilesAsync(relativeFolder, request.Fotos, ct);
        var response = new FinalizarFotosRomaneioResponse(true, romaneio, relativeFolder.Replace(Path.DirectorySeparatorChar, '/'), storedFiles, false, completedAt);
        var filesJson = JsonSerializer.Serialize(storedFiles);
        long finalizationId;
        try
        {
            finalizationId = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO expedicao.romaneio_foto_finalizacoes
                    (codigo_romaneio, dispositivo_id, chave_idempotencia, pasta, arquivos_json, observacao, concluido_em)
                VALUES (@romaneio, @deviceId, @operationId, @folder, CAST(@filesJson AS jsonb), @observacao, @completedAt)
                RETURNING id
                """, new { romaneio, deviceId, operationId, folder = response.Folder, filesJson, observacao = request.Observacao?.Trim(), completedAt }, cancellationToken: ct));
        }
        catch
        {
            TryDeleteDirectory(Path.Combine(FullRootPath(), relativeFolder));
            throw;
        }

        try
        {
            await SendEmailAsync(response, request.Observacao, ct, romaneioData);
            await MarkEmailSentAsync(connection, finalizationId, ct);
            return response with { EmailSent = true };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fotos do romaneio {Romaneio} foram gravadas, mas o e-mail não foi enviado.", romaneio);
            throw new RomaneioEmailException("As fotos foram gravadas, mas o e-mail não foi enviado. Repita a operação com a mesma Idempotency-Key.", ex);
        }
    }

    private async Task<IReadOnlyCollection<RomaneioFotoArquivoDto>> SaveFilesAsync(string relativeFolder, IReadOnlyList<IFormFile> files, CancellationToken ct)
    {
        var root = FullRootPath();
        Directory.CreateDirectory(root);
        var finalDirectory = SafeCombine(root, relativeFolder);
        var temporaryDirectory = finalDirectory + ".upload-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporaryDirectory);
        var result = new List<RomaneioFotoArquivoDto>(files.Count);
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                if (file.Length <= 0 || file.Length > photos.MaxFileBytes)
                    throw new InvalidDataException($"A foto {index + 1} está vazia ou excede o limite de {photos.MaxFileBytes / 1024 / 1024} MB.");
                var temporaryPath = Path.Combine(temporaryDirectory, $"foto-{index + 1:000}.tmp");
                await using (var source = file.OpenReadStream())
                await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await source.CopyToAsync(target, ct);
                }
                var extension = ValidateImage(temporaryPath);
                var name = $"foto-{index + 1:000}{extension}";
                File.Move(temporaryPath, Path.Combine(temporaryDirectory, name));
                result.Add(new(name, file.Length));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
            Directory.Move(temporaryDirectory, finalDirectory);
            return result;
        }
        catch
        {
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
    }

    private async Task SendEmailAsync(FinalizarFotosRomaneioResponse result, string? observation, CancellationToken ct, RomaneioRow? data = null)
    {
        if (!email.Enabled || string.IsNullOrWhiteSpace(email.FromAddress) || string.IsNullOrWhiteSpace(email.Smtp.Host))
            throw new InvalidOperationException("O envio SMTP não está configurado.");
        var fullFolder = string.IsNullOrWhiteSpace(photos.DisplayRootPath)
            ? SafeCombine(FullRootPath(), result.Folder.Replace('/', Path.DirectorySeparatorChar))
            : photos.DisplayRootPath.TrimEnd('\\') + "\\" + result.Folder.Replace('/', '\\');
        var body = $"""
            <h2>Fotos do carregamento</h2>
            <p><strong>Romaneio:</strong> {result.Romaneio}</p>
            <p><strong>Destino:</strong> {WebUtility.HtmlEncode(data?.Destino ?? "-")}</p>
            <p><strong>Motorista:</strong> {WebUtility.HtmlEncode(data?.Motorista ?? "-")}</p>
            <p><strong>Placa:</strong> {WebUtility.HtmlEncode(data?.Placa ?? "-")}</p>
            <p><strong>Fotos:</strong> {result.Files.Count}</p>
            <p><strong>Pasta:</strong> {WebUtility.HtmlEncode(fullFolder)}</p>
            <p><strong>Observação:</strong> {WebUtility.HtmlEncode(observation?.Trim() ?? "-")}</p>
            """;
        using var message = new MailMessage
        {
            From = new MailAddress(email.FromAddress, email.FromName),
            Subject = $"Fotos do carregamento - Romaneio {result.Romaneio}",
            Body = body,
            IsBodyHtml = true
        };
        message.To.Add(photos.Recipient);
        using var client = new SmtpClient(email.Smtp.Host, email.Smtp.Port) { EnableSsl = email.Smtp.EnableSsl };
        if (!string.IsNullOrWhiteSpace(email.Smtp.UserName))
            client.Credentials = new NetworkCredential(email.Smtp.UserName, email.Smtp.Password);
        await client.SendMailAsync(message, ct);
    }

    private static async Task MarkEmailSentAsync(System.Data.Common.DbConnection connection, long id, CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE expedicao.romaneio_foto_finalizacoes
               SET email_enviado = true, email_enviado_em = now()
             WHERE id = @id
            """, new { id }, cancellationToken: ct));

    private static Task<RomaneioRow?> QueryRomaneioAsync(System.Data.Common.DbConnection connection, int romaneio, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<RomaneioRow>(new CommandDefinition("""
            SELECT cod_romaneiro AS Codigo, shopping_destino AS Destino,
                   nome_motorista AS Motorista, placa_caminhao AS Placa
              FROM expedicao.t_romaneio
             WHERE cod_romaneiro = @romaneio
            """, new { romaneio }, cancellationToken: ct));

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(photos.RootPath)) throw new InvalidOperationException("Configure RomaneioFotos:RootPath.");
        if (string.IsNullOrWhiteSpace(photos.Recipient)) throw new InvalidOperationException("Configure RomaneioFotos:Recipient.");
    }

    private string FullRootPath() => Path.GetFullPath(photos.RootPath);

    private static string SafeCombine(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Caminho de fotos inválido.");
        return full;
    }

    private static string ValidateImage(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using (var stream = File.OpenRead(path))
            if (stream.Read(header) < 12) throw new InvalidDataException("Arquivo de imagem incompleto.");
        var extension = header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff ? ".jpg"
            : header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? ".png"
            : header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8) ? ".webp"
            : throw new InvalidDataException("Formato permitido: JPEG, PNG ou WebP.");
        using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException("O arquivo não contém uma imagem válida.");
        if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Width > 12000 || bitmap.Height > 12000)
            throw new InvalidDataException("As dimensões da imagem são inválidas.");
        return extension;
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    private static FinalizarFotosRomaneioResponse ToResponse(FinalizationRow row) =>
        new(true, row.Romaneio, row.Folder,
            JsonSerializer.Deserialize<List<RomaneioFotoArquivoDto>>(row.FilesJson) ?? [], row.EmailSent, row.CompletedAt);

    private sealed record DeviceRow(long Id, string Nome);
    private sealed record RomaneioRow(int Codigo, string? Destino, string? Motorista, string? Placa);
    private sealed record FinalizationRow(long Id, int Romaneio, string Folder, string FilesJson, string? Observation, bool EmailSent, DateTimeOffset CompletedAt);
}

public sealed class RomaneioEmailException(string message, Exception innerException) : Exception(message, innerException);

