using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Services;
using LanScreenShare.Network.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests
{
    [TestClass]
    public class BidirectionalAudioTests
    {
        [TestMethod]
        public async Task ViewerMicrophone_TransmitsFromClientToServerSuccessfully()
        {
            using var server = new TcpAudioServer();
            int port = 15002;
            await server.StartAsync(port);

            var tcsReceived = new TaskCompletionSource<CapturedAudioChunk>();
            server.ClientAudioReceived += (s, chunk) =>
            {
                tcsReceived.TrySetResult(chunk);
            };

            using var client = new TcpAudioClient();
            await client.ConnectAsync("127.0.0.1", port);

            Assert.IsTrue(client.IsConnected, "El cliente de audio debe conectarse al servidor.");

            byte[] dummyPcm = new byte[128];
            Array.Fill(dummyPcm, (byte)0x7F);

            var sendChunk = new CapturedAudioChunk
            {
                Data = dummyPcm,
                SampleRate = 48000,
                Channels = 2,
                BitsPerSample = 16,
                AudioFormat = 1,
                Source = AudioSourceType.ViewerMicrophone,
                Timestamp = 123456789L
            };

            await client.SendAudioChunkAsync(sendChunk);

            var completedTask = await Task.WhenAny(tcsReceived.Task, Task.Delay(3000));
            Assert.AreEqual(tcsReceived.Task, completedTask, "El servidor de audio debió recibir el paquete de micrófono del visor.");

            var receivedChunk = await tcsReceived.Task;
            Assert.IsNotNull(receivedChunk);
            Assert.AreEqual(AudioSourceType.ViewerMicrophone, receivedChunk.Source);
            Assert.AreEqual(sendChunk.SampleRate, receivedChunk.SampleRate);
            Assert.AreEqual(sendChunk.Channels, receivedChunk.Channels);
            Assert.AreEqual(sendChunk.Timestamp, receivedChunk.Timestamp);
            CollectionAssert.AreEqual(sendChunk.Data, receivedChunk.Data);

            await client.DisconnectAsync();
            server.Stop();
        }

        [TestMethod]
        public async Task DesktopAudio_TransmitsFromServerToClientSuccessfully()
        {
            using var server = new TcpAudioServer();
            int port = 15003;
            await server.StartAsync(port);

            var tcsReceived = new TaskCompletionSource<CapturedAudioChunk>();

            using var client = new TcpAudioClient();
            client.AudioReceived += (s, chunk) =>
            {
                tcsReceived.TrySetResult(chunk);
            };

            await client.ConnectAsync("127.0.0.1", port);
            Assert.IsTrue(client.IsConnected);

            // Breve espera para que el servidor registre el cliente
            await Task.Delay(100);

            byte[] dummyAudio = new byte[256];
            Array.Fill(dummyAudio, (byte)0x33);

            var hostChunk = new CapturedAudioChunk
            {
                Data = dummyAudio,
                SampleRate = 48000,
                Channels = 2,
                BitsPerSample = 16,
                AudioFormat = 1,
                Source = AudioSourceType.Desktop,
                Timestamp = 987654321L
            };

            await server.BroadcastAudioAsync(hostChunk);

            var completedTask = await Task.WhenAny(tcsReceived.Task, Task.Delay(3000));
            Assert.AreEqual(tcsReceived.Task, completedTask, "El cliente debió recibir el audio emitido por el host.");

            var received = await tcsReceived.Task;
            Assert.IsNotNull(received);
            Assert.AreEqual(AudioSourceType.Desktop, received.Source);
            Assert.AreEqual(987654321L, received.Timestamp);
            CollectionAssert.AreEqual(hostChunk.Data, received.Data);

            await client.DisconnectAsync();
            server.Stop();
        }

        [TestMethod]
        public async Task AudioClient_SessionDoesNotDisconnectWhenConnectTimeoutExpires()
        {
            using var server = new TcpAudioServer();
            int port = 15004;
            await server.StartAsync(port);

            using var client = new TcpAudioClient();

            // Usamos un CancellationToken con timeout de 300ms para conectar
            using (var shortTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                await client.ConnectAsync("127.0.0.1", port, cancellationToken: shortTimeoutCts.Token);
                Assert.IsTrue(client.IsConnected, "El cliente debió conectarse antes del timeout.");
            } // Aquí shortTimeoutCts es liberado (Dispose)

            // Esperar 600ms (tiempo superior al timeout de conexión original)
            await Task.Delay(600);

            // Verificar que la sesión larga de audio NO se desconectó
            Assert.IsTrue(client.IsConnected, "El cliente de audio debe permanecer conectado aunque el token del handshake haya finalizado.");

            await client.DisconnectAsync();
            server.Stop();
        }

        [TestMethod]
        public void ProcessAudioChunk_DoesNotDropAudio_UnderModerateDrift()
        {
            var sync = new AudioVideoSyncController();
            long baseTs = 1000000L;

            // Simular recepción de fotograma de video
            sync.OnVideoFrameReceived(baseTs);

            // Simular audio con desfase normal moderado de 150ms de retraso
            long audioTs = baseTs - 150;
            var action = sync.ProcessAudioChunk(audioTs, out int delayMs);

            Assert.AreEqual(AvSyncAction.PlayImmediately, action, "Audio con desfase moderado (<400ms) no debe ser descartado.");
        }
    }
}
