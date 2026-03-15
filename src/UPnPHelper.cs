using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace FFXIVTv;

/// <summary>
/// UPnP IGD (Internet Gateway Device) helper.
/// Discovers the router via SSDP multicast, then adds/removes port mappings via SOAP.
/// Also fetches the public IP via ipify for display in the host UI.
/// </summary>
public static class UPnPHelper
{
    // Shared long-lived client for device description + SOAP calls.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public sealed record GatewayInfo(string ControlUrl, string ServiceType);

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends SSDP M-SEARCH multicast and returns the first usable IGD gateway found,
    /// or null if no UPnP-capable router is reachable within the timeout.
    ///
    /// All search targets are blasted out at once (each twice for UDP reliability)
    /// then we collect every unique LOCATION in a single 4-second window.
    /// This is faster and catches routers that only respond to ssdp:all or IGD:2.
    /// </summary>
    public static async Task<GatewayInfo?> DiscoverAsync(CancellationToken ct = default)
    {
        const string multicastAddr = "239.255.255.250";
        const int    multicastPort = 1900;

        // Cast a wide net: include IGD:2, WANIPConnection:2, WANPPPConnection (PPPoE),
        // and ssdp:all as a catch-all for routers that ignore specific service types.
        string[] searchTargets = {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANIPConnection:2",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
            "ssdp:all",
        };

        var endpoint     = new IPEndPoint(IPAddress.Parse(multicastAddr), multicastPort);
        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 4500;

            // Blast all search targets (sent twice each for UDP packet-loss resilience).
            foreach (var st in searchTargets)
            {
                if (ct.IsCancellationRequested) return null;
                byte[] data = Encoding.UTF8.GetBytes(
                    "M-SEARCH * HTTP/1.1\r\n"                        +
                    $"HOST: {multicastAddr}:{multicastPort}\r\n"     +
                    "MAN: \"ssdp:discover\"\r\n"                     +
                    "MX: 3\r\n"                                      +
                    $"ST: {st}\r\n\r\n");
                udp.Send(data, data.Length, endpoint);
                udp.Send(data, data.Length, endpoint);
            }

            // Collect all LOCATION responses in one 4-second window.
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline)
            {
                var    from = new IPEndPoint(IPAddress.Any, 0);
                byte[] resp;
                try { resp = udp.Receive(ref from); }
                catch (SocketException) { break; }

                string text     = Encoding.UTF8.GetString(resp);
                string? location = null;
                foreach (var line in text.Split('\n'))
                {
                    if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                    {
                        location = line.Substring(9).Trim().TrimEnd('\r');
                        break;
                    }
                }

                if (location == null || !seenLocations.Add(location)) continue;
                var gateway = await ParseDescriptionAsync(location, ct);
                if (gateway != null) return gateway;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch { }

        return null;
    }

    private static async Task<GatewayInfo?> ParseDescriptionAsync(string location, CancellationToken ct)
    {
        try
        {
            string xml = await Http.GetStringAsync(location, ct);
            var doc = XDocument.Parse(xml);
            XNamespace ns = "urn:schemas-upnp-org:device-1-0";

            // Accept WANIPConnection v1/v2 and WANPPPConnection (PPPoE routers).
            string[] serviceTypes = {
                "urn:schemas-upnp-org:service:WANIPConnection:1",
                "urn:schemas-upnp-org:service:WANIPConnection:2",
                "urn:schemas-upnp-org:service:WANPPPConnection:1",
            };

            foreach (var service in doc.Descendants(ns + "service"))
            {
                string? serviceType = service.Element(ns + "serviceType")?.Value;
                string? controlUrl  = service.Element(ns + "controlURL")?.Value;

                if (serviceType == null || controlUrl == null) continue;
                if (!serviceTypes.Any(t => serviceType.Equals(t, StringComparison.OrdinalIgnoreCase))) continue;

                var uri     = new Uri(location);
                string full = controlUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? controlUrl
                    : $"{uri.Scheme}://{uri.Host}:{uri.Port}{controlUrl}";

                return new GatewayInfo(full, serviceType);
            }
        }
        catch { }
        return null;
    }

    // ── Port mapping ──────────────────────────────────────────────────────────

    public static async Task<bool> AddPortMappingAsync(
        GatewayInfo gateway, int externalPort, int internalPort, string internalIp,
        CancellationToken ct = default)
    {
        string soap = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
  <s:Body>
    <u:AddPortMapping xmlns:u=""{gateway.ServiceType}"">
      <NewRemoteHost></NewRemoteHost>
      <NewExternalPort>{externalPort}</NewExternalPort>
      <NewProtocol>TCP</NewProtocol>
      <NewInternalPort>{internalPort}</NewInternalPort>
      <NewInternalClient>{internalIp}</NewInternalClient>
      <NewEnabled>1</NewEnabled>
      <NewPortMappingDescription>FFXIV-TV Sync</NewPortMappingDescription>
      <NewLeaseDuration>0</NewLeaseDuration>
    </u:AddPortMapping>
  </s:Body>
</s:Envelope>";

        return await SoapAsync(gateway, "AddPortMapping", soap, ct);
    }

    public static async Task<bool> DeletePortMappingAsync(
        GatewayInfo gateway, int externalPort, CancellationToken ct = default)
    {
        string soap = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
  <s:Body>
    <u:DeletePortMapping xmlns:u=""{gateway.ServiceType}"">
      <NewRemoteHost></NewRemoteHost>
      <NewExternalPort>{externalPort}</NewExternalPort>
      <NewProtocol>TCP</NewProtocol>
    </u:DeletePortMapping>
  </s:Body>
</s:Envelope>";

        return await SoapAsync(gateway, "DeletePortMapping", soap, ct);
    }

    private static async Task<bool> SoapAsync(GatewayInfo gateway, string action, string soap, CancellationToken ct)
    {
        try
        {
            var content = new StringContent(soap, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPAction", $"\"{gateway.ServiceType}#{action}\"");
            var resp = await Http.PostAsync(gateway.ControlUrl, content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns the first non-loopback IPv4 address on an active interface.</summary>
    public static string? GetLocalIp()
    {
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Fetches the machine's public IP via ipify. Returns null on failure.</summary>
    public static async Task<string?> GetPublicIpAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return (await client.GetStringAsync("https://api.ipify.org", ct)).Trim();
        }
        catch { return null; }
    }
}
