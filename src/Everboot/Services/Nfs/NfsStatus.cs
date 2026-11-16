namespace Everboot.Services.Nfs;

internal sealed class NfsStatus
{
    private long _bytesRead;
    private long _reads;
    public bool Running { get; set; }
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    public long Reads => Interlocked.Read(ref _reads);
    public void RecordRead(int bytes) { Interlocked.Add(ref _bytesRead, bytes); Interlocked.Increment(ref _reads); }
}
