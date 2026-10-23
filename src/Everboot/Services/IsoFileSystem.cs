using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace Everboot.Services;

internal static class IsoFileSystem
{
    // UDF bridge DVDs can expose only a README on the ISO9660 side (Windows 7
    // installation media is one example). Detection and every file service must
    // use the same complete filesystem view.
    internal static DiscFileSystem? Open(Stream stream, IsoLayout layout)
    {
        stream.Position = 0;
        if (layout.HasFlag(IsoLayout.Udf)) return new UdfReader(stream);
        if (layout.HasFlag(IsoLayout.Iso9660)) return new CDReader(stream, joliet: true);
        return null;
    }
}
