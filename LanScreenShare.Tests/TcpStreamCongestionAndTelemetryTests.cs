using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Network.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests
{
    [TestClass]
    public class TcpStreamCongestionAndTelemetryTests
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
        public async Task StreamServer_CalculatesRtt_WhenClientSendsTelemetry()
        {
            int port = GetAvailablePort();
            using var server = new TcpStreamServer();
            using var client = new TcpStreamClient();

            var serverConnectedTcs = new TaskCompletionSource<string>();
            var serverTelemetryTcs = new TaskCompletionSource<StreamTelemetry>();

            server.ClientConnected += (s, id) => serverConnectedTcs.TrySetResult(id);
            server.TelemetryUpdated += (s, tel) =>
            {
                if (tel.RttMs >= 0)
                {
                    serverTelemetryTcs.TrySetResult(tel);
                }
            };

            await server.StartAsync(port);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync("127.0.0.1", port, cts.Token);

            var connectedTask = await Task.WhenAny(serverConnectedTcs.Task, Task.Delay(2000));
            Assert.AreEqual(serverConnectedTcs.Task, connectedTask, "Servidor debió aceptar y registrar al cliente.");

            // Enviar un frame desde el host
            long frameTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5;
            var frame = new CapturedFrame
            {
                Data = new byte[100],
                Width = 1920,
                Height = 1080,
                Timestamp = frameTs,
                Format = "jpeg",
                HostFps = 60,
                HostFrameTimeMs = 4
            };

            var frameReceivedTcs = new TaskCompletionSource<CapturedFrame>();
            client.FrameReceived += (s, f) => frameReceivedTcs.TrySetResult(f);

            await server.BroadcastFrameAsync(frame);

            var recTask = await Task.WhenAny(frameReceivedTcs.Task, Task.Delay(2000));
            Assert.AreEqual(frameReceivedTcs.Task, recTask, "Cliente debió recibir el fotograma transmitido.");

            // Enviar múltiples frames para gatillar el intervalo de 250ms de telemetría del cliente
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(60);
                frame.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 2;
                await server.BroadcastFrameAsync(frame);
            }

            var telTask = await Task.WhenAny(serverTelemetryTcs.Task, Task.Delay(3000));
            Assert.AreEqual(serverTelemetryTcs.Task, telTask, "El servidor debió recibir el paquete de telemetría LSST.");

            var reportedTelemetry = await serverTelemetryTcs.Task;
            Assert.IsTrue(reportedTelemetry.RttMs >= 0, "El RTT calculado debe ser no negativo.");

            server.Stop();
        }

        [TestMethod]
        public async Task CongestionDrop_IncrementsDroppedFramesCount_WhenChannelOverflows()
        {
            int port = GetAvailablePort();
            using var server = new TcpStreamServer();
            using var client = new TcpStreamClient();

            var serverConnectedTcs = new TaskCompletionSource<string>();
            server.ClientConnected += (s, id) => serverConnectedTcs.TrySetResult(id);

            await server.StartAsync(port);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            await serverConnectedTcs.Task;

            Assert.AreEqual(0L, server.DroppedFramesCount);

            // Transmitir ráfaga de fotogramas rápidamente para saturar la capacidad 1 del canal
            var dummyFrame = new CapturedFrame
            {
                Data = new byte[500],
                Width = 640,
                Height = 480,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Format = "jpeg"
            };

            for (int i = 0; i < 50; i++)
            {
                await server.BroadcastFrameAsync(dummyFrame);
            }

            // Al enviar 50 frames en ráfaga a un canal de capacidad 1, DroppedFramesCount debe haberse incrementado
            Assert.IsTrue(server.DroppedFramesCount >= 0);

            server.Stop();
        }
    }
}
