using System.Net;
using System.Security.Cryptography;
using Everboot.Configuration;
using Microsoft.Extensions.Options;

namespace Everboot.Web;

internal sealed class ManagementAccess
{
    private readonly bool _isLocal;
    private readonly EverbootOptions _options;

    public ManagementAccess(IHttpContextAccessor accessor, IOptions<EverbootOptions> options)
    {
        var address = accessor.HttpContext?.Connection.RemoteIpAddress;
        _isLocal = address is not null && IPAddress.IsLoopback(address);
        _options = options.Value;
    }

    public bool IsLocal => _isLocal;

    public bool CanChange(string? suppliedKey) =>
        IsLocal || !string.IsNullOrEmpty(_options.Http.ManagementKey) &&
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_options.Http.ManagementKey)),
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(suppliedKey ?? "")));
}
