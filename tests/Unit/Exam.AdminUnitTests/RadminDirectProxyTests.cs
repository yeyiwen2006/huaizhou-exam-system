using System.Net;
using Exam.Admin.Services;

namespace Exam.AdminUnitTests;

public sealed class RadminDirectProxyTests
{
    [Fact]
    public void VpnAddressBypassesWebProxyWhilePublicHttpsKeepsSystemProxy()
    {
        var proxyAddress = new Uri("http://127.0.0.1:7890");
        var proxy = new RadminDirectProxy(new WebProxy(proxyAddress));
        var vpn = new Uri("https://26.1.1.1/");
        var internet = new Uri("https://primary.example.com/");
        Assert.True(proxy.IsBypassed(vpn));
        Assert.Equal(vpn, proxy.GetProxy(vpn));
        Assert.False(proxy.IsBypassed(internet));
        Assert.Equal(proxyAddress, proxy.GetProxy(internet));
    }
}
