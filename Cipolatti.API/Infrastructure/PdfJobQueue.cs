using System.Threading.Channels;

namespace Cipolatti.API.Infrastructure;

public sealed record PdfProcessingJob(long IdPdf, bool Replacement = false, string? StagedOriginal = null, string? Version = null);

public interface IPdfJobQueue
{
    ValueTask EnqueueAsync(PdfProcessingJob job, CancellationToken ct);
    IAsyncEnumerable<PdfProcessingJob> ReadAllAsync(CancellationToken ct);
}

public sealed class PdfJobQueue : IPdfJobQueue
{
    private readonly Channel<PdfProcessingJob> channel = Channel.CreateUnbounded<PdfProcessingJob>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(PdfProcessingJob job, CancellationToken ct) => channel.Writer.WriteAsync(job, ct);
    public IAsyncEnumerable<PdfProcessingJob> ReadAllAsync(CancellationToken ct) => channel.Reader.ReadAllAsync(ct);
}
