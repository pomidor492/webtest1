using System.Net;
using System.Net.Sockets;
using System.Text;



// 1. Get public endpoint via STUN (this also creates a local UDP socket on a random port)
var publicEndpoint = StunClient.GetPublicEndpoint();
Console.WriteLine($"My public endpoint: {publicEndpoint}");
Console.WriteLine($"Enter peer endpoint");

/*
// 2. Register with signaling server
using var http = new HttpClient();
string signalingUrl = "http://your-signaling-server:5000";
await http.PostAsync($"{signalingUrl}/register",
    new StringContent(publicEndpoint.ToString()));

// 3. Wait for peer
string? peerEndpointString = null;
while (peerEndpointString == null)
{
    try
    {
        var response = await http.GetAsync($"{signalingUrl}/peer");
        if (response.IsSuccessStatusCode)
            peerEndpointString = await response.Content.ReadAsStringAsync();
    }
    catch { }
    await Task.Delay(2000);
}
var peerEndpoint = IPEndPoint.Parse(peerEndpointString);
Console.WriteLine($"Peer endpoint: {peerEndpoint}");
*/
string peerEndpointString = Console.ReadLine();
var peerEndpoint = IPEndPoint.Parse(peerEndpointString);
Console.WriteLine($"Peer endpoint: {peerEndpoint}");
// 4. Start messaging (reuse the local port from STUN)
var messenger = new P2PMessenger(publicEndpoint.Port, peerEndpoint);
await messenger.StartAsync();

public class StunClient
{
    public static IPEndPoint GetPublicEndpoint(string stunServer = "stun.l.google.com", int port = 19302, int timeoutMs = 3000)
    {
        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = timeoutMs;
        udp.Connect(stunServer, port);

        // STUN binding request header (20 bytes)
        byte[] request = new byte[20];

        // Message Type: Binding request = 0x0001
        request[0] = 0x00;
        request[1] = 0x01;

        // Message Length: 0 (no attributes)
        request[2] = 0x00;
        request[3] = 0x00;

        // Magic Cookie (RFC 5389): 0x2112A442
        request[4] = 0x21;
        request[5] = 0x12;
        request[6] = 0xA4;
        request[7] = 0x42;

        // Transaction ID (12 bytes) – must be random
        new Random().NextBytes(request.AsSpan(8, 12));

        // Send request
        udp.Send(request, request.Length);

        // Receive response
        var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = udp.Receive(ref remoteEndPoint);

        return ParseStunResponse(response);
    }

    private static IPEndPoint ParseStunResponse(byte[] response)
    {
        // Minimum size: 20 byte header + at least one attribute (4+ bytes)
        if (response.Length < 24)
            throw new Exception("Response too short");

        // Verify magic cookie (optional but recommended)
        if (response[4] != 0x21 || response[5] != 0x12 || response[6] != 0xA4 || response[7] != 0x42)
            throw new Exception("Invalid magic cookie");

        // Message length is in bytes 2-3 (network order)
        int messageLength = (response[2] << 8) | response[3];
        if (response.Length < 20 + messageLength)
            throw new Exception("Incomplete response");

        int offset = 20; // start of attributes
        while (offset + 4 <= response.Length)
        {
            ushort attrType = (ushort)((response[offset] << 8) | response[offset + 1]);
            ushort attrLength = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
            int valueOffset = offset + 4;

            // Check if attribute fits in the response
            if (valueOffset + attrLength > response.Length)
                break;

            if (attrType == 0x0001 || attrType == 0x0020) // MAPPED-ADDRESS or XOR-MAPPED-ADDRESS
            {
                // The first byte of the value is reserved (0)
                // Second byte is address family (0x01 = IPv4)
                if (response[valueOffset + 1] != 0x01)
                    throw new Exception("Only IPv4 is supported");

                int port;
                IPAddress ip;

                if (attrType == 0x0020) // XOR-MAPPED-ADDRESS
                {
                    // Port is XOR'd with the magic cookie (first two bytes of magic cookie)
                    int portXor = (response[valueOffset + 2] << 8) | response[valueOffset + 3];
                    port = portXor ^ 0x2112;

                    // IP is XOR'd with the magic cookie (4 bytes)
                    byte[] ipBytes = new byte[4];
                    ipBytes[0] = (byte)(response[valueOffset + 4] ^ 0x21);
                    ipBytes[1] = (byte)(response[valueOffset + 5] ^ 0x12);
                    ipBytes[2] = (byte)(response[valueOffset + 6] ^ 0xA4);
                    ipBytes[3] = (byte)(response[valueOffset + 7] ^ 0x42);
                    ip = new IPAddress(ipBytes);
                }
                else // MAPPED-ADDRESS (non-XOR)
                {
                    port = (response[valueOffset + 2] << 8) | response[valueOffset + 3];
                    byte[] ipBytes = new byte[4];
                    Array.Copy(response, valueOffset + 4, ipBytes, 0, 4);
                    ip = new IPAddress(ipBytes);
                }

                return new IPEndPoint(ip, port);
            }

            // Advance to next attribute (attributes are padded to 4-byte boundary)
            offset += 4 + ((attrLength + 3) & ~3);
        }

        throw new Exception("No MAPPED-ADDRESS or XOR-MAPPED-ADDRESS found in STUN response");
    }
}

public class P2PMessenger
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _remoteEndPoint;

    public P2PMessenger(int localPort, IPEndPoint remoteEndPoint)
    {
        _udp = new UdpClient(localPort);
        _remoteEndPoint = remoteEndPoint;
    }

    public async Task StartAsync()
    {
        // Start a background task to receive messages
        _ = Task.Run(ReceiveLoop);

        // Main loop to send messages
        while (true)
        {
            string? message = Console.ReadLine();
            if (string.IsNullOrEmpty(message)) break;

            byte[] data = Encoding.UTF8.GetBytes(message);
            await _udp.SendAsync(data, data.Length, _remoteEndPoint);
        }
    }

    private async Task ReceiveLoop()
    {
        while (true)
        {
            var result = await _udp.ReceiveAsync();
            string message = Encoding.UTF8.GetString(result.Buffer);
            Console.WriteLine($"Received: {message} from {result.RemoteEndPoint}");
        }
    }
}