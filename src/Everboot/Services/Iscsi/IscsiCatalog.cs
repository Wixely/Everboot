using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Everboot.Configuration;
using Microsoft.Extensions.Options;

namespace Everboot.Services.Iscsi;

/// <summary>
/// View over the <see cref="BootCatalog"/> through an iSCSI lens: each image
/// becomes a target with a stable IQN derived from its slug. Resolved live so
/// adding/removing images doesn't need a service restart.
/// </summary>
internal sealed class IscsiCatalog
{
    private readonly BootCatalog _bootCatalog;
    private readonly IscsiOptions _options;

    public IscsiCatalog(BootCatalog bootCatalog, IOptions<EverbootOptions> options)
    {
        _bootCatalog = bootCatalog;
        _options = options.Value.Iscsi;
    }

    public IReadOnlyList<IscsiTarget> AllTargets =>
        _bootCatalog.Entries
            .Where(e => File.Exists(e.FullPath))
            .Select(BuildTarget)
            .ToList();

    public IscsiTarget? Find(string iqn)
    {
        foreach (var entry in _bootCatalog.Entries)
        {
            var candidate = BuildIqn(entry.Slug);
            if (string.Equals(candidate, iqn, StringComparison.OrdinalIgnoreCase))
            {
                return BuildTarget(entry);
            }
        }
        return null;
    }

    public string BuildIqn(string slug) => $"{_options.IqnBase}:iso-{slug}";

    private IscsiTarget BuildTarget(IsoEntry entry)
    {
        var iqn = BuildIqn(entry.Slug);
        var size = new FileInfo(entry.FullPath).Length;
        var isImg = string.Equals(Path.GetExtension(entry.FileName), ".img", StringComparison.OrdinalIgnoreCase);
        return isImg
            ? IscsiTarget.ForDisk(iqn, entry.FullPath, size, entry.DisplayName)
            : IscsiTarget.ForIso(iqn, entry.FullPath, size, entry.DisplayName);
    }
}
