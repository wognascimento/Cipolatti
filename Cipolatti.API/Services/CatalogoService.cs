using System.Text.Json;
using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Cipolatti.API.Models.Catalogo;
using Dapper;

namespace Cipolatti.API.Services;

public sealed class CatalogoService(IDbConnectionFactory db) : ICatalogoService
{
    // Temporariamente, o catálogo lista todo tema existente, independentemente
    // do status editorial ou do campo legado de ativação.
    private const string PublicThemePredicate = "TRUE";

    public async Task<PagedResult<TemaResumoDto>> ListarTemasAsync(CatalogoTemasFilter filter, CancellationToken ct)
    {
        var parameters = new
        {
            filter.SearchPattern,
            filter.ConceptIds,
            HasConceptFilter = filter.ConceptIds.Length > 0,
            filter.MatchAll,
            filter.Destaque,
            filter.PageSize,
            Offset = (filter.Page - 1) * filter.PageSize
        };

        var sql = $"""
            SELECT count(*)
            FROM comercial.temas t
            LEFT JOIN comercial.tema_catalogo tc ON tc.idtema = t.idtema
            WHERE {PublicThemePredicate}
              AND (@Destaque IS NULL OR tc.destaque = @Destaque)
              AND (@SearchPattern IS NULL OR
                   t.temas ILIKE @SearchPattern ESCAPE '\' OR
                   t.nome_fantasia ILIKE @SearchPattern ESCAPE '\' OR
                   t.codinome ILIKE @SearchPattern ESCAPE '\' OR
                   t.descricao ILIKE @SearchPattern ESCAPE '\' OR
                   t.palavra_chave ILIKE @SearchPattern ESCAPE '\' OR
                   EXISTS (
                     SELECT 1 FROM comercial.conceito_tema cts
                     JOIN comercial.conceito cs ON cs.idconceito = cts.idconceito
                     WHERE cts.idtema = t.idtema
                       AND cs.conceito ILIKE @SearchPattern ESCAPE '\'))
              AND (NOT @HasConceptFilter OR
                   (NOT @MatchAll AND EXISTS (
                     SELECT 1 FROM comercial.conceito_tema ctf
                     WHERE ctf.idtema = t.idtema AND ctf.idconceito = ANY(@ConceptIds))) OR
                   (@MatchAll AND (
                     SELECT count(DISTINCT ctf.idconceito)
                     FROM comercial.conceito_tema ctf
                     WHERE ctf.idtema = t.idtema AND ctf.idconceito = ANY(@ConceptIds)
                   ) = cardinality(@ConceptIds)));

            SELECT t.idtema AS "IdTema", COALESCE(tc.slug, 'tema-' || t.idtema::text) AS "Slug",
                   COALESCE(NULLIF(btrim(tc.nome_publico), ''), t.temas) AS "Nome",
                   CASE WHEN tc.imagem_capa IS NULL THEN NULL ELSE '/api/catalogo/capas/' || t.idtema END AS "ImagemCapa",
                   COALESCE(NULLIF(btrim(tc.descricao_catalogo), ''), t.descricao) AS "Descricao",
                   COALESCE(tc.destaque, false) AS "Destaque", COALESCE(tc.inedito, false) AS "Inedito", COALESCE(tc.ordem, 0) AS "Ordem",
                   COALESCE(jsonb_agg(
                     jsonb_build_object('idConceito', c.idconceito, 'nome', c.conceito, 'quantidadeTemas', 0)
                     ORDER BY c.conceito
                   ) FILTER (WHERE c.idconceito IS NOT NULL), '[]'::jsonb)::text AS "ConceitosJson"
            FROM comercial.temas t
            LEFT JOIN comercial.tema_catalogo tc ON tc.idtema = t.idtema
            LEFT JOIN comercial.conceito_tema ct ON ct.idtema = t.idtema
            LEFT JOIN comercial.conceito c ON c.idconceito = ct.idconceito
            WHERE {PublicThemePredicate}
              AND (@Destaque IS NULL OR tc.destaque = @Destaque)
              AND (@SearchPattern IS NULL OR
                   t.temas ILIKE @SearchPattern ESCAPE '\' OR
                   t.nome_fantasia ILIKE @SearchPattern ESCAPE '\' OR
                   t.codinome ILIKE @SearchPattern ESCAPE '\' OR
                   t.descricao ILIKE @SearchPattern ESCAPE '\' OR
                   t.palavra_chave ILIKE @SearchPattern ESCAPE '\' OR
                   EXISTS (
                     SELECT 1 FROM comercial.conceito_tema cts
                     JOIN comercial.conceito cs ON cs.idconceito = cts.idconceito
                     WHERE cts.idtema = t.idtema
                       AND cs.conceito ILIKE @SearchPattern ESCAPE '\'))
              AND (NOT @HasConceptFilter OR
                   (NOT @MatchAll AND EXISTS (
                     SELECT 1 FROM comercial.conceito_tema ctf
                     WHERE ctf.idtema = t.idtema AND ctf.idconceito = ANY(@ConceptIds))) OR
                   (@MatchAll AND (
                     SELECT count(DISTINCT ctf.idconceito)
                     FROM comercial.conceito_tema ctf
                     WHERE ctf.idtema = t.idtema AND ctf.idconceito = ANY(@ConceptIds)
                   ) = cardinality(@ConceptIds)))
            GROUP BY t.idtema, t.temas, t.descricao, tc.slug, tc.nome_publico,
                     tc.imagem_capa, tc.descricao_catalogo, tc.destaque, tc.inedito, tc.ordem
            ORDER BY COALESCE(tc.inedito, false) DESC, COALESCE(tc.destaque, false) DESC, tc.ordem, "Nome", t.idtema
            LIMIT @PageSize OFFSET @Offset;
            """;

        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        var total = await results.ReadSingleAsync<long>();
        var rows = (await results.ReadAsync<TemaResumoRow>()).AsList();
        return new PagedResult<TemaResumoDto>(rows.Select(MapResumo).ToArray(), filter.Page, filter.PageSize, total);
    }

    public async Task<TemaDetalheDto?> ObterTemaAsync(string slug, CancellationToken ct)
    {
        var sql = $"""
            SELECT t.idtema AS "IdTema", COALESCE(tc.slug, 'tema-' || t.idtema::text) AS "Slug",
                   COALESCE(NULLIF(btrim(tc.nome_publico), ''), t.temas) AS "Nome",
                   CASE WHEN tc.imagem_capa IS NULL THEN NULL ELSE '/api/catalogo/capas/' || t.idtema END AS "ImagemCapa",
                   COALESCE(NULLIF(btrim(tc.descricao_catalogo), ''), t.descricao) AS "Descricao",
                   COALESCE(tc.destaque, false) AS "Destaque",
                   COALESCE(tc.inedito, false) AS "Inedito"
            FROM comercial.temas t
            LEFT JOIN comercial.tema_catalogo tc ON tc.idtema = t.idtema
            WHERE {PublicThemePredicate} AND COALESCE(tc.slug, 'tema-' || t.idtema::text) = @slug;

            SELECT c.idconceito AS "IdConceito", c.conceito AS "Nome", 0 AS "QuantidadeTemas",
                   CASE WHEN c.imagem_url IS NULL THEN NULL ELSE '/api/catalogo/conceitos/' || c.idconceito END AS "ImagemUrl"
            FROM comercial.temas t
            LEFT JOIN comercial.tema_catalogo tc ON tc.idtema = t.idtema
            JOIN comercial.conceito_tema ct ON ct.idtema = t.idtema
            JOIN comercial.conceito c ON c.idconceito = ct.idconceito
            WHERE {PublicThemePredicate} AND COALESCE(tc.slug, 'tema-' || t.idtema::text) = @slug
            ORDER BY c.conceito;

            {PdfsBySlugSql}

            {VideosBySlugSql}
            """;

        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(sql, new { slug }, cancellationToken: ct));
        var theme = await results.ReadSingleOrDefaultAsync<TemaDetalheRow>();
        var concepts = (await results.ReadAsync<ConceitoDto>()).AsList();
        var pdfs = (await results.ReadAsync<TemaPdfDto>()).AsList();
        var videos = (await results.ReadAsync<TemaVideoDto>()).AsList();
        return theme is null ? null : new TemaDetalheDto(theme.IdTema, theme.Slug, theme.Nome,
            theme.ImagemCapa, theme.Descricao, theme.Destaque, theme.Inedito, concepts, pdfs, videos);
    }

    public async Task<IReadOnlyCollection<ConceitoDto>> ListarConceitosAsync(CancellationToken ct)
    {
        var sql = $"""
            SELECT c.idconceito AS "IdConceito", c.conceito AS "Nome",
                   count(DISTINCT t.idtema)::int AS "QuantidadeTemas",CASE WHEN c.imagem_url IS NULL THEN NULL ELSE '/api/catalogo/conceitos/'||c.idconceito END AS "ImagemUrl"
            FROM comercial.conceito c
            LEFT JOIN comercial.conceito_tema ct ON ct.idconceito = c.idconceito
            LEFT JOIN comercial.temas t ON t.idtema = ct.idtema
            LEFT JOIN comercial.tema_catalogo tc ON tc.idtema = t.idtema
            GROUP BY c.idconceito, c.conceito, c.imagem_url
            ORDER BY c.conceito;
            """;
        await using var connection = await db.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<ConceitoDto>(new CommandDefinition(sql, cancellationToken: ct))).AsList();
    }

    public Task<IReadOnlyCollection<TemaPdfDto>?> ListarPdfsAsync(string slug, CancellationToken ct) =>
        ListBySlugAsync<TemaPdfDto>(slug, PdfsBySlugSql, ct);

    public async Task<IReadOnlyCollection<TemaPdfPaginaDto>?> ListarPaginasAsync(long idPdf, CancellationToken ct)
    {
        var sql = $"""
            SELECT EXISTS (
              SELECT 1 FROM comercial.tema_pdf p
              JOIN comercial.temas t ON t.idtema = p.idtema
              JOIN comercial.tema_catalogo tc ON tc.idtema = t.idtema
              WHERE {PublicThemePredicate} AND p.idtema_pdf = @idPdf
                AND p.ativo = true AND p.status = 'ready');

            SELECT pg.idtema_pdf_pagina AS "IdTemaPdfPagina",
                   pg.numero_pagina AS "NumeroPagina",
                   '/api/catalogo/pdfs/' || pg.idtema_pdf || '/paginas/' || pg.numero_pagina || '/arquivo' AS "ArquivoImagem",
                   pg.largura AS "Largura", pg.altura AS "Altura", pg.tamanho_bytes AS "TamanhoBytes"
            FROM comercial.tema_pdf_pagina pg
            JOIN comercial.tema_pdf p ON p.idtema_pdf = pg.idtema_pdf
            JOIN comercial.temas t ON t.idtema = p.idtema
            JOIN comercial.tema_catalogo tc ON tc.idtema = t.idtema
            WHERE {PublicThemePredicate} AND p.idtema_pdf = @idPdf
              AND p.ativo = true AND p.status = 'ready'
            ORDER BY pg.numero_pagina;
            """;
        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(sql, new { idPdf }, cancellationToken: ct));
        var exists = await results.ReadSingleAsync<bool>();
        var pages = (await results.ReadAsync<TemaPdfPaginaDto>()).AsList();
        return exists ? pages : null;
    }

    public Task<IReadOnlyCollection<TemaVideoDto>?> ListarVideosAsync(string slug, CancellationToken ct) =>
        ListBySlugAsync<TemaVideoDto>(slug, VideosBySlugSql, ct);

    public async Task<IReadOnlyCollection<TemaResumoDto>?> ListarRelacionadosAsync(string slug, CancellationToken ct)
    {
        var sql = $"""
            SELECT EXISTS (
              SELECT 1 FROM comercial.temas t
              JOIN comercial.tema_catalogo tc ON tc.idtema=t.idtema
              WHERE {PublicThemePredicate} AND tc.slug=@slug);

            WITH atual AS (
              SELECT t.idtema, t.palavra_chave, t.caracteristicas
              FROM comercial.temas t
              JOIN comercial.tema_catalogo tc ON tc.idtema=t.idtema
              WHERE {PublicThemePredicate} AND tc.slug=@slug
            ), candidatos AS (
              SELECT t.idtema, tc.slug,
                     COALESCE(NULLIF(btrim(tc.nome_publico), ''), t.temas) AS nome,
                     CASE WHEN tc.imagem_capa IS NULL THEN NULL ELSE '/api/catalogo/capas/' || t.idtema END AS imagem_capa,
                     COALESCE(NULLIF(btrim(tc.descricao_catalogo), ''), t.descricao) AS descricao,
                     tc.destaque, tc.inedito, tc.ordem,
                     (SELECT count(*)::int * 2
                        FROM comercial.conceito_tema a
                        JOIN comercial.conceito_tema b ON b.idconceito=a.idconceito
                       WHERE a.idtema=atual.idtema AND b.idtema=t.idtema)
                     + CASE WHEN EXISTS (
                         SELECT 1
                         FROM regexp_split_to_table(lower(concat_ws(' ', atual.palavra_chave, atual.caracteristicas)), '[^[:alnum:]]+') token
                         WHERE length(token) >= 4
                           AND lower(concat_ws(' ', t.palavra_chave, t.caracteristicas)) LIKE '%' || token || '%'
                       ) THEN 1 ELSE 0 END AS pontuacao
              FROM atual
              JOIN comercial.temas t ON t.idtema <> atual.idtema
              JOIN comercial.tema_catalogo tc ON tc.idtema=t.idtema
              WHERE {PublicThemePredicate}
            ), melhores AS (
              SELECT * FROM candidatos WHERE pontuacao > 0
              ORDER BY pontuacao DESC, COALESCE(inedito, false) DESC, COALESCE(destaque, false) DESC, ordem, nome, idtema
              LIMIT 4
            )
            SELECT m.idtema AS "IdTema", m.slug AS "Slug", m.nome AS "Nome",
                   m.imagem_capa AS "ImagemCapa", m.descricao AS "Descricao",
                   m.destaque AS "Destaque", m.inedito AS "Inedito", m.ordem AS "Ordem",
                   COALESCE(jsonb_agg(
                     jsonb_build_object('idConceito', c.idconceito, 'nome', c.conceito, 'quantidadeTemas', 0)
                     ORDER BY c.conceito
                   ) FILTER (WHERE c.idconceito IS NOT NULL), '[]'::jsonb)::text AS "ConceitosJson"
            FROM melhores m
            LEFT JOIN comercial.conceito_tema ct ON ct.idtema=m.idtema
            LEFT JOIN comercial.conceito c ON c.idconceito=ct.idconceito
            GROUP BY m.idtema,m.slug,m.nome,m.imagem_capa,m.descricao,m.destaque,m.inedito,m.ordem,m.pontuacao
            ORDER BY m.pontuacao DESC,COALESCE(m.inedito, false) DESC,COALESCE(m.destaque, false) DESC,m.ordem,m.nome,m.idtema;
            """;
        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(sql, new { slug }, cancellationToken: ct));
        var exists = await results.ReadSingleAsync<bool>();
        var rows = (await results.ReadAsync<TemaResumoRow>()).AsList();
        return exists ? rows.Select(MapResumo).ToArray() : null;
    }

    private async Task<IReadOnlyCollection<T>?> ListBySlugAsync<T>(string slug, string resourceSql, CancellationToken ct)
    {
        var sql = $"""
            SELECT EXISTS (
              SELECT 1 FROM comercial.temas t
              JOIN comercial.tema_catalogo tc ON tc.idtema=t.idtema
              WHERE {PublicThemePredicate} AND tc.slug=@slug);
            {resourceSql}
            """;
        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(sql, new { slug }, cancellationToken: ct));
        var exists = await results.ReadSingleAsync<bool>();
        var items = (await results.ReadAsync<T>()).AsList();
        return exists ? items : null;
    }

    private static TemaResumoDto MapResumo(TemaResumoRow row) => new(row.IdTema, row.Slug, row.Nome,
        row.ImagemCapa, row.Descricao, row.Destaque, row.Inedito, row.Ordem,
        JsonSerializer.Deserialize<ConceitoDto[]>(row.ConceitosJson, JsonOptions) ?? []);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string PdfsBySlugSql = """
        SELECT p.idtema_pdf AS "IdTemaPdf", p.titulo AS "Titulo", p.descricao AS "Descricao",
               p.ordem AS "Ordem", p.principal AS "Principal",
               p.permitir_download AS "PermitirDownload", p.quantidade_paginas AS "QuantidadePaginas"
        FROM comercial.tema_pdf p
        JOIN comercial.temas t ON t.idtema = p.idtema
        JOIN comercial.tema_catalogo tc ON tc.idtema = t.idtema
        WHERE tc.publicado = true AND t.ativo = 'S' AND tc.slug = @slug
          AND p.ativo = true AND p.status = 'ready'
        ORDER BY p.principal DESC, p.ordem, p.idtema_pdf;
        """;

    private const string VideosBySlugSql = """
        SELECT v.idtema_video AS "IdTemaVideo", v.titulo AS "Titulo", v.descricao AS "Descricao",
               v.youtube_video_id AS "YoutubeVideoId", v.youtube_url AS "YoutubeUrl",
               v.thumbnail_url AS "ThumbnailUrl", v.ordem AS "Ordem",
               v.privacy_status AS "PrivacyStatus"
        FROM comercial.tema_video v
        JOIN comercial.temas t ON t.idtema = v.idtema
        JOIN comercial.tema_catalogo tc ON tc.idtema = t.idtema
        WHERE tc.publicado = true AND t.ativo = 'S' AND tc.slug = @slug
          AND v.ativo = true AND v.status IN ('ready', 'processing')
          AND v.youtube_video_id IS NOT NULL
          AND COALESCE(v.privacy_status, 'unlisted') <> 'private'
        ORDER BY v.ordem, v.idtema_video;
        """;

    private sealed record TemaResumoRow(int IdTema, string Slug, string Nome, string? ImagemCapa,
        string? Descricao, bool Destaque, bool Inedito, int Ordem, string ConceitosJson);
    private sealed record TemaDetalheRow(int IdTema, string Slug, string Nome, string? ImagemCapa,
        string? Descricao, bool Destaque, bool Inedito);
}
