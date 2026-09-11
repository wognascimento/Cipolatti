using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models.Catalogo;
using Dapper;

namespace Cipolatti.API.Services;

public sealed class CatalogoPdfAdminService(IDbConnectionFactory db, IFileStorage storage, IPdfJobQueue queue,
    ILogger<CatalogoPdfAdminService> logger) : ICatalogoPdfAdminService
{
    public async Task<IReadOnlyCollection<AdminTemaPdfDto>?> ListarAsync(int idTema, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM comercial.temas WHERE idtema=@idTema)", new { idTema }, cancellationToken: ct))) return null;
        return (await connection.QueryAsync<AdminTemaPdfDto>(new CommandDefinition(PdfSelect +
            " WHERE idtema=@idTema ORDER BY principal DESC,ordem,idtema_pdf", new { idTema }, cancellationToken: ct))).AsList();
    }

    public async Task<AdminTemaPdfDto?> CriarAsync(int idTema, CriarTemaPdfRequest request, Stream content,
        string contentType, string? usuario, CancellationToken ct)
    {
        ValidateUpload(request.Titulo, contentType);
        await using var connection = await db.OpenConnectionAsync(ct);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM comercial.temas WHERE idtema=@idTema)", new { idTema }, cancellationToken: ct))) return null;
        if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM comercial.tema_pdf WHERE idtema=@idTema AND ativo=true AND lower(btrim(titulo))=lower(btrim(@titulo)))",
                new { idTema, titulo=request.Titulo }, cancellationToken: ct)))
            throw new CatalogConflictException("Já existe uma apresentação PDF ativa com este título neste tema.");
        var idPdf = await connection.QuerySingleAsync<long>(new CommandDefinition("""
            INSERT INTO comercial.tema_pdf(idtema,titulo,descricao,arquivo_original,ordem,ativo,principal,permitir_download,status,inserido_por)
            VALUES(@idTema,@titulo,@descricao,'pending',
                   COALESCE((SELECT max(ordem)+1 FROM comercial.tema_pdf WHERE idtema=@idTema),0),true,false,@download,'pending',@usuario)
            RETURNING idtema_pdf
            """, new { idTema, titulo=request.Titulo.Trim(), descricao=NullIfBlank(request.Descricao), download=request.PermitirDownload, usuario }, cancellationToken:ct));
        try
        {
            var stored = await storage.SavePdfOriginalAsync(idTema, idPdf, content, ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (request.Principal)
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE comercial.tema_pdf SET principal=false,alterado_por=@usuario,data_altera=now() WHERE idtema=@idTema AND ativo=true", new { idTema, usuario }, transaction, cancellationToken:ct));
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE comercial.tema_pdf SET arquivo_original=@path,principal=@principal,alterado_por=@usuario,data_altera=now()
                WHERE idtema_pdf=@idPdf
                """, new { path=stored.RelativePath, principal=request.Principal, usuario, idPdf }, transaction, cancellationToken:ct));
            await transaction.CommitAsync(ct);
            await queue.EnqueueAsync(new(idPdf), ct);
            logger.LogInformation("PDF {IdPdf} recebido para o tema {IdTema} por {Usuario}", idPdf,idTema,usuario??"api");
            return await ObterAsync(idPdf, ct);
        }
        catch
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM comercial.tema_pdf WHERE idtema_pdf=@idPdf AND status='pending'", new { idPdf }, cancellationToken:CancellationToken.None));
            throw;
        }
    }

    public async Task<AdminTemaPdfDto?> AtualizarAsync(long idPdf, AtualizarTemaPdfRequest request, string? usuario, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Titulo)) throw new ArgumentException("Informe o titulo.");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var idTema = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT idtema FROM comercial.tema_pdf WHERE idtema_pdf=@idPdf", new { idPdf }, transaction, cancellationToken:ct));
        if (idTema is null) return null;
        if (request.Principal && request.Ativo)
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE comercial.tema_pdf SET principal=false,alterado_por=@usuario,data_altera=now() WHERE idtema=@idTema AND idtema_pdf<>@idPdf AND ativo=true",
                new { idTema, idPdf, usuario }, transaction, cancellationToken:ct));
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE comercial.tema_pdf SET titulo=@titulo,descricao=@descricao,ordem=@ordem,ativo=@ativo,
                principal=@principal,permitir_download=@download,alterado_por=@usuario,data_altera=now()
            WHERE idtema_pdf=@idPdf
            """, new { titulo=request.Titulo.Trim(),descricao=NullIfBlank(request.Descricao),request.Ordem,request.Ativo,
                principal=request.Principal&&request.Ativo,download=request.PermitirDownload,usuario,idPdf },transaction,cancellationToken:ct));
        await transaction.CommitAsync(ct);
        logger.LogInformation("Metadados do PDF {IdPdf} atualizados por {Usuario}",idPdf,usuario??"api");
        return await ObterAsync(idPdf,ct);
    }

    public async Task<bool> SubstituirAsync(long idPdf, Stream content, string contentType, string? usuario, CancellationToken ct)
    {
        ValidateUpload("PDF",contentType);
        await using var connection=await db.OpenConnectionAsync(ct);
        var idTema=await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT idtema FROM comercial.tema_pdf WHERE idtema_pdf=@idPdf",new{idPdf},cancellationToken:ct));
        if(idTema is null)return false;
        var stored=await storage.SavePdfOriginalAsync(idTema.Value,idPdf,content,ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE comercial.tema_pdf SET alterado_por=@usuario,data_altera=now() WHERE idtema_pdf=@idPdf",
            new{idPdf,usuario},cancellationToken:ct));
        var job=new PdfProcessingJob(idPdf,true,stored.RelativePath,stored.Version);
        await storage.PersistPdfJobAsync(job,ct);
        await queue.EnqueueAsync(job,ct);
        logger.LogInformation("Substituicao do PDF {IdPdf} solicitada por {Usuario}",idPdf,usuario??"api");
        return true;
    }

    public async Task<bool> ReprocessarAsync(long idPdf,string? usuario,CancellationToken ct)
    {
        await using var connection=await db.OpenConnectionAsync(ct);
        var row=await connection.QuerySingleOrDefaultAsync<PdfSource>(new CommandDefinition(
            "SELECT idtema AS IdTema,arquivo_original AS Original FROM comercial.tema_pdf WHERE idtema_pdf=@idPdf",new{idPdf},cancellationToken:ct));
        if(row is null)return false;
        var version=DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff")+"-"+Guid.NewGuid().ToString("N")[..8];
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE comercial.tema_pdf SET mensagem_erro=NULL,alterado_por=@usuario,data_altera=now() WHERE idtema_pdf=@idPdf",
            new{idPdf,usuario},cancellationToken:ct));
        var job=new PdfProcessingJob(idPdf,true,row.Original,version);
        await storage.PersistPdfJobAsync(job,ct);
        await queue.EnqueueAsync(job,ct);
        logger.LogInformation("Reprocessamento do PDF {IdPdf} solicitado por {Usuario}",idPdf,usuario??"api");
        return true;
    }

    public async Task<bool> UsarPrimeiraPaginaComoCapaAsync(long idPdf, string? usuario, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<(int IdTema, string? Pagina)>(new CommandDefinition("""
            SELECT p.idtema AS "IdTema", pg.arquivo_imagem AS "Pagina"
            FROM comercial.tema_pdf p
            JOIN comercial.tema_pdf_pagina pg ON pg.idtema_pdf=p.idtema_pdf AND pg.numero_pagina=1
            WHERE p.idtema_pdf=@idPdf AND p.status='ready' AND p.ativo=true
            """, new { idPdf }, cancellationToken: ct));
        if (row.IdTema <= 0 || string.IsNullOrWhiteSpace(row.Pagina)) return false;
        var path = await storage.SaveCoverFromPdfPageAsync(row.IdTema, row.Pagina, ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO comercial.tema_catalogo
                (idtema,slug,imagem_capa,publicado,inserido_por,data_cadastro,alterado_por,data_altera)
            VALUES (@idTema,'tema-' || @idTema::text,@path,false,@usuario,now(),@usuario,now())
            ON CONFLICT (idtema) DO UPDATE SET imagem_capa=EXCLUDED.imagem_capa,
                alterado_por=@usuario,data_altera=now()
            """, new { idTema = row.IdTema, path, usuario }, cancellationToken: ct));
        logger.LogInformation("Primeira página do PDF {IdPdf} definida como capa do tema {IdTema} por {Usuario}", idPdf, row.IdTema, usuario ?? "api");
        return true;
    }

    public async Task<bool> DesativarAsync(long idPdf,string? usuario,CancellationToken ct)
    {
        await using var connection=await db.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE comercial.tema_pdf SET ativo=false,principal=false,alterado_por=@usuario,data_altera=now()
            WHERE idtema_pdf=@idPdf
            """,new{idPdf,usuario},cancellationToken:ct))>0;
    }

    public async Task<bool> ReordenarAsync(IReadOnlyCollection<ReordenarTemaPdfItem> items,string? usuario,CancellationToken ct)
    {
        if(items.Count==0)return true;
        if(items.Select(x=>x.IdTemaPdf).Distinct().Count()!=items.Count||items.Any(x=>x.Ordem<0))throw new ArgumentException("Ordenacao invalida.");
        await using var connection=await db.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        var themeCount=await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(DISTINCT idtema)::int FROM comercial.tema_pdf WHERE idtema_pdf=ANY(@ids)",new{ids=items.Select(x=>x.IdTemaPdf).ToArray()},transaction,cancellationToken:ct));
        if(themeCount!=1)return false;
        foreach(var item in items)
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE comercial.tema_pdf SET ordem=@ordem,alterado_por=@usuario,data_altera=now() WHERE idtema_pdf=@id",
                new{id=item.IdTemaPdf,item.Ordem,usuario},transaction,cancellationToken:ct));
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<string?> ObterPaginaPublicaAsync(long idPdf,int numero,CancellationToken ct)
    {
        const string sql="""
            SELECT pg.arquivo_imagem FROM comercial.tema_pdf_pagina pg
            JOIN comercial.tema_pdf p ON p.idtema_pdf=pg.idtema_pdf
            JOIN comercial.tema_catalogo tc ON tc.idtema=p.idtema
            JOIN comercial.temas t ON t.idtema=p.idtema
            WHERE pg.idtema_pdf=@idPdf AND pg.numero_pagina=@numero AND p.ativo=true AND p.status='ready'
              AND tc.publicado=true AND t.ativo='S'
            """;
        await using var connection=await db.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(sql,new{idPdf,numero},cancellationToken:ct));
    }

    public async Task<(string Path,string FileName)?> ObterDownloadPublicoAsync(long idPdf,CancellationToken ct)
    {
        const string sql="""
            SELECT p.arquivo_original AS Path,p.titulo AS FileName FROM comercial.tema_pdf p
            JOIN comercial.tema_catalogo tc ON tc.idtema=p.idtema JOIN comercial.temas t ON t.idtema=p.idtema
            WHERE p.idtema_pdf=@idPdf AND p.ativo=true AND p.status='ready' AND p.permitir_download=true
              AND tc.publicado=true AND t.ativo='S'
            """;
        await using var connection=await db.OpenConnectionAsync(ct);
        var row=await connection.QuerySingleOrDefaultAsync<DownloadRow>(new CommandDefinition(sql,new{idPdf},cancellationToken:ct));
        return row is null?null:(row.Path,SafeDownloadName(row.FileName)+".pdf");
    }

    private async Task<AdminTemaPdfDto?> ObterAsync(long idPdf,CancellationToken ct)
    { await using var connection=await db.OpenConnectionAsync(ct); return await connection.QuerySingleOrDefaultAsync<AdminTemaPdfDto>(new CommandDefinition(PdfSelect+" WHERE idtema_pdf=@idPdf",new{idPdf},cancellationToken:ct)); }
    private static void ValidateUpload(string title,string contentType)
    { if(string.IsNullOrWhiteSpace(title))throw new ArgumentException("Informe o titulo."); if(!string.Equals(contentType,"application/pdf",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Tipo de arquivo permitido: application/pdf."); }
    private static string? NullIfBlank(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    private sealed record PdfSource(int IdTema,string Original);
    private sealed record DownloadRow(string Path,string FileName);
    private static string SafeDownloadName(string value)
    {
        var name=string.Concat(value.Select(ch=>char.IsLetterOrDigit(ch)||ch is '-' or '_'?ch:'-')).Trim('-');
        return string.IsNullOrEmpty(name)?"apresentacao":name[..Math.Min(name.Length,120)];
    }
    private const string PdfSelect="""
        SELECT idtema_pdf AS "IdTemaPdf",idtema AS "IdTema",titulo AS "Titulo",descricao AS "Descricao",
               ordem AS "Ordem",ativo AS "Ativo",principal AS "Principal",permitir_download AS "PermitirDownload",
               quantidade_paginas AS "QuantidadePaginas",status AS "Status",mensagem_erro AS "MensagemErro",data_altera AS "DataAltera"
        FROM comercial.tema_pdf
        """;
}
