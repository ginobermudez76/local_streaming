using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Core.Services;
using LanScreenShare.Network.Security;
using LanScreenShare.Network.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests
{
    [TestClass]
    public class BidirectionalAudioTests
    {
        private static X509Certificate2 CreateEcdsaCert(string cn, string ekuOid)
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest($"CN={cn}", ecdsa, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: true));
            var eku = new OidCollection { new Oid(ekuOid) };
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, critical: true));

            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
            byte[] pfx = cert.Export(X509ContentType.Pfx);
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
        }

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

            // Esperar determinísticamente a que el servidor registre el cliente
            for (int i = 0; i < 40 && server.ConnectedClientsCount == 0; i++)
            {
                await Task.Delay(25);
            }
            Assert.AreEqual(1, server.ConnectedClientsCount, "El servidor de audio debió registrar al cliente.");

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
        public async Task AudioServer_SurvivesSubscriberExceptionInClientAudioReceived_AndContinuesBroadcasting()
        {
            using var server = new TcpAudioServer();
            int port = 15005;
            await server.StartAsync(port);

            server.ClientAudioReceived += (s, chunk) =>
            {
                throw new InvalidOperationException("Simulated UI thread cross-access exception");
            };

            using var client = new TcpAudioClient();
            var tcsClientReceivedDesktop = new TaskCompletionSource<CapturedAudioChunk>();
            client.AudioReceived += (s, chunk) =>
            {
                tcsClientReceivedDesktop.TrySetResult(chunk);
            };

            await client.ConnectAsync("127.0.0.1", port);
            await Task.Delay(100);

            // 1. El visor envía un paquete de micrófono cuyo handler lanza excepción en el servidor
            await client.SendAudioChunkAsync(new CapturedAudioChunk
            {
                Data = new byte[] { 1, 2, 3, 4 },
                SampleRate = 48000,
                Channels = 2,
                BitsPerSample = 16,
                AudioFormat = 1,
                Source = AudioSourceType.ViewerMicrophone,
                Timestamp = 100L
            });

            await Task.Delay(150);

            // 2. El servidor NO debe haber desconectado al cliente y debe poder emitir audio de escritorio
            Assert.AreEqual(1, server.ConnectedClientsCount, "El servidor no debe desconectar al cliente si un suscriptor lanza excepción.");
            Assert.IsTrue(client.IsConnected, "El cliente debe seguir conectado.");

            await server.BroadcastAudioAsync(new CapturedAudioChunk
            {
                Data = new byte[] { 9, 8, 7, 6 },
                SampleRate = 48000,
                Channels = 2,
                BitsPerSample = 16,
                AudioFormat = 1,
                Source = AudioSourceType.Desktop,
                Timestamp = 200L
            });

            var completed = await Task.WhenAny(tcsClientReceivedDesktop.Task, Task.Delay(2000));
            Assert.AreEqual(tcsClientReceivedDesktop.Task, completed, "El cliente debe seguir recibiendo audio del servidor.");

            await client.DisconnectAsync();
            server.Stop();
        }

        private static bool HasMinChunks(int val, int min) => val >= min;

        private static bool AreAllChunksReceived(int vHost, int dViewer, int mViewer) =>
            HasMinChunks(vHost, 5) && HasMinChunks(dViewer, 5) && HasMinChunks(mViewer, 5);

        [TestMethod]
        public async Task BidirectionalAudio_OverMtlsSslStream_SimultaneousFullDuplex_Succeeds()
        {
            using var hostCert = CreateEcdsaCert("AudioHost", TlsConfigurationHelper.ServerAuthOid);
            using var viewerCert = CreateEcdsaCert("AudioViewer", TlsConfigurationHelper.ClientAuthOid);

            string hostFp = TlsConfigurationHelper.ComputeFingerprint(hostCert);
            string viewerFp = TlsConfigurationHelper.ComputeFingerprint(viewerCert);

            var store = new InMemoryPairedDeviceStore();
            await store.AddPairedDeviceAsync(new PairedDevice("h1", "Host", hostFp, DateTime.UtcNow, DateTime.UtcNow, false));
            await store.AddPairedDeviceAsync(new PairedDevice("v1", "Viewer", viewerFp, DateTime.UtcNow, DateTime.UtcNow, false));

            var authority = new SessionAuthority(store);
            var (created, session, _) = await authority.TryCreateSessionAsync(viewerFp);
            Assert.IsTrue(created);
            Assert.IsNotNull(session);

            var audioToken = authority.IssueChannelToken(session.SessionId, ChannelType.Audio);
            var serverSslOpts = TlsConfigurationHelper.CreateServerOptions(hostCert, store);
            var clientSslOpts = TlsConfigurationHelper.CreateClientOptions(viewerCert, "localhost", store);

            using var server = new TcpAudioServer(authority, serverSslOpts);
            int port = 15006;
            await server.StartAsync(port);

            int viewerChunksOnHost = 0;
            int hostDesktopChunksOnViewer = 0;
            int hostMicChunksOnViewer = 0;
            var tcsAllReceived = new TaskCompletionSource<bool>();

            void CheckCompletion()
            {
                if (AreAllChunksReceived(
                        Volatile.Read(ref viewerChunksOnHost),
                        Volatile.Read(ref hostDesktopChunksOnViewer),
                        Volatile.Read(ref hostMicChunksOnViewer)))
                {
                    tcsAllReceived.TrySetResult(true);
                }
            }

            server.ClientAudioReceived += (s, chunk) =>
            {
                if (chunk.Source == AudioSourceType.ViewerMicrophone)
                {
                    Interlocked.Increment(ref viewerChunksOnHost);
                    CheckCompletion();
                }
            };

            using var client = new TcpAudioClient();
            client.AudioReceived += (s, chunk) =>
            {
                if (chunk.Source == AudioSourceType.Desktop)
                    Interlocked.Increment(ref hostDesktopChunksOnViewer);
                else if (chunk.Source == AudioSourceType.Microphone)
                    Interlocked.Increment(ref hostMicChunksOnViewer);

                CheckCompletion();
            };

            await client.ConnectAsync(new AudioClientConnectionOptions("127.0.0.1", port, clientSslOpts, session.SessionId, audioToken.TokenId));
            await Task.Delay(100);

            Assert.AreEqual(1, server.ConnectedClientsCount, "El cliente mTLS debe estar registrado en el servidor.");

            await SendSimultaneousDuplexAudioAsync(server, client);

            var done = await Task.WhenAny(tcsAllReceived.Task, Task.Delay(3000));
            Assert.AreEqual(tcsAllReceived.Task, done,
                $"Fallo recepción full-duplex mTLS (ViewerOnHost={viewerChunksOnHost}, DesktopOnViewer={hostDesktopChunksOnViewer}, MicOnViewer={hostMicChunksOnViewer})");

            await client.DisconnectAsync();
            server.Stop();
        }

        private static async Task SendSimultaneousDuplexAudioAsync(TcpAudioServer server, TcpAudioClient client)
        {
            var tasks = new List<Task>();
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(server.BroadcastAudioAsync(new CapturedAudioChunk
                {
                    Data = new byte[512],
                    SampleRate = 48000,
                    Channels = 2,
                    BitsPerSample = 32,
                    AudioFormat = 3,
                    Source = AudioSourceType.Desktop,
                    Timestamp = 1000 + i
                }));

                tasks.Add(server.BroadcastAudioAsync(new CapturedAudioChunk
                {
                    Data = new byte[256],
                    SampleRate = 48000,
                    Channels = 1,
                    BitsPerSample = 16,
                    AudioFormat = 1,
                    Source = AudioSourceType.Microphone,
                    Timestamp = 2000 + i
                }));

                tasks.Add(client.SendAudioChunkAsync(new CapturedAudioChunk
                {
                    Data = new byte[256],
                    SampleRate = 48000,
                    Channels = 1,
                    BitsPerSample = 16,
                    AudioFormat = 1,
                    Source = AudioSourceType.ViewerMicrophone,
                    Timestamp = 3000 + i
                }));
            }

            await Task.WhenAll(tasks);
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
