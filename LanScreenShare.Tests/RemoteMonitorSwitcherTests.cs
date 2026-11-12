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
    public class RemoteMonitorSwitcherTests
    {
        private sealed class FakeInputSimulator : IInputSimulatorService
        {
            public readonly List<RemoteInputMessage> SimulatedInputs = new();

            public void SimulateInput(RemoteInputMessage input)
            {
                lock (SimulatedInputs)
                {
                    SimulatedInputs.Add(input);
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
        public void Lsi2ControlPacket_SwitchMonitor_PreservesCycleAndSpecificIndices()
        {
            byte[] buffer = new byte[Lsi2ControlPacket.PacketSize];

            // Caso 1: -1 (Ciclar al siguiente monitor)
            var cycleMsg = new RemoteInputMessage
            {
                Type = InputEventType.SwitchMonitor,
                KeyCode = -1
            };
            Lsi2ControlPacket.WritePacket(buffer, 10UL, 20UL, cycleMsg);

            Assert.IsTrue(Lsi2ControlPacket.TryReadPacket(buffer, out var h1, out var parsed1, out _));
            Assert.IsNotNull(parsed1);
            Assert.AreEqual(InputEventType.SwitchMonitor, parsed1.Type);
            Assert.AreEqual(-1, parsed1.KeyCode);
            Assert.AreEqual(10UL, h1.SequenceNumber);

            // Caso 2: Índice específico (e.g., monitor 2)
            var indexMsg = new RemoteInputMessage
            {
                Type = InputEventType.SwitchMonitor,
                KeyCode = 2
            };
            Lsi2ControlPacket.WritePacket(buffer, 11UL, 21UL, indexMsg);

            Assert.IsTrue(Lsi2ControlPacket.TryReadPacket(buffer, out _, out var parsed2, out _));
            Assert.IsNotNull(parsed2);
            Assert.AreEqual(InputEventType.SwitchMonitor, parsed2.Type);
            Assert.AreEqual(2, parsed2.KeyCode);
        }

        [TestMethod]
        public async Task SwitchMonitor_ViewerRequestsSwitch_AndHostBroadcastsActiveMonitorInfo()
        {
            var fakeSimulator = new FakeInputSimulator();
            using var server = new TcpControlServer(inputSimulator: fakeSimulator);
            int port = 15203;
            await server.StartAsync(port);

            var switchRequestedTcs = new TaskCompletionSource<int>();
            var monitorInfoReceivedTcs = new TaskCompletionSource<string>();

            server.SwitchMonitorRequested += (s, idx) =>
            {
                switchRequestedTcs.TrySetResult(idx);
                _ = server.BroadcastMonitorInfoAsync("Pantalla 2");
            };

            using var client = new TcpControlClient();
            client.MonitorInfoReceived += (s, label) =>
            {
                monitorInfoReceivedTcs.TrySetResult(label);
            };

            await client.ConnectAsync("127.0.0.1", port);
            Assert.IsTrue(client.IsConnected);

            await client.SendSwitchMonitorAsync(-1);

            var reqDone = await Task.WhenAny(switchRequestedTcs.Task, Task.Delay(3000));
            Assert.AreEqual(switchRequestedTcs.Task, reqDone, "El servidor debió recibir la solicitud de cambio de monitor.");
            Assert.AreEqual(-1, await switchRequestedTcs.Task);

            var infoDone = await Task.WhenAny(monitorInfoReceivedTcs.Task, Task.Delay(3000));
            Assert.AreEqual(monitorInfoReceivedTcs.Task, infoDone, "El visor debió recibir la confirmación del nuevo monitor activo.");
            Assert.AreEqual("Pantalla 2", await monitorInfoReceivedTcs.Task);

            // SwitchMonitor jamás debe llegar al simulador de teclado/ratón
            lock (fakeSimulator.SimulatedInputs)
            {
                Assert.AreEqual(0, fakeSimulator.SimulatedInputs.Count);
            }

            await client.DisconnectAsync();
            server.Stop();
        }

        [TestMethod]
        public async Task SwitchMonitor_OverMtlsWithSessionAuthority_CyclesMultipleMonitorsSuccessfully()
        {
            using var hostCert = CreateEcdsaCert("MonitorHost", TlsConfigurationHelper.ServerAuthOid);
            using var viewerCert = CreateEcdsaCert("MonitorViewer", TlsConfigurationHelper.ClientAuthOid);

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
            int port = 15204;
            await server.StartAsync(port);

            string[] simulatedMonitors = { "Pantalla 1", "Pantalla 2", "Todas las pantallas" };
            int currentIdx = 0;

            var receivedLabels = new List<string>();
            var threeSwitchesDoneTcs = new TaskCompletionSource<bool>();

            server.SwitchMonitorRequested += (s, requestedIdx) =>
            {
                currentIdx = (currentIdx + 1) % simulatedMonitors.Length;
                _ = server.BroadcastMonitorInfoAsync(simulatedMonitors[currentIdx]);
            };

            using var client = new TcpControlClient();
            client.MonitorInfoReceived += (s, label) =>
            {
                lock (receivedLabels)
                {
                    receivedLabels.Add(label);
                    if (receivedLabels.Count >= 3)
                    {
                        threeSwitchesDoneTcs.TrySetResult(true);
                    }
                }
            };

            await client.ConnectAsync(new ControlClientConnectionOptions("127.0.0.1", port, clientSslOpts, session.SessionId, controlToken.TokenId));

            await client.SendSwitchMonitorAsync(-1);
            await client.SendSwitchMonitorAsync(-1);
            await client.SendSwitchMonitorAsync(-1);

            var done = await Task.WhenAny(threeSwitchesDoneTcs.Task, Task.Delay(3000));
            Assert.AreEqual(threeSwitchesDoneTcs.Task, done, "El visor debió recibir las 3 notificaciones de ciclo de monitores.");

            lock (receivedLabels)
            {
                CollectionAssert.AreEqual(
                    new[] { "Pantalla 2", "Todas las pantallas", "Pantalla 1" },
                    receivedLabels);
            }

            await client.DisconnectAsync();
            server.Stop();
        }

        [TestMethod]
        public async Task SelectSource_ViewerRequestsCatalogAndSelectsWindowSourceById()
        {
            using var server = new TcpControlServer();
            int port = 15205;
            await server.StartAsync(port);

            var catalogRequestedTcs = new TaskCompletionSource<bool>();
            var selectSourceTcs = new TaskCompletionSource<string>();
            var catalogReceivedTcs = new TaskCompletionSource<string>();

            server.SourceCatalogRequested += (s, e) =>
            {
                catalogRequestedTcs.TrySetResult(true);
            };

            server.SelectSourceRequested += (s, sourceId) =>
            {
                selectSourceTcs.TrySetResult(sourceId);
                _ = server.BroadcastMonitorInfoAsync("{\"ActiveSourceId\":\"" + sourceId + "\",\"ActiveSourceName\":\"Visual Studio Code\",\"Sources\":[]}");
            };

            using var client = new TcpControlClient();
            client.MonitorInfoReceived += (s, payload) =>
            {
                catalogReceivedTcs.TrySetResult(payload);
            };

            await client.ConnectAsync("127.0.0.1", port);
            await client.RequestSourceCatalogAsync();

            var catReqDone = await Task.WhenAny(catalogRequestedTcs.Task, Task.Delay(3000));
            Assert.AreEqual(catalogRequestedTcs.Task, catReqDone, "El servidor debió recibir la solicitud de catálogo de fuentes.");

            await client.SendSelectSourceAsync("window:98765");

            var selDone = await Task.WhenAny(selectSourceTcs.Task, Task.Delay(3000));
            Assert.AreEqual(selectSourceTcs.Task, selDone, "El servidor debió recibir el ID de la fuente seleccionada.");
            Assert.AreEqual("window:98765", await selectSourceTcs.Task);

            var infoDone = await Task.WhenAny(catalogReceivedTcs.Task, Task.Delay(3000));
            Assert.AreEqual(catalogReceivedTcs.Task, infoDone);
            StringAssert.Contains(await catalogReceivedTcs.Task, "Visual Studio Code");

            await client.DisconnectAsync();
            server.Stop();
        }
    }
}

