using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;
using LanScreenShare.Network.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests
{
    [TestClass]
    public class FileTransferTests
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
        public void UniqueDestinationPath_GeneratesUniqueNamesCorrectly()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "LssTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string p1 = TcpFileTransferServer.GetUniqueDestinationPath(tempDir, "foto.png");
                Assert.AreEqual(Path.Combine(tempDir, "foto.png"), p1);

                File.WriteAllText(p1, "dummy");

                string p2 = TcpFileTransferServer.GetUniqueDestinationPath(tempDir, "foto.png");
                Assert.AreEqual(Path.Combine(tempDir, "foto (1).png"), p2);

                File.WriteAllText(p2, "dummy2");

                string p3 = TcpFileTransferServer.GetUniqueDestinationPath(tempDir, "foto.png");
                Assert.AreEqual(Path.Combine(tempDir, "foto (2).png"), p3);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [TestMethod]
        public async Task FileTransfer_ClientToServer_TransfersContentAccurately()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "LssTransfer_" + Guid.NewGuid().ToString("N"));
            string serverDownloads = Path.Combine(testDir, "ServerDownloads");
            Directory.CreateDirectory(testDir);
            Directory.CreateDirectory(serverDownloads);

            // Crear un archivo de prueba con 250 KB de datos aleatorios
            string sourceFile = Path.Combine(testDir, "payload.dat");
            byte[] expectedData = new byte[250 * 1024];
            Random.Shared.NextBytes(expectedData);
            await File.WriteAllBytesAsync(sourceFile, expectedData);

            int port = 15401;
            using var server = new TcpFileTransferServer();
            server.DownloadDirectory = serverDownloads;
            await server.StartAsync(port);

            var serverReceivedTcs = new TaskCompletionSource<FileTransferCompleted>();
            server.TransferCompleted += (s, c) => serverReceivedTcs.TrySetResult(c);

            using var client = new TcpFileTransferClient();
            await client.ConnectAsync("127.0.0.1", port);
            Assert.IsTrue(client.IsConnected);

            bool sendResult = await client.SendFileAsync(sourceFile);
            Assert.IsTrue(sendResult, "El cliente debió enviar el archivo exitosamente.");

            var completedTask = await Task.WhenAny(serverReceivedTcs.Task, Task.Delay(5000));
            Assert.AreEqual(serverReceivedTcs.Task, completedTask, "El servidor debió completar la recepción antes del timeout.");

            var completed = await serverReceivedTcs.Task;
            Assert.IsFalse(completed.IsUpload);
            Assert.IsTrue(File.Exists(completed.FilePath));

            byte[] actualData = await File.ReadAllBytesAsync(completed.FilePath);
            CollectionAssert.AreEqual(expectedData, actualData, "El contenido recibido debe coincidir exactamente con el enviado.");

            await client.DisconnectAsync();
            server.Stop();

            try { Directory.Delete(testDir, true); } catch { }
        }

        [TestMethod]
        public async Task FileTransfer_ServerToClient_TransfersContentAccurately()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "LssTransferReverse_" + Guid.NewGuid().ToString("N"));
            string clientDownloads = Path.Combine(testDir, "ClientDownloads");
            Directory.CreateDirectory(testDir);
            Directory.CreateDirectory(clientDownloads);

            string sourceFile = Path.Combine(testDir, "doc.pdf");
            byte[] expectedData = new byte[180 * 1024];
            Random.Shared.NextBytes(expectedData);
            await File.WriteAllBytesAsync(sourceFile, expectedData);

            int port = 15402;
            using var server = new TcpFileTransferServer();
            await server.StartAsync(port);

            var clientReceivedTcs = new TaskCompletionSource<FileTransferCompleted>();
            using var client = new TcpFileTransferClient();
            client.DownloadDirectory = clientDownloads;
            client.TransferCompleted += (s, c) => clientReceivedTcs.TrySetResult(c);

            var serverClientConnectedTcs = new TaskCompletionSource<string>();
            server.ClientConnected += (s, id) => serverClientConnectedTcs.TrySetResult(id);

            await client.ConnectAsync("127.0.0.1", port);
            await serverClientConnectedTcs.Task;

            bool sendResult = await server.SendFileAsync(sourceFile);
            Assert.IsTrue(sendResult, "El servidor debió enviar el archivo exitosamente.");

            var completedTask = await Task.WhenAny(clientReceivedTcs.Task, Task.Delay(5000));
            Assert.AreEqual(clientReceivedTcs.Task, completedTask, "El cliente debió completar la recepción antes del timeout.");

            var completed = await clientReceivedTcs.Task;
            Assert.IsFalse(completed.IsUpload);
            Assert.IsTrue(File.Exists(completed.FilePath));

            byte[] actualData = await File.ReadAllBytesAsync(completed.FilePath);
            CollectionAssert.AreEqual(expectedData, actualData);

            await client.DisconnectAsync();
            server.Stop();

            try { Directory.Delete(testDir, true); } catch { }
        }

        [TestMethod]
        public async Task FileTransfer_OverMtlsWithSessionAuthority_Succeeds()
        {
            using var hostCert = CreateEcdsaCert("FileHost", TlsConfigurationHelper.ServerAuthOid);
            using var viewerCert = CreateEcdsaCert("FileViewer", TlsConfigurationHelper.ClientAuthOid);

            string hostFp = TlsConfigurationHelper.ComputeFingerprint(hostCert);
            string viewerFp = TlsConfigurationHelper.ComputeFingerprint(viewerCert);

            var store = new InMemoryPairedDeviceStore();
            await store.AddPairedDeviceAsync(new PairedDevice("h1", "Host", hostFp, DateTime.UtcNow, DateTime.UtcNow, false));
            await store.AddPairedDeviceAsync(new PairedDevice("v1", "Viewer", viewerFp, DateTime.UtcNow, DateTime.UtcNow, false));

            var authority = new SessionAuthority(store);
            var (created, session, _) = await authority.TryCreateSessionAsync(viewerFp);
            Assert.IsTrue(created);
            Assert.IsNotNull(session);

            var fileToken = authority.IssueChannelToken(session.SessionId, ChannelType.FileTransfer);
            Assert.IsNotNull(fileToken);

            var serverSslOpts = TlsConfigurationHelper.CreateServerOptions(hostCert, store);
            var clientSslOpts = TlsConfigurationHelper.CreateClientOptions(viewerCert, "localhost", store);

            string testDir = Path.Combine(Path.GetTempPath(), "LssMtlsFileTransfer_" + Guid.NewGuid().ToString("N"));
            string serverDownloads = Path.Combine(testDir, "ServerDownloads");
            Directory.CreateDirectory(testDir);
            Directory.CreateDirectory(serverDownloads);

            string sourceFile = Path.Combine(testDir, "secure_doc.txt");
            byte[] expectedData = new byte[64 * 1024];
            Random.Shared.NextBytes(expectedData);
            await File.WriteAllBytesAsync(sourceFile, expectedData);

            int port = 15403;
            using var server = new TcpFileTransferServer(authority, serverSslOpts);
            server.DownloadDirectory = serverDownloads;
            await server.StartAsync(port);

            var serverCompletedTcs = new TaskCompletionSource<FileTransferCompleted>();
            server.TransferCompleted += (s, c) => serverCompletedTcs.TrySetResult(c);

            using var client = new TcpFileTransferClient();
            await client.ConnectAsync("127.0.0.1", port, clientSslOpts, session.SessionId, fileToken.TokenId);
            Assert.IsTrue(client.IsConnected);

            bool sendResult = await client.SendFileAsync(sourceFile);
            Assert.IsTrue(sendResult);

            var completedTask = await Task.WhenAny(serverCompletedTcs.Task, Task.Delay(5000));
            Assert.AreEqual(serverCompletedTcs.Task, completedTask);

            var completed = await serverCompletedTcs.Task;
            byte[] actualData = await File.ReadAllBytesAsync(completed.FilePath);
            CollectionAssert.AreEqual(expectedData, actualData);

            await client.DisconnectAsync();
            server.Stop();

            try { Directory.Delete(testDir, true); } catch { }
        }
    }
}
