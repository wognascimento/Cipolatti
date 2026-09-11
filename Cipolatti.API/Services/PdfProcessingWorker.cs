using Cipolatti.API.Infrastructure;
using Dapper;

namespace Cipolatti.API.Services;

public sealed class PdfProcessingWorker(IPdfJobQueue queue, IServiceScopeFactory scopes,
    ILogger<PdfProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try{await RecoverAsync(stoppingToken);break;}
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){return;}
            catch(Exception ex){logger.LogWarning(ex,"Nao foi possivel recuperar jobs de PDF; nova tentativa em 15 segundos");await Task.Delay(TimeSpan.FromSeconds(15),stoppingToken);}
        }
        await foreach(var job in queue.ReadAllAsync(stoppingToken))
        {
            try { await ProcessAsync(job,stoppingToken); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogError(ex,"Falha inesperada no job do PDF {IdPdf}",job.IdPdf);}
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope=scopes.CreateScope();
        var db=scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        var storage=scope.ServiceProvider.GetRequiredService<IFileStorage>();
        await using var connection=await db.OpenConnectionAsync(ct);
        var ids=await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT idtema_pdf FROM comercial.tema_pdf WHERE status IN ('pending','processing') ORDER BY data_cadastro",cancellationToken:ct));
        foreach(var id in ids)await queue.EnqueueAsync(new(id),ct);
        foreach(var job in await storage.ReadPersistedPdfJobsAsync(ct))await queue.EnqueueAsync(job,ct);
    }

    private async Task ProcessAsync(PdfProcessingJob job,CancellationToken ct)
    {
        using var scope=scopes.CreateScope();
        var db=scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        var renderer=scope.ServiceProvider.GetRequiredService<IPdfRenderer>();
        var storage=scope.ServiceProvider.GetRequiredService<IFileStorage>();
        await using var connection=await db.OpenConnectionAsync(ct);
        var row=await connection.QuerySingleOrDefaultAsync<PdfWorkRow>(new CommandDefinition(
            "SELECT idtema AS IdTema,arquivo_original AS Original FROM comercial.tema_pdf WHERE idtema_pdf=@id",new{id=job.IdPdf},cancellationToken:ct));
        if(row is null){if(job.Replacement)await storage.CompletePdfJobAsync(job,ct);return;}
        var original=job.StagedOriginal??row.Original;
        var version=job.Version??PathSegment(original,"versions");
        if(!job.Replacement)
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE comercial.tema_pdf SET status='processing',mensagem_erro=NULL,data_altera=now() WHERE idtema_pdf=@id",
                new{id=job.IdPdf},cancellationToken:ct));
        var terminal=false;
        try
        {
            var pages=await renderer.RenderAsync(row.IdTema,job.IdPdf,original,version,ct);
            await using var transaction=await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM comercial.tema_pdf_pagina WHERE idtema_pdf=@id",new{id=job.IdPdf},transaction,cancellationToken:ct));
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO comercial.tema_pdf_pagina(idtema_pdf,numero_pagina,arquivo_imagem,largura,altura,tamanho_bytes)
                VALUES(@IdPdf,@PageNumber,@RelativePath,@Width,@Height,@SizeBytes)
                """,pages.Select(page=>new{IdPdf=job.IdPdf,page.PageNumber,page.RelativePath,page.Width,page.Height,page.SizeBytes}),transaction,cancellationToken:ct));
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE comercial.tema_pdf SET arquivo_original=@original,quantidade_paginas=@count,status='ready',
                    mensagem_erro=NULL,data_altera=now() WHERE idtema_pdf=@id
                """,new{original,count=pages.Count,id=job.IdPdf},transaction,cancellationToken:ct));
            await transaction.CommitAsync(ct);
            terminal=true;
            logger.LogInformation("PDF {IdPdf} processado com {Paginas} paginas",job.IdPdf,pages.Count);
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            await connection.ExecuteAsync(new CommandDefinition(job.Replacement
                ? "UPDATE comercial.tema_pdf SET mensagem_erro=@error,data_altera=now() WHERE idtema_pdf=@id"
                : "UPDATE comercial.tema_pdf SET status='error',mensagem_erro=@error,data_altera=now() WHERE idtema_pdf=@id",
                new{id=job.IdPdf,error=ex.Message[..Math.Min(ex.Message.Length,2000)]},cancellationToken:CancellationToken.None));
            terminal=true;
            logger.LogError(ex,"Erro ao processar PDF {IdPdf}; versao publica anterior foi preservada: {Replacement}",job.IdPdf,job.Replacement);
        }
        finally
        {
            if(job.Replacement&&terminal)await storage.CompletePdfJobAsync(job,CancellationToken.None);
        }
    }

    private static string PathSegment(string path,string marker){var parts=path.Split('/');var index=Array.IndexOf(parts,marker);return index>=0&&index+1<parts.Length?parts[index+1]:throw new InvalidDataException("Path do PDF invalido.");}
    private sealed record PdfWorkRow(int IdTema,string Original);
}
