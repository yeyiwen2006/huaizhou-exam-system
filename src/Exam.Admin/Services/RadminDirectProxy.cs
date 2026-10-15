using System.Net;
using System.Net.Sockets;

namespace Exam.Admin.Services;

// A system web proxy cannot route the Radmin adapter's virtual LAN.
internal sealed class RadminDirectProxy(IWebProxy upstream) : IWebProxy
{
    public ICredentials? Credentials { get => upstream.Credentials; set => upstream.Credentials = value; }
    public bool IsBypassed(Uri host) => IsRadmin(host) || upstream.IsBypassed(host);
    public Uri? GetProxy(Uri destination) => IsRadmin(destination) ? destination : upstream.GetProxy(destination);
    private static bool IsRadmin(Uri address) => IPAddress.TryParse(address.Host, out var ip) &&
        ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] == 26;
}
