using Sgs.Collector.Common;
using Sgs.Collector.Data;

namespace Sgs.Collector.Storage;

public sealed class BlobService
{
    private readonly BlobRepository _blobs;
    private readonly IBlobStore _store;

    public BlobService(BlobRepository blobs, IBlobStore store)
    {
        _blobs = blobs;
        _store = store;
    }

    public async Task EnsureUploadedAsync(string hash, byte[] content, string contentType, CancellationToken ct)
    {
        if (await _blobs.ExistsAsync(hash))
        {
            return;
        }

        await _store.PutAsync(hash, content, contentType, ct);
        await _blobs.MarkUploadedAsync(hash, content.LongLength, Clock.NowIso());
    }
}
