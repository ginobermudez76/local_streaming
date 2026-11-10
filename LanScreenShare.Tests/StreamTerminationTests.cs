using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Network.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class StreamTerminationTests
{
    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [TestMethod]
    public void FrameHeaderMagicEnd_IsExpectedMagicLSS0()
    {
        // 0x4C535330 en ASCII: 'L' (0x4C), 'S' (0x53), 'S' (0x53), '0' (0x30)
        uint magic = TcpStreamServer.FrameHeaderMagicEnd;
        Assert.AreEqual((byte)'L', (byte)((magic >> 24) & 0xFF));
        Assert.AreEqual((byte)'S', (byte)((magic >> 16) & 0xFF));
        Assert.AreEqual((byte)'S', (byte)((magic >> 8) & 0xFF));
        Assert.AreEqual((byte)'0', (byte)(magic & 0xFF));
    }

    [TestMethod]
    public async Task WhenHostStops_ClientReceivesTerminationStatus()
    {
        int port = 15301;
        using var server = new TcpStreamServer();
        using var client = new TcpStreamClient();

        var clientConnectedTcs = new TaskCompletionSource<string>();
        server.ClientConnected += (s, id) => clientConnectedTcs.TrySetResult(id);

        await server.StartAsync(port);

        string lastStatus = string.Empty;
        var statusTcs = new TaskCompletionSource<string>();

        client.StatusChanged += (s, status) =>
        {
            lastStatus = status;
            if (status.Contains("finaliz", StringComparison.OrdinalIgnoreCase))
            {
                statusTcs.TrySetResult(status);
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("127.0.0.1", port, cts.Token);

        // Esperar a que el servidor haya aceptado y registrado completamente al cliente en _clients
        var connectedTask = await Task.WhenAny(clientConnectedTcs.Task, Task.Delay(2000));
        Assert.AreEqual(clientConnectedTcs.Task, connectedTask, "El servidor debió registrar al cliente antes de continuar.");
        Assert.IsTrue(client.IsConnected, "El cliente debería estar conectado.");

        // El host detiene la transmisión
        server.Stop();

        // Esperar a que el cliente reciba la señal LSS0 y reporte la finalización
        var completedTask = await Task.WhenAny(statusTcs.Task, Task.Delay(3000));
        Assert.AreEqual(statusTcs.Task, completedTask, "El cliente debió recibir la señal de fin de transmisión antes del timeout.");

        string finalStatus = await statusTcs.Task;
        StringAssert.Contains(finalStatus, "finalizado");
        Assert.IsFalse(client.IsConnected, "El cliente debe quedar desconectado tras finalizar la transmisión.");
    }
}
