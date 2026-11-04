using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace PlcKi.Server.Mobile;

/// <summary>
/// For the QR labels (Setup › QR labels): the addresses a phone on the plant network can use to reach this PC,
/// and whether PLC-Ki listens to other computers at all. Admins only (the path is under /api/admin). Reads only.
/// </summary>
public static class PhoneEndpoints
{
    public static WebApplication MapPhone(this WebApplication app)
    {
        app.MapGet("/api/admin/phone-address", (IServer server) =>
        {
            var urls = server.Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? new List<string>();
            var port = urls.Select(PortOf).FirstOrDefault(p => p != null) ?? 5080;
            // "http://localhost:5080" only answers this PC; "*", "+", "0.0.0.0", "[::]" or a LAN address answer phones too
            bool remote = urls.Any(u => !Uri.TryCreate(u.Replace("://*", "://any").Replace("://+", "://any"), UriKind.Absolute, out var x)
                || x.Host is "any" or "0.0.0.0" or "[::]" || (IPAddress.TryParse(x.Host, out var ip) && !IPAddress.IsLoopback(ip)));
            return Results.Ok(new
            {
                port,
                listensToOthers = remote,
                hostName = Dns.GetHostName(),
                addresses = LanAddresses(),
            });
        });
        return app;
    }

    private static int? PortOf(string url) =>
        Uri.TryCreate(url.Replace("://*", "://any").Replace("://+", "://any"), UriKind.Absolute, out var u) ? u.Port : null;

    /// <summary>IPv4 addresses of the network cards that are up, most likely plant network first (wired, then Wi-Fi, virtual last).</summary>
    private static List<object> LanAddresses()
    {
        var list = new List<(int Rank, string Ip, string Card)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var name = nic.Name + (string.IsNullOrEmpty(nic.Description) || nic.Description == nic.Name ? "" : " (" + nic.Description + ")");
                bool virt = (nic.Name + " " + nic.Description).Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
                    || (nic.Name + " " + nic.Description).Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    || nic.Name.StartsWith("docker", StringComparison.OrdinalIgnoreCase) || nic.Name.StartsWith("veth", StringComparison.Ordinal);
                int rank = virt ? 2 : nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a.Address)) continue;
                    var ip = a.Address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;   // no DHCP answer: nobody can reach it
                    list.Add((rank, ip, name));
                }
            }
        }
        catch (NetworkInformationException) { /* no list on this system: the page falls back to the address in the browser */ }
        return list.OrderBy(x => x.Rank).Select(x => (object)new { ip = x.Ip, card = x.Card, @virtual = x.Rank == 2 }).ToList();
    }
}
