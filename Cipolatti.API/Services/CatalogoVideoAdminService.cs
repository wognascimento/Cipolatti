using System.Text.RegularExpressions;
using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models.Catalogo;
using Dapper;
using Npgsql;

namespace Cipolatti.API.Services;

public sealed partial class CatalogoVideoAdminService(IDbConnectionFactory db,ILogger<CatalogoVideoAdminService> logger):ICatalogoVideoAdminService
{
    public async Task<IReadOnlyCollection<AdminTemaVideoDto>?> ListarAsync(int idTema,CancellationToken ct)
    {
        await using var connection=await db.OpenConnectionAsync(ct);
        if(!await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM comercial.temas WHERE idtema=@idTema)",new{idTema},cancellationToken:ct)))return null;
        return(await connection.QueryAsync<AdminTemaVideoDto>(new CommandDefinition(VideoSelect+" WHERE idtema=@idTema ORDER BY ordem,idtema_video",new{idTema},cancellationToken:ct))).AsList();
    }

    public async Task<AdminTemaVideoDto?> CriarAsync(int idTema,CriarTemaVideoRequest request,string? usuario,CancellationToken ct)
    {
        var input=Normalize(request);
        await using var connection=await db.OpenConnectionAsync(ct);
        if(!await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM comercial.temas WHERE idtema=@idTema)",new{idTema},cancellationToken:ct)))return null;
        try
        {
            var id=await connection.QuerySingleAsync<long>(new CommandDefinition("""
                INSERT INTO comercial.tema_video(idtema,titulo,descricao,youtube_video_id,youtube_url,thumbnail_url,ordem,ativo,status,privacy_status,inserido_por)
                VALUES(@idTema,@title,@description,@videoId,@url,@thumbnail,
                       COALESCE((SELECT max(ordem)+1 FROM comercial.tema_video WHERE idtema=@idTema),0),true,'ready',@privacy,@usuario)
                RETURNING idtema_video
                """,new{idTema,input.Title,input.Description,input.VideoId,input.Url,input.Thumbnail,input.Privacy,usuario},cancellationToken:ct));
            logger.LogInformation("Video {IdVideo} associado ao tema {IdTema} por {Usuario}",id,idTema,usuario??"api");
            return await ObterAsync(id,ct);
        }
        catch(PostgresException ex)when(ex.SqlState==PostgresErrorCodes.UniqueViolation){throw new CatalogConflictException("Este video do YouTube ja esta associado ao catalogo.");}
    }

    public async Task<AdminTemaVideoDto?> AtualizarAsync(long idVideo,AtualizarTemaVideoRequest request,string? usuario,CancellationToken ct)
    {
        var input=Normalize(request);
        await using var connection=await db.OpenConnectionAsync(ct);
        try
        {
            var changed=await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE comercial.tema_video SET titulo=@title,descricao=@description,youtube_video_id=@videoId,
                    youtube_url=@url,thumbnail_url=@thumbnail,ordem=@ordem,ativo=@ativo,status='ready',
                    privacy_status=@privacy,mensagem_erro=NULL,alterado_por=@usuario,data_altera=now()
                WHERE idtema_video=@idVideo
                """,new{input.Title,input.Description,input.VideoId,input.Url,input.Thumbnail,input.Privacy,request.Ordem,request.Ativo,usuario,idVideo},cancellationToken:ct));
            if(changed==0)return null;
            logger.LogInformation("Video {IdVideo} atualizado por {Usuario}",idVideo,usuario??"api");
            return await ObterAsync(idVideo,ct);
        }
        catch(PostgresException ex)when(ex.SqlState==PostgresErrorCodes.UniqueViolation){throw new CatalogConflictException("Este video do YouTube ja esta associado ao catalogo.");}
    }

    public async Task<bool> DesativarAsync(long idVideo,string? usuario,CancellationToken ct)
    {await using var connection=await db.OpenConnectionAsync(ct);var changed=await connection.ExecuteAsync(new CommandDefinition("UPDATE comercial.tema_video SET ativo=false,alterado_por=@usuario,data_altera=now() WHERE idtema_video=@idVideo",new{idVideo,usuario},cancellationToken:ct));if(changed>0)logger.LogInformation("Video {IdVideo} removido do tema por {Usuario}; video no YouTube preservado",idVideo,usuario??"api");return changed>0;}

    public async Task<bool> ReordenarAsync(IReadOnlyCollection<ReordenarTemaVideoItem> items,string? usuario,CancellationToken ct)
    {
        if(items.Count==0)return true;if(items.Select(x=>x.IdTemaVideo).Distinct().Count()!=items.Count||items.Any(x=>x.Ordem<0))throw new ArgumentException("Ordenacao invalida.");
        await using var connection=await db.OpenConnectionAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        var ids=items.Select(x=>x.IdTemaVideo).ToArray();var count=await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::int FROM comercial.tema_video WHERE idtema_video=ANY(@ids)",new{ids},transaction,cancellationToken:ct));
        var themes=await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(DISTINCT idtema)::int FROM comercial.tema_video WHERE idtema_video=ANY(@ids)",new{ids},transaction,cancellationToken:ct));
        if(count!=items.Count||themes!=1)return false;
        foreach(var item in items)await connection.ExecuteAsync(new CommandDefinition("UPDATE comercial.tema_video SET ordem=@ordem,alterado_por=@usuario,data_altera=now() WHERE idtema_video=@id",new{id=item.IdTemaVideo,item.Ordem,usuario},transaction,cancellationToken:ct));
        await transaction.CommitAsync(ct);logger.LogInformation("{Quantidade} videos reordenados por {Usuario}",items.Count,usuario??"api");return true;
    }

    private async Task<AdminTemaVideoDto?> ObterAsync(long id,CancellationToken ct){await using var connection=await db.OpenConnectionAsync(ct);return await connection.QuerySingleOrDefaultAsync<AdminTemaVideoDto>(new CommandDefinition(VideoSelect+" WHERE idtema_video=@id",new{id},cancellationToken:ct));}
    internal static string ExtractYoutubeId(string value)
    {
        value=value.Trim();if(YoutubeIdRegex().IsMatch(value))return value;
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https")throw new ArgumentException("Informe uma URL HTTPS valida do YouTube ou o ID do video.");
        var host=uri.Host.ToLowerInvariant();string? id=null;
        if(host is "youtu.be" or "www.youtu.be")id=uri.AbsolutePath.Trim('/').Split('/')[0];
        else if(host is "youtube.com" or "www.youtube.com" or "m.youtube.com")
        {if(uri.AbsolutePath=="/watch")id=System.Web.HttpUtility.ParseQueryString(uri.Query)["v"];else{var parts=uri.AbsolutePath.Trim('/').Split('/');if(parts.Length==2&&parts[0] is "embed" or "shorts" or "live")id=parts[1];}}
        if(id is null||!YoutubeIdRegex().IsMatch(id))throw new ArgumentException("A URL nao contem um ID de video YouTube valido.");return id;
    }
    private static VideoInput Normalize(CriarTemaVideoRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.Titulo))throw new ArgumentException("Informe o titulo.");var id=ExtractYoutubeId(request.YoutubeUrlOuId);
        string? thumbnail=null;if(!string.IsNullOrWhiteSpace(request.ThumbnailUrl)){if(!Uri.TryCreate(request.ThumbnailUrl.Trim(),UriKind.Absolute,out var uri)||uri.Scheme!="https")throw new ArgumentException("A thumbnail deve usar URL HTTPS.");thumbnail=uri.ToString();}
        if(request.PrivacyStatus is not("public" or "unlisted" or "private"))throw new ArgumentException("Privacidade invalida.");
        return new(request.Titulo.Trim(),string.IsNullOrWhiteSpace(request.Descricao)?null:request.Descricao.Trim(),id,$"https://www.youtube.com/watch?v={id}",thumbnail,request.PrivacyStatus);
    }
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$",RegexOptions.CultureInvariant)]private static partial Regex YoutubeIdRegex();
    private sealed record VideoInput(string Title,string? Description,string VideoId,string Url,string? Thumbnail,string Privacy);
    private const string VideoSelect="""
        SELECT idtema_video AS "IdTemaVideo",idtema AS "IdTema",titulo AS "Titulo",descricao AS "Descricao",
               youtube_video_id AS "YoutubeVideoId",youtube_url AS "YoutubeUrl",thumbnail_url AS "ThumbnailUrl",
               ordem AS "Ordem",ativo AS "Ativo",status AS "Status",privacy_status AS "PrivacyStatus",
               mensagem_erro AS "MensagemErro",data_altera AS "DataAltera" FROM comercial.tema_video
        """;
}
