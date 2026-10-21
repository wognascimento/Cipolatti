using System.Globalization;
using System.Text;
using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Cipolatti.API.Models.Catalogo;
using Dapper;
using Npgsql;

namespace Cipolatti.API.Services;

public sealed class CatalogoAdminService(IDbConnectionFactory db, IFileStorage storage, ILogger<CatalogoAdminService> logger) : ICatalogoAdminService
{
    public async Task<PagedResult<AdminTemaResumoDto>> ListarAsync(string? busca, int page, int pageSize, CancellationToken ct)
    {
        var pattern = string.IsNullOrWhiteSpace(busca) ? null : $"%{EscapeLike(busca.Trim())}%";
        const string sql = """
            SELECT count(*) FROM comercial.temas t
            WHERE @pattern IS NULL OR t.temas ILIKE @pattern ESCAPE '\' OR t.nome_fantasia ILIKE @pattern ESCAPE '\';
            SELECT t.idtema AS "IdTema", COALESCE(NULLIF(btrim(tc.nome_publico),''),t.temas) AS "Nome",
                   tc.slug AS "Slug", COALESCE(tc.publicado,false) AS "Publicado",
                   COALESCE(tc.destaque,false) AS "Destaque",
                   string_agg(DISTINCT c.conceito, ', ' ORDER BY c.conceito) AS "Conceitos",
                   count(DISTINCT p.idtema_pdf)::int AS "Pdfs", count(DISTINCT v.idtema_video)::int AS "Videos",
                   COALESCE(tc.data_altera,tc.data_cadastro,t.data_altera,t.data_atualizacao,t.data_cadastro) AS "Atualizacao"
            FROM comercial.temas t
            LEFT JOIN comercial.tema_catalogo tc ON tc.idtema=t.idtema
            LEFT JOIN comercial.conceito_tema ct ON ct.idtema=t.idtema
            LEFT JOIN comercial.conceito c ON c.idconceito=ct.idconceito
            LEFT JOIN comercial.tema_pdf p ON p.idtema=t.idtema AND p.ativo=true
            LEFT JOIN comercial.tema_video v ON v.idtema=t.idtema AND v.ativo=true
            WHERE @pattern IS NULL OR t.temas ILIKE @pattern ESCAPE '\' OR t.nome_fantasia ILIKE @pattern ESCAPE '\'
            GROUP BY t.idtema,t.temas,t.data_altera,t.data_atualizacao,t.data_cadastro,
                     tc.nome_publico,tc.slug,tc.publicado,tc.destaque,tc.data_altera,tc.data_cadastro
            ORDER BY COALESCE(NULLIF(btrim(tc.nome_publico),''),t.temas),t.idtema
            LIMIT @pageSize OFFSET @offset;
            """;
        await using var connection = await db.OpenConnectionAsync(ct);
        using var result = await connection.QueryMultipleAsync(new CommandDefinition(sql,
            new { pattern, pageSize, offset = (page - 1) * pageSize }, cancellationToken: ct));
        var total = await result.ReadSingleAsync<long>();
        var items = (await result.ReadAsync<AdminTemaResumoDto>()).AsList();
        return new(items, page, pageSize, total);
    }

    public async Task<AdminTemaDetalheDto?> ObterAsync(int idTema, CancellationToken ct)
    {
        const string sql = """
            SELECT t.idtema AS "IdTema",t.temas AS "NomeLegado",tc.nome_publico AS "NomePublico",
                   tc.slug AS "Slug",tc.imagem_capa AS "ImagemCapa",
                   COALESCE(NULLIF(btrim(tc.descricao_catalogo),''),t.descricao) AS "DescricaoCatalogo",
                   COALESCE(tc.publicado,false) AS "Publicado",COALESCE(tc.destaque,false) AS "Destaque",
                   COALESCE(tc.inedito,false) AS "Inedito",
                   COALESCE(tc.ordem,0) AS "Ordem"
            FROM comercial.temas t LEFT JOIN comercial.tema_catalogo tc ON tc.idtema=t.idtema
            WHERE t.idtema=@idTema;
            SELECT idconceito FROM comercial.conceito_tema WHERE idtema=@idTema ORDER BY idconceito;
            SELECT idtema_capa AS "IdTemaCapa", '/api/catalogo/capas/'||idtema||'/'||idtema_capa AS "ImagemUrl", ordem AS "Ordem"
            FROM comercial.tema_capa WHERE idtema=@idTema AND ativo=true ORDER BY ordem,idtema_capa;
            """;
        await using var connection = await db.OpenConnectionAsync(ct);
        using var result = await connection.QueryMultipleAsync(new CommandDefinition(sql, new { idTema }, cancellationToken: ct));
        var row = await result.ReadSingleOrDefaultAsync<AdminTemaRow>();
        var concepts = (await result.ReadAsync<int>()).AsList();
        var covers = (await result.ReadAsync<AdminTemaCapaDto>()).AsList();
        return row is null ? null : Map(row, concepts, covers);
    }

    public async Task<IReadOnlyCollection<ConceitoDto>> ListarConceitosAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT c.idconceito AS "IdConceito",c.conceito AS "Nome",
                   count(DISTINCT ct.idtema)::int AS "QuantidadeTemas",CASE WHEN c.imagem_url IS NULL THEN NULL ELSE '/api/catalogo/conceitos/'||c.idconceito END AS "ImagemUrl"
            FROM comercial.conceito c
            LEFT JOIN comercial.conceito_tema ct ON ct.idconceito=c.idconceito
            GROUP BY c.idconceito,c.conceito
            ORDER BY c.conceito
            """;
        await using var connection = await db.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<ConceitoDto>(new CommandDefinition(sql, cancellationToken: ct))).AsList();
    }

    public async Task<ConceitoDto> CriarConceitoAsync(SalvarConceitoRequest request, string? usuario, CancellationToken ct)
    {
        var nome=ValidarNomeConceito(request.Nome);
        await using var connection=await db.OpenConnectionAsync(ct);
        if(await ConceitoDuplicadoAsync(connection,nome,null,ct))throw new CatalogConflictException("Ja existe um conceito com este nome.");
        try
        {
            var id=await connection.QuerySingleAsync<int>(new CommandDefinition("""
                INSERT INTO comercial.conceito(conceito,imagem_url,inserido_por,data_cadastro,alterado_por,data_altera)
                VALUES(@nome,@imagemUrl,@usuario,now(),@usuario,now()) RETURNING idconceito
                """,new{nome,imagemUrl=NullIfBlank(request.ImagemUrl),usuario},cancellationToken:ct));
            logger.LogInformation("Conceito {IdConceito} cadastrado por {Usuario}",id,usuario??"api");
            return new(id,nome,0,NullIfBlank(request.ImagemUrl));
        }
        catch(PostgresException ex)when(ex.SqlState==PostgresErrorCodes.UniqueViolation)
        {throw new CatalogConflictException("Ja existe um conceito com este nome.");}
    }

    public async Task<ConceitoDto?> AtualizarConceitoAsync(int idConceito, SalvarConceitoRequest request, string? usuario, CancellationToken ct)
    {
        var nome=ValidarNomeConceito(request.Nome);
        await using var connection=await db.OpenConnectionAsync(ct);
        if(await ConceitoDuplicadoAsync(connection,nome,idConceito,ct))throw new CatalogConflictException("Ja existe um conceito com este nome.");
        try
        {
            var changed=await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE comercial.conceito SET conceito=@nome,imagem_url=@imagemUrl,alterado_por=@usuario,data_altera=now()
                WHERE idconceito=@idConceito
                """,new{idConceito,nome,imagemUrl=NullIfBlank(request.ImagemUrl),usuario},cancellationToken:ct));
            if(changed==0)return null;
            var quantidade=await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(DISTINCT idtema)::int FROM comercial.conceito_tema WHERE idconceito=@idConceito",
                new{idConceito},cancellationToken:ct));
            logger.LogInformation("Conceito {IdConceito} atualizado por {Usuario}",idConceito,usuario??"api");
            return new(idConceito,nome,quantidade,NullIfBlank(request.ImagemUrl));
        }
        catch(PostgresException ex)when(ex.SqlState==PostgresErrorCodes.UniqueViolation)
        {throw new CatalogConflictException("Ja existe um conceito com este nome.");}
    }

    public async Task<bool> ExcluirConceitoAsync(int idConceito,bool desassociar,string? usuario,CancellationToken ct)
    {
        await using var connection=await db.OpenConnectionAsync(ct); await using var tx=await connection.BeginTransactionAsync(ct);
        var exists=await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM comercial.conceito WHERE idconceito=@idConceito)",new{idConceito},tx,cancellationToken:ct));
        if(!exists)return false;
        var count=await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::int FROM comercial.conceito_tema WHERE idconceito=@idConceito",new{idConceito},tx,cancellationToken:ct));
        if(count>0&&!desassociar)throw new InvalidOperationException($"Este conceito está associado a {count} tema(s). Confirme a desassociação para continuar.");
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM comercial.conceito_tema WHERE idconceito=@idConceito; DELETE FROM comercial.conceito WHERE idconceito=@idConceito",new{idConceito},tx,cancellationToken:ct)); await tx.CommitAsync(ct); return true;
    }

    private static async Task<bool> ConceitoDuplicadoAsync(System.Data.Common.DbConnection connection,string nome,int? id,CancellationToken ct)=>
        await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM comercial.conceito WHERE lower(btrim(conceito))=lower(@nome) AND (@id IS NULL OR idconceito<>@id))",
            new{nome,id},cancellationToken:ct));

    private static string ValidarNomeConceito(string nome)
    {
        nome=nome.Trim();
        if(nome.Length==0)throw new ArgumentException("Informe o nome do conceito.");
        if(nome.Length>254)throw new ArgumentException("O conceito deve ter no maximo 254 caracteres.");
        return nome;
    }

    public async Task<AdminTemaDetalheDto?> AtualizarAsync(int idTema, AtualizarTemaCatalogoRequest request, string? usuario, CancellationToken ct)
    {
        var conceptIds = request.ConceitoIds.Distinct().Order().ToArray();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var legacyName = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT temas FROM comercial.temas WHERE idtema=@idTema", new { idTema }, transaction, cancellationToken: ct));
        if (legacyName is null) return null;
        if (conceptIds.Length > 0)
        {
            var valid = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM comercial.conceito WHERE idconceito=ANY(@conceptIds)", new { conceptIds }, transaction, cancellationToken: ct));
            if (valid != conceptIds.Length) throw new ArgumentException("Um ou mais conceitos não existem.", nameof(request));
        }
        var slugBase = Slugify(string.IsNullOrWhiteSpace(request.Slug) ? request.NomePublico ?? legacyName : request.Slug);
        if (slugBase.Length == 0) throw new ArgumentException("Não foi possível gerar um slug válido.", nameof(request));
        slugBase = slugBase[..Math.Min(slugBase.Length, 170)].TrimEnd('-');
        var slug = slugBase;
        for (var suffix = 2; await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                 "SELECT EXISTS(SELECT 1 FROM comercial.tema_catalogo WHERE slug=@slug AND idtema<>@idTema)", new { slug, idTema }, transaction, cancellationToken: ct)); suffix++)
            slug = $"{slugBase}-{suffix}";
        const string upsert = """
            INSERT INTO comercial.tema_catalogo
              (idtema,slug,nome_publico,descricao_catalogo,publicado,destaque,inedito,ordem,data_publicacao,inserido_por,data_cadastro,alterado_por,data_altera)
            VALUES
              (@idTema,@slug,@nomePublico,@descricao,@publicado,@destaque,@inedito,@ordem,CASE WHEN @publicado THEN now() ELSE NULL END,@usuario,now(),@usuario,now())
            ON CONFLICT (idtema) DO UPDATE SET
              slug=EXCLUDED.slug,nome_publico=EXCLUDED.nome_publico,descricao_catalogo=EXCLUDED.descricao_catalogo,
              publicado=EXCLUDED.publicado,destaque=EXCLUDED.destaque,inedito=EXCLUDED.inedito,ordem=EXCLUDED.ordem,
              data_publicacao=CASE WHEN EXCLUDED.publicado AND NOT tema_catalogo.publicado THEN now()
                                    WHEN NOT EXCLUDED.publicado THEN NULL ELSE tema_catalogo.data_publicacao END,
              alterado_por=@usuario,data_altera=now();
            DELETE FROM comercial.conceito_tema WHERE idtema=@idTema AND NOT (idconceito=ANY(@conceptIds));
            INSERT INTO comercial.conceito_tema (idtema,idconceito)
            SELECT @idTema,idconceito FROM unnest(@conceptIds::integer[]) idconceito
            ON CONFLICT (idtema,idconceito) DO NOTHING;
            """;
        await connection.ExecuteAsync(new CommandDefinition(upsert, new
        {
            idTema, slug, nomePublico = NullIfBlank(request.NomePublico), descricao = NullIfBlank(request.DescricaoCatalogo),
            request.Publicado, request.Destaque, request.Inedito, request.Ordem, conceptIds, usuario
        }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        logger.LogInformation("Tema {IdTema} atualizado no catálogo por {Usuario}. Publicado: {Publicado}", idTema, usuario ?? "api", request.Publicado);
        return await ObterAsync(idTema, ct);
    }

    public async Task<string?> AtualizarCapaAsync(int idTema, Stream content, string contentType, string? usuario, CancellationToken ct)
    {
        if (contentType is not ("image/jpeg" or "image/png" or "image/webp"))
            throw new InvalidDataException("Tipo de imagem não permitido.");
        await using var connection = await db.OpenConnectionAsync(ct);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM comercial.tema_catalogo WHERE idtema=@idTema)", new { idTema }, cancellationToken: ct))) return null;
        var path = await storage.SaveCoverAsWebpAsync(idTema, content, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE comercial.tema_catalogo SET imagem_capa=@path,alterado_por=@usuario,data_altera=now() WHERE idtema=@idTema",
            new { path, idTema, usuario }, cancellationToken: ct));
        logger.LogInformation("Capa do tema {IdTema} atualizada por {Usuario}", idTema, usuario ?? "api");
        return path;
    }

    public async Task<string?> ObterCapaPublicaAsync(int idTema, CancellationToken ct)
    {
        const string sql = """SELECT tc.imagem_capa FROM comercial.tema_catalogo tc JOIN comercial.temas t ON t.idtema=tc.idtema WHERE tc.idtema=@idTema AND tc.publicado=true AND t.ativo='S'""";
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(sql, new { idTema }, cancellationToken: ct));
    }

    public async Task<IReadOnlyCollection<AdminTemaCapaDto>?> AdicionarCapasAsync(int idTema, IReadOnlyCollection<(Stream Content, string ContentType)> imagens, string? usuario, CancellationToken ct)
    {
        if (imagens.Count == 0) throw new InvalidDataException("Informe ao menos uma imagem.");
        if (imagens.Any(x => x.ContentType is not ("image/jpeg" or "image/png" or "image/webp"))) throw new InvalidDataException("Formato permitido: JPEG, PNG ou WebP.");
        await using var connection = await db.OpenConnectionAsync(ct);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM comercial.temas WHERE idtema=@idTema)", new { idTema }, cancellationToken: ct))) return null;
        var paths = new List<string>();
        foreach (var imagem in imagens) paths.Add(await storage.SaveGalleryCoverAsWebpAsync(idTema, imagem.Content, ct));
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var ordem = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(max(ordem)+1,0)::int FROM comercial.tema_capa WHERE idtema=@idTema AND ativo=true", new { idTema }, transaction, cancellationToken: ct));
        foreach (var path in paths)
        {
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO comercial.tema_capa(idtema,arquivo_imagem,ordem,ativo,inserido_por) VALUES(@idTema,@path,@ordem,true,@usuario)", new { idTema, path, ordem, usuario }, transaction, cancellationToken: ct));
            ordem++;
        }
        await connection.ExecuteAsync(new CommandDefinition("UPDATE comercial.tema_catalogo SET imagem_capa=COALESCE(imagem_capa,@path),alterado_por=@usuario,data_altera=now() WHERE idtema=@idTema", new { idTema, path = paths[0], usuario }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        logger.LogInformation("{Quantidade} capas adicionadas ao tema {IdTema} por {Usuario}", paths.Count, idTema, usuario ?? "api");
        return (await connection.QueryAsync<AdminTemaCapaDto>(new CommandDefinition("SELECT idtema_capa AS \"IdTemaCapa\", '/api/catalogo/capas/'||idtema||'/'||idtema_capa AS \"ImagemUrl\", ordem AS \"Ordem\" FROM comercial.tema_capa WHERE idtema=@idTema AND ativo=true ORDER BY ordem,idtema_capa", new { idTema }, cancellationToken: ct))).AsList();
    }

    public async Task<bool> RemoverCapaAsync(long idTemaCapa, string? usuario, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct); await using var transaction = await connection.BeginTransactionAsync(ct);
        var cover = await connection.QuerySingleOrDefaultAsync<CapaArquivoRow>(new CommandDefinition("SELECT idtema AS \"IdTema\",arquivo_imagem AS \"Arquivo\" FROM comercial.tema_capa WHERE idtema_capa=@idTemaCapa AND ativo=true", new { idTemaCapa }, transaction, cancellationToken: ct));
        if (cover is null) return false;
        await connection.ExecuteAsync(new CommandDefinition("UPDATE comercial.tema_capa SET ativo=false,alterado_por=@usuario,data_altera=now() WHERE idtema_capa=@idTemaCapa", new { idTemaCapa, usuario }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("UPDATE comercial.tema_catalogo SET imagem_capa=(SELECT arquivo_imagem FROM comercial.tema_capa WHERE idtema=@idTema AND ativo=true ORDER BY ordem,idtema_capa LIMIT 1),alterado_por=@usuario,data_altera=now() WHERE idtema=@idTema AND imagem_capa=@arquivo", new { idTema = cover.IdTema, arquivo = cover.Arquivo, usuario }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct); return true;
    }

    public async Task<string?> ObterCapaGaleriaPublicaAsync(int idTema, long idTemaCapa, CancellationToken ct)
    {
        const string sql = """SELECT c.arquivo_imagem FROM comercial.tema_capa c JOIN comercial.temas t ON t.idtema=c.idtema WHERE c.idtema=@idTema AND c.idtema_capa=@idTemaCapa AND c.ativo=true AND t.ativo='S'""";
        await using var connection = await db.OpenConnectionAsync(ct); return await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(sql, new { idTema, idTemaCapa }, cancellationToken: ct));
    }

    public async Task<bool> AtualizarImagemConceitoAsync(int idConceito,Stream content,string contentType,string? usuario,CancellationToken ct)
    { if(contentType is not("image/jpeg" or "image/png" or "image/webp"))throw new InvalidDataException("Formato permitido: JPEG, PNG ou WebP."); await using var connection=await db.OpenConnectionAsync(ct);if(!await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM comercial.conceito WHERE idconceito=@idConceito)",new{idConceito},cancellationToken:ct)))return false;var path=await storage.SaveConceptAsWebpAsync(idConceito,content,ct);await connection.ExecuteAsync(new CommandDefinition("UPDATE comercial.conceito SET imagem_url=@path,alterado_por=@usuario,data_altera=now() WHERE idconceito=@idConceito",new{path,idConceito,usuario},cancellationToken:ct));return true; }
    public async Task<string?> ObterImagemConceitoAsync(int idConceito,CancellationToken ct){await using var connection=await db.OpenConnectionAsync(ct);return await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT imagem_url FROM comercial.conceito WHERE idconceito=@idConceito",new{idConceito},cancellationToken:ct));}

    private static AdminTemaDetalheDto Map(AdminTemaRow row, IReadOnlyCollection<int> concepts, IReadOnlyCollection<AdminTemaCapaDto> covers) => new(row.IdTema,
        row.NomeLegado,row.NomePublico,row.Slug,row.ImagemCapa,row.DescricaoCatalogo,row.Publicado,row.Destaque,row.Inedito,row.Ordem,concepts,covers);
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    internal static string Slugify(string value)
    {
        var normalized=value.Normalize(NormalizationForm.FormD); var result=new StringBuilder(); var hyphen=false;
        foreach(var ch in normalized)
        {
            if(CharUnicodeInfo.GetUnicodeCategory(ch)==UnicodeCategory.NonSpacingMark) continue;
            if(char.IsLetterOrDigit(ch)){result.Append(char.ToLowerInvariant(ch));hyphen=false;}
            else if(result.Length>0&&!hyphen){result.Append('-');hyphen=true;}
        }
        return result.ToString().Trim('-');
    }
    private sealed record AdminTemaRow(int IdTema,string NomeLegado,string? NomePublico,string? Slug,string? ImagemCapa,
        string? DescricaoCatalogo,bool Publicado,bool Destaque,bool Inedito,int Ordem);
    private sealed record CapaArquivoRow(int IdTema, string Arquivo);
}
