using System.Net.Http.Headers;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models.Catalogo;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Cipolatti.API.Services;

public sealed class YoutubeService(IDbConnectionFactory db,IHttpClientFactory clients,IDataProtectionProvider protection,
    IOptions<YoutubeOptions> settings,ILogger<YoutubeService> logger):IYoutubeService
{
    private readonly YoutubeOptions options=settings.Value;
    private readonly IDataProtector tokenProtector=protection.CreateProtector("CatalogoVirtual.YouTube.RefreshToken.v1");
    private readonly IDataProtector sessionProtector=protection.CreateProtector("CatalogoVirtual.YouTube.UploadSession.v1");
    private readonly IDataProtector stateProtector=protection.CreateProtector("CatalogoVirtual.YouTube.OAuthState.v1");

    public async Task<YoutubeStatusDto> GetStatusAsync(CancellationToken ct)
    {
        if(!Configured)return new(false,false,null,null,null,null);
        await using var connection=await db.OpenConnectionAsync(ct);
        var row=await connection.QuerySingleOrDefaultAsync<StatusRow>(new CommandDefinition("SELECT canal_id AS CanalId,canal_nome AS CanalNome,conta_email AS ContaEmail,data_autorizacao AS DataAutorizacao FROM comercial.youtube_integracao WHERE ativo=true",cancellationToken:ct));
        return row is null?new(true,false,null,null,null,null):new(true,true,row.CanalId,row.CanalNome,row.ContaEmail,new DateTimeOffset(DateTime.SpecifyKind(row.DataAutorizacao,DateTimeKind.Utc)));
    }

    public string CreateAuthorizationUrl(string? usuario)
    {
        EnsureConfigured();var state=stateProtector.Protect(JsonSerializer.Serialize(new OAuthState(usuario,DateTimeOffset.UtcNow.AddMinutes(10))));
        var query=new Dictionary<string,string?>{{"client_id",options.ClientId},{"redirect_uri",options.RedirectUri},{"response_type","code"},{"scope","https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly"},{"access_type","offline"},{"include_granted_scopes","true"},{"prompt","consent"},{"state",state}};
        return "https://accounts.google.com/o/oauth2/v2/auth?"+string.Join('&',query.Select(x=>$"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));
    }

    public async Task<string> CompleteAuthorizationAsync(string code,string state,CancellationToken ct)
    {
        EnsureConfigured();OAuthState stateData;
        try{stateData=JsonSerializer.Deserialize<OAuthState>(stateProtector.Unprotect(state))??throw new InvalidDataException();}catch(Exception ex)when(ex is not OperationCanceledException){throw new ArgumentException("State OAuth invalido ou expirado.");}
        if(stateData.ExpiresAt<DateTimeOffset.UtcNow)throw new ArgumentException("State OAuth expirado.");
        var client=clients.CreateClient("youtube");
        using var tokenResponse=await client.PostAsync("https://oauth2.googleapis.com/token",new FormUrlEncodedContent(new Dictionary<string,string>{{"code",code},{"client_id",options.ClientId},{"client_secret",options.ClientSecret},{"redirect_uri",options.RedirectUri},{"grant_type","authorization_code"}}),ct);
        var tokenJson=await tokenResponse.Content.ReadAsStringAsync(ct);if(!tokenResponse.IsSuccessStatusCode)throw new InvalidOperationException("O Google recusou a troca do codigo OAuth.");
        var token=JsonSerializer.Deserialize<TokenResponse>(tokenJson,JsonOptions)??throw new InvalidOperationException("Resposta OAuth invalida.");
        if(string.IsNullOrWhiteSpace(token.AccessToken)||string.IsNullOrWhiteSpace(token.RefreshToken))throw new InvalidOperationException("O Google nao retornou refresh token; use Reconectar para conceder acesso offline.");
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token.AccessToken);
        using var channelResponse=await client.SendAsync(request,ct);channelResponse.EnsureSuccessStatusCode();var channels=JsonSerializer.Deserialize<ChannelResponse>(await channelResponse.Content.ReadAsStringAsync(ct),JsonOptions);
        var channel=channels?.Items?.FirstOrDefault()??throw new InvalidOperationException("A conta autorizada nao possui canal YouTube.");
        await using var connection=await db.OpenConnectionAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE comercial.youtube_integracao SET ativo=false,alterado_por=@usuario,data_atualizacao=now() WHERE ativo=true",new{usuario=stateData.Usuario},transaction,cancellationToken:ct));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO comercial.youtube_integracao(canal_id,canal_nome,refresh_token_protegido,ativo,data_autorizacao,data_atualizacao,autorizado_por,alterado_por)
            VALUES(@id,@name,@token,true,now(),now(),@usuario,@usuario) ON CONFLICT(canal_id) DO UPDATE SET canal_nome=EXCLUDED.canal_nome,
              refresh_token_protegido=EXCLUDED.refresh_token_protegido,ativo=true,data_autorizacao=now(),data_atualizacao=now(),alterado_por=@usuario
            """,new{id=channel.Id,name=channel.Snippet?.Title??channel.Id,token=tokenProtector.Protect(token.RefreshToken),usuario=stateData.Usuario},transaction,cancellationToken:ct));
        await transaction.CommitAsync(ct);logger.LogInformation("Canal YouTube {CanalId} autorizado por {Usuario}",channel.Id,stateData.Usuario??"api");return options.FrontendReturnUrl;
    }

    public async Task<AdminTemaVideoDto> UploadVideoAsync(int idTema,YoutubeUploadRequest request,Stream video,string contentType,long length,Stream? thumbnail,string? thumbnailContentType,string? usuario,CancellationToken ct)
    {
        EnsureConfigured();
        if(length<=0||length>150L*1024*1024)throw new ArgumentException("O vídeo deve ter entre 1 byte e 150 MB.");
        if(string.IsNullOrWhiteSpace(contentType)||!contentType.StartsWith("video/",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Envie um arquivo de vídeo válido.");
        if(request.PrivacyStatus is not("public" or "unlisted" or "private"))throw new ArgumentException("Privacidade inválida.");
        var title=request.Titulo.Trim();if(title.Length==0)throw new ArgumentException("Informe o título do vídeo.");
        var tempVideo=Path.Combine(Path.GetTempPath(),$"catalogo-youtube-{Guid.NewGuid():N}.upload");
        var tempThumbnail=thumbnail is null?null:Path.Combine(Path.GetTempPath(),$"catalogo-youtube-{Guid.NewGuid():N}.thumbnail");
        long idVideo=0;long idUpload=0;
        try
        {
            await CopyWithLimitAsync(video,tempVideo,length,ct);
            if(thumbnail is not null){if(thumbnailContentType is not("image/jpeg" or "image/png"))throw new ArgumentException("A thumbnail deve ser JPEG ou PNG.");await CopyWithLimitAsync(thumbnail,tempThumbnail!,5L*1024*1024,ct);}
            await using var connection=await db.OpenConnectionAsync(ct);
            if(!await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM comercial.temas WHERE idtema=@idTema)",new{idTema},cancellationToken:ct)))throw new KeyNotFoundException("Tema não encontrado.");
            idVideo=await connection.QuerySingleAsync<long>(new CommandDefinition("""
                INSERT INTO comercial.tema_video(idtema,titulo,descricao,ordem,ativo,status,privacy_status,inserido_por)
                VALUES(@idTema,@title,@description,COALESCE((SELECT max(ordem)+1 FROM comercial.tema_video WHERE idtema=@idTema),0),true,'uploading',@privacy,@usuario)
                RETURNING idtema_video
                """,new{idTema,title,description=NullIfBlank(request.Descricao),privacy=request.PrivacyStatus,usuario},cancellationToken:ct));
            idUpload=await connection.QuerySingleAsync<long>(new CommandDefinition("""
                INSERT INTO comercial.youtube_upload(idtema_video,arquivo_local,content_type,tamanho_bytes,status,inserido_por,alterado_por)
                VALUES(@idVideo,@path,@contentType,@length,'pending',@usuario,@usuario) RETURNING idyoutube_upload
                """,new{idVideo,path=tempVideo,contentType,length,usuario},cancellationToken:ct));
            await connection.ExecuteAsync(new CommandDefinition("UPDATE comercial.youtube_upload SET status='uploading',data_altera=now() WHERE idyoutube_upload=@idUpload",new{idUpload},cancellationToken:ct));
            var accessToken=await GetAccessTokenAsync(connection,ct);var client=clients.CreateClient("youtube");client.Timeout=Timeout.InfiniteTimeSpan;
            var sessionUri=await StartResumableUploadAsync(client,accessToken,title,request.Descricao,request.PrivacyStatus,contentType,length,ct);
            await connection.ExecuteAsync(new CommandDefinition("UPDATE comercial.youtube_upload SET session_uri_protegida=@session,status='uploading',data_altera=now() WHERE idyoutube_upload=@idUpload",new{session=sessionProtector.Protect(sessionUri),idUpload},cancellationToken:ct));
            var youtubeId=await SendResumableUploadAsync(client,accessToken,sessionUri,tempVideo,length,idUpload,connection,ct);
            var thumbnailUrl=$"https://i.ytimg.com/vi/{youtubeId}/hqdefault.jpg";if(tempThumbnail is not null)await UploadThumbnailAsync(client,accessToken,youtubeId,tempThumbnail,thumbnailContentType!,ct);
            var processed=await WaitForProcessingAsync(client,accessToken,youtubeId,ct);
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE comercial.tema_video SET youtube_video_id=@youtubeId,youtube_url=@url,thumbnail_url=@thumbnail,status=@videoStatus,privacy_status=@privacy,alterado_por=@usuario,data_altera=now() WHERE idtema_video=@idVideo;
                UPDATE comercial.youtube_upload SET bytes_enviados=tamanho_bytes,status='ready',mensagem_erro=NULL,alterado_por=@usuario,data_altera=now() WHERE idyoutube_upload=@idUpload;
                """,new{idVideo,youtubeId,url=$"https://www.youtube.com/watch?v={youtubeId}",thumbnail=thumbnailUrl,videoStatus=processed?"ready":"processing",privacy=request.PrivacyStatus,usuario,idUpload},cancellationToken:ct));
            return await connection.QuerySingleAsync<AdminTemaVideoDto>(new CommandDefinition(VideoSelect+" WHERE idtema_video=@idVideo",new{idVideo},cancellationToken:ct));
        }
        catch(Exception ex)when(ex is not OperationCanceledException)
        {
            if(idUpload>0||idVideo>0)try{await using var errorConnection=await db.OpenConnectionAsync(CancellationToken.None);await using var cleanup=await errorConnection.BeginTransactionAsync(CancellationToken.None);if(idUpload>0)await errorConnection.ExecuteAsync(new CommandDefinition("DELETE FROM comercial.youtube_upload WHERE idyoutube_upload=@idUpload",new{idUpload},cleanup,cancellationToken:CancellationToken.None));if(idVideo>0)await errorConnection.ExecuteAsync(new CommandDefinition("DELETE FROM comercial.tema_video WHERE idtema_video=@idVideo",new{idVideo},cleanup,cancellationToken:CancellationToken.None));await cleanup.CommitAsync(CancellationToken.None);logger.LogWarning("Upload YouTube {IdUpload} falhou; registros temporários removidos da base",idUpload);}catch(Exception logEx){logger.LogError(logEx,"Falha ao remover registros temporários do upload YouTube {IdUpload}",idUpload);}
            throw;
        }
        finally{TryDelete(tempVideo);if(tempThumbnail is not null)TryDelete(tempThumbnail);}
    }

    private async Task<string> GetAccessTokenAsync(System.Data.Common.DbConnection connection,CancellationToken ct)
    {
        var protectedToken=await connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT refresh_token_protegido FROM comercial.youtube_integracao WHERE ativo=true",cancellationToken:ct));
        if(string.IsNullOrWhiteSpace(protectedToken))throw new InvalidOperationException("Conecte o canal YouTube antes de publicar.");
        string refresh;try{refresh=tokenProtector.Unprotect(protectedToken);}catch{throw new InvalidOperationException("A autorização YouTube não pôde ser recuperada. Reconecte o canal.");}
        var client=clients.CreateClient("youtube");using var response=await client.PostAsync("https://oauth2.googleapis.com/token",new FormUrlEncodedContent(new Dictionary<string,string>{{"client_id",options.ClientId},{"client_secret",options.ClientSecret},{"refresh_token",refresh},{"grant_type","refresh_token"}}),ct);
        var json=await response.Content.ReadAsStringAsync(ct);if(!response.IsSuccessStatusCode)throw new InvalidOperationException("O token YouTube expirou ou foi revogado. Reconecte o canal.");
        var token=JsonSerializer.Deserialize<TokenResponse>(json,JsonOptions);return token?.AccessToken??throw new InvalidOperationException("O Google não retornou um access token.");
    }

    private static async Task<string> StartResumableUploadAsync(HttpClient client,string token,string title,string? description,string privacy,string contentType,long length,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,"https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);request.Headers.Add("X-Upload-Content-Length",length.ToString(CultureInfo.InvariantCulture));request.Headers.Add("X-Upload-Content-Type",contentType);request.Content=JsonContent.Create(new{snippet=new{title,description=description??string.Empty},status=new{privacyStatus=privacy}});
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);var responseBody=await response.Content.ReadAsStringAsync(ct);if(!response.IsSuccessStatusCode||response.Headers.Location is null){var detail=responseBody.Length>600?responseBody[..600]:responseBody;throw new InvalidOperationException($"O YouTube não iniciou a sessão de upload (HTTP {(int)response.StatusCode}). {detail}");}return response.Headers.Location.ToString();
    }

    private static async Task<string> SendResumableUploadAsync(HttpClient client,string token,string session,string path,long length,long idUpload,System.Data.Common.DbConnection db,CancellationToken ct)
    {
        const int chunkSize=8*1024*1024;long offset=0;await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,chunkSize,true);var buffer=new byte[chunkSize];
        while(offset<length){var count=await file.ReadAsync(buffer.AsMemory(0,(int)Math.Min(chunkSize,length-offset)),ct);if(count==0)throw new InvalidDataException("Arquivo de vídeo incompleto.");var from=offset;var to=offset+count-1;var attempts=0;
            while(true){using var request=new HttpRequestMessage(HttpMethod.Put,session);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);var body=new ByteArrayContent(buffer,0,count);body.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");body.Headers.ContentRange=new ContentRangeHeaderValue(from,to,length);request.Content=body;try{using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);if(response.StatusCode==HttpStatusCode.PermanentRedirect){offset=ParseUploadedOffset(response,from);file.Position=offset;break;}if(!response.IsSuccessStatusCode)throw new InvalidOperationException("O YouTube rejeitou um bloco do vídeo.");var payload=JsonSerializer.Deserialize<YoutubeVideoResponse>(await response.Content.ReadAsStringAsync(ct),JsonOptions);return payload?.Id??throw new InvalidOperationException("O YouTube não retornou o ID do vídeo.");}catch(HttpRequestException)when(++attempts<3){await Task.Delay(TimeSpan.FromSeconds(attempts),ct);file.Position=from;}}
            await db.ExecuteAsync(new CommandDefinition("UPDATE comercial.youtube_upload SET bytes_enviados=@offset,data_altera=now() WHERE idyoutube_upload=@idUpload",new{offset,idUpload},cancellationToken:ct));}
        throw new InvalidOperationException("Upload YouTube não concluído.");
    }

    private static long ParseUploadedOffset(HttpResponseMessage response,long fallback){if(response.Headers.TryGetValues("Range",out var values)){var value=values.FirstOrDefault();if(value is not null&&value.Contains('-')&&long.TryParse(value[(value.LastIndexOf('-')+1)..],out var end))return end+1;}return fallback;}
    private static async Task UploadThumbnailAsync(HttpClient client,string token,string videoId,string path,string contentType,CancellationToken ct){using var request=new HttpRequestMessage(HttpMethod.Post,$"https://www.googleapis.com/upload/youtube/v3/thumbnails/set?videoId={Uri.EscapeDataString(videoId)}");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);await using var file=File.OpenRead(path);var content=new StreamContent(file);content.Headers.ContentType=new MediaTypeHeaderValue(contentType);request.Content=content;using var response=await client.SendAsync(request,ct);if(!response.IsSuccessStatusCode)throw new InvalidOperationException("O YouTube não aceitou a thumbnail.");}
    private static async Task<bool> WaitForProcessingAsync(HttpClient client,string token,string videoId,CancellationToken ct){for(var i=0;i<20;i++){using var request=new HttpRequestMessage(HttpMethod.Get,$"https://www.googleapis.com/youtube/v3/videos?part=status&id={Uri.EscapeDataString(videoId)}");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);using var response=await client.SendAsync(request,ct);if(response.IsSuccessStatusCode){var result=JsonSerializer.Deserialize<YoutubeProcessingResponse>(await response.Content.ReadAsStringAsync(ct),JsonOptions);var status=result?.Items?.FirstOrDefault()?.Status?.UploadStatus;if(status=="processed")return true;if(status is "failed" or "rejected")throw new InvalidOperationException("O YouTube informou falha no processamento do vídeo.");}await Task.Delay(TimeSpan.FromSeconds(3),ct);}return false;}
    private static async Task CopyWithLimitAsync(Stream source,string path,long limit,CancellationToken ct){await using var target=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true);var buffer=new byte[81920];long total=0;int read;while((read=await source.ReadAsync(buffer,ct))>0){total+=read;if(total>limit)throw new InvalidDataException("Arquivo excede o limite permitido.");await target.WriteAsync(buffer.AsMemory(0,read),ct);}}
    private static string? NullIfBlank(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    private static void TryDelete(string path){try{if(File.Exists(path))File.Delete(path);}catch{} }

    private const string VideoSelect="""
        SELECT idtema_video AS "IdTemaVideo",idtema AS "IdTema",titulo AS "Titulo",descricao AS "Descricao",
               youtube_video_id AS "YoutubeVideoId",youtube_url AS "YoutubeUrl",thumbnail_url AS "ThumbnailUrl",
               ordem AS "Ordem",ativo AS "Ativo",status AS "Status",privacy_status AS "PrivacyStatus",
               mensagem_erro AS "MensagemErro",data_altera AS "DataAltera" FROM comercial.tema_video
        """;
    private bool Configured=>!string.IsNullOrWhiteSpace(options.ClientId)&&!string.IsNullOrWhiteSpace(options.ClientSecret)&&Uri.TryCreate(options.RedirectUri,UriKind.Absolute,out _);
    private void EnsureConfigured(){if(!Configured)throw new InvalidOperationException("Configure Youtube__ClientId, Youtube__ClientSecret e Youtube__RedirectUri.");}
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true,PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower};
    private sealed record OAuthState(string? Usuario,DateTimeOffset ExpiresAt);
    private sealed record StatusRow(string CanalId,string CanalNome,string? ContaEmail,DateTime DataAutorizacao);
    private sealed record TokenResponse(string AccessToken,string RefreshToken);
    private sealed record YoutubeVideoResponse(string Id);
    private sealed record YoutubeProcessingResponse(YoutubeProcessingItem[]? Items);
    private sealed record YoutubeProcessingItem(YoutubeProcessingStatus? Status);
    private sealed record YoutubeProcessingStatus(string? UploadStatus);
    private sealed record ChannelResponse(ChannelItem[]? Items);
    private sealed record ChannelItem(string Id,ChannelSnippet? Snippet);
    private sealed record ChannelSnippet(string? Title);
}
