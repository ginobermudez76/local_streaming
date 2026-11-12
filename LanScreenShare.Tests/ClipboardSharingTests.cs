using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;
using LanScreenShare.Network.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests
{
    [TestClass]
    public class ClipboardSharingTests
    {
        private sealed class FakeInputSimulator : IInputSimulatorService
        {
            public readonly List<RemoteInputMessage> SimulatedInputs = new();
            public readonly TaskCompletionSource<bool> TwoInputsSimulatedTcs = new();

            public void SimulateInput(RemoteInputMessage input)
            {
                lock (SimulatedInputs)
                {
                    SimulatedInputs.Add(input);
                    if (SimulatedInputs.Count >= 2)
                    {
                        TwoInputsSimulatedTcs.TrySetResult(true);
                    }
                }
            }

            public void ReleaseInjectedInputs()
            {
            }

            public void SetTargetSource(CaptureSource source)
            {
            }

            public void Dispose()
            {
            }
        }

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
        public void Lsi2ControlPacket_ClipboardTextHeader_RoundTripsPayloadLengthInKeyCode()
        {
            byte[] buffer = new byte[Lsi2ControlPacket.PacketSize];
            var msg = new RemoteInputMessage
            {
                Type = InputEventType.ClipboardText,
                KeyCode = 4096
            };

            Lsi2ControlPacket.WritePacket(buffer, sequenceNumber: 42UL, timestamp: 99999UL, msg);

            bool ok = Lsi2ControlPacket.TryReadPacket(
                buffer,
                out var header,
                out var parsed,
                out string? error);

            Assert.IsTrue(ok, $"TryReadPacket falló: {error}");
            Assert.IsNotNull(parsed);
            Assert.AreEqual(42UL, header.SequenceNumber);
            Assert.AreEqual(99999UL, header.Timestamp);
            Assert.AreEqual(InputEventType.ClipboardText, parsed.Type);
            Assert.AreEqual(4096, parsed.KeyCode);
        }

        [TestMethod]
        public async Task ClipboardText_ViewerToHost_InterleavedWithInputEvents_PreservesStreamAlignment()
        {
            var fakeSimulator = new FakeInputSimulator();
            using var server = new TcpControlServer(inputSimulator: fakeSimulator);
            int port = 15103;
            await server.StartAsync(port);

            var receivedClipboardTcs = new TaskCompletionSource<string>();
            var receivedKeyUpTcs = new TaskCompletionSource<int>();

            server.ClipboardTextReceived += (s, text) =>
            {
                receivedClipboardTcs.TrySetResult(text);
            };

            server.InputReceived += (s, input) =>
            {
                if (input.Type == InputEventType.KeyUp)
                {
                    receivedKeyUpTcs.TrySetResult(input.KeyCode);
                }
            };

            using var client = new TcpControlClient();
            await client.ConnectAsync("127.0.0.1", port);
            Assert.IsTrue(client.IsConnected);

            // 1. Enviar KeyDown antes del portapapeles
            await client.SendInputAsync(new RemoteInputMessage
            {
                Type = InputEventType.KeyDown,
                KeyCode = 0x41 // 'A'
            });

            // 2. Enviar texto de portapapeles UTF-8 con caracteres especiales y emojis
            string expectedText = "¡Hola desde el espectador! 🚀 Línea 2 con acentos: áéíóú ñ";
            await client.SendClipboardTextAsync(expectedText);

            // 3. Enviar KeyUp inmediatamente después para comprobar que el stream sigue alineado a 28 bytes
            await client.SendInputAsync(new RemoteInputMessage
            {
                Type = InputEventType.KeyUp,
                KeyCode = 0x41
            });

            var clipDone = await Task.WhenAny(receivedClipboardTcs.Task, Task.Delay(3000));
            Assert.AreEqual(receivedClipboardTcs.Task, clipDone, "El servidor debió recibir el texto del portapapeles.");
            Assert.AreEqual(expectedText, await receivedClipboardTcs.Task);

            var keyUpDone = await Task.WhenAny(receivedKeyUpTcs.Task, Task.Delay(3000));
            Assert.AreEqual(receivedKeyUpTcs.Task, keyUpDone, "El paquete KeyUp posterior al portapapeles debió procesarse sin desalineación.");
            Assert.AreEqual(0x41, await receivedKeyUpTcs.Task);

            var simDone = await Task.WhenAny(fakeSimulator.TwoInputsSimulatedTcs.Task, Task.Delay(3000));
            Assert.AreEqual(fakeSimulator.TwoInputsSimulatedTcs.Task, simDone, "El simulador debió recibir los dos eventos de teclado.");

            // Verificar que ClipboardText NO se envió al simulador físico de teclado/ratón
            lock (fakeSimulator.SimulatedInputs)
            {
                Assert.AreEqual(2, fakeSimulator.SimulatedInputs.Count);
                Assert.IsFalse(fakeSimulator.SimulatedInputs.Exists(i => i.Type == InputEventType.ClipboardText));
            }

            await client.DisconnectAsync();
            server.Stop();
        }

        [TestMethod]
        public async Task ClipboardText_BidirectionalOverMtls_HostToViewerAndViewerToHost_Succeeds()
        {
            using var hostCert = CreateEcdsaCert("ControlHost", TlsConfigurationHelper.ServerAuthOid);
            using var viewerCert = CreateEcdsaCert("ControlViewer", TlsConfigurationHelper.ClientAuthOid);

            string hostFp = TlsConfigurationHelper.ComputeFingerprint(hostCert);
            string viewerFp = TlsConfigurationHelper.ComputeFingerprint(viewerCert);

            var store = new InMemoryPairedDeviceStore();
            await store.AddPairedDeviceAsync(new PairedDevice("h1", "Host", hostFp, DateTime.UtcNow, DateTime.UtcNow, false));
            await store.AddPairedDeviceAsync(new PairedDevice("v1", "Viewer", viewerFp, DateTime.UtcNow, DateTime.UtcNow, false));

            var authority = new SessionAuthority(store);
            var (created, session, _) = await authority.TryCreateSessionAsync(viewerFp);
            Assert.IsTrue(created);
            Assert.IsNotNull(session);

            var controlToken = authority.IssueChannelToken(session.SessionId, ChannelType.RemoteControl);
            Assert.IsNotNull(controlToken);

            var serverSslOpts = TlsConfigurationHelper.CreateServerOptions(hostCert, store);
            var clientSslOpts = TlsConfigurationHelper.CreateClientOptions(viewerCert, "localhost", store);

            using var server = new TcpControlServer(authority, null, serverSslOpts);
            int port = 15104;
            await server.StartAsync(port);

            var hostReceivedTcs = new TaskCompletionSource<string>();
            var viewerReceivedTcs = new TaskCompletionSource<string>();

            server.ClipboardTextReceived += (s, text) => hostReceivedTcs.TrySetResult(text);

            using var client = new TcpControlClient();
            client.ClipboardTextReceived += (s, text) => viewerReceivedTcs.TrySetResult(text);

            // Conectar con un CancellationToken de handshake con margen suficiente para concurrencia de tests
            using (var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                await client.ConnectAsync(new ControlClientConnectionOptions("127.0.0.1", port, clientSslOpts, session.SessionId, controlToken.TokenId), handshakeCts.Token);
            }

            // Esperar a que venza el token de handshake (500ms) y a que el servidor registre al controlador activo
            await Task.Delay(500);
            Assert.IsTrue(client.IsConnected, "El cliente de control debe seguir conectado tras vencer el token de handshake.");

            string hostText = "Texto copiado en el Anfitrión (Host -> Viewer)";
            string viewerText = "Texto copiado en el Espectador (Viewer -> Host)";

            await server.BroadcastClipboardTextAsync(hostText);
            await client.SendClipboardTextAsync(viewerText);

            var bothDone = await Task.WhenAny(
                Task.WhenAll(hostReceivedTcs.Task, viewerReceivedTcs.Task),
                Task.Delay(3000));

            Assert.IsTrue(hostReceivedTcs.Task.IsCompleted && viewerReceivedTcs.Task.IsCompleted,
                "Ambas partes debieron recibir el texto del portapapeles sobre mTLS.");

            Assert.AreEqual(viewerText, await hostReceivedTcs.Task);
            Assert.AreEqual(hostText, await viewerReceivedTcs.Task);

            await client.DisconnectAsync();
            server.Stop();
        }
    }
}
