using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class LoopbackMtlsIntegrationTests
{
    private static X509Certificate2 CreateEcdsaCertificate(string cn, string ekuOid)
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
        // Recargar con flag Exportable en memoria para compatibilidad de SslStream en Windows
        byte[] pfx = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }

    [TestMethod]
    public async Task CompleteMtlsAndChannelAttachmentLifecycle_Succeeds()
    {
        using var hostCert = CreateEcdsaCertificate("TestHost", TlsConfigurationHelper.ServerAuthOid);
        using var viewerCert = CreateEcdsaCertificate("TestViewer", TlsConfigurationHelper.ClientAuthOid);

        string hostFingerprint = TlsConfigurationHelper.ComputeFingerprint(hostCert);
        string viewerFingerprint = TlsConfigurationHelper.ComputeFingerprint(viewerCert);

        var deviceStore = new InMemoryPairedDeviceStore();
        await deviceStore.AddPairedDeviceAsync(new PairedDevice("d1", "HostDev", hostFingerprint, DateTime.UtcNow, DateTime.UtcNow, false));
        await deviceStore.AddPairedDeviceAsync(new PairedDevice("d2", "ViewerDev", viewerFingerprint, DateTime.UtcNow, DateTime.UtcNow, false));

        var sessionAuthority = new SessionAuthority(deviceStore);

        // 1. Iniciar listener de prueba en puerto efímero
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;

        var serverTask = Task.Run(async () =>
        {
            // Aceptar conexión de canal primario (5001)
            using var serverTcp = await listener.AcceptTcpClientAsync(token);
            using var serverSsl = new SslStream(serverTcp.GetStream(), false);

            var serverOptions = TlsConfigurationHelper.CreateServerOptions(hostCert, deviceStore);
            await serverSsl.AuthenticateAsServerAsync(serverOptions, token);

            Assert.IsTrue(serverSsl.IsEncrypted);
            Assert.IsTrue(serverSsl.IsMutuallyAuthenticated);
            Assert.AreEqual(SslProtocols.Tls13, serverSsl.SslProtocol);

            // Crear sesión en SessionAuthority
            var (success, session, _) = await sessionAuthority.TryCreateSessionAsync(viewerFingerprint);
            Assert.IsTrue(success);
            Assert.IsNotNull(session);

            // Emitir token para canal de control
            var tokenIssued = sessionAuthority.IssueChannelToken(session.SessionId, ChannelType.RemoteControl);

            // Aceptar conexión de canal secundario (5003 - Control)
            using var serverTcpControl = await listener.AcceptTcpClientAsync(token);
            using var serverSslControl = new SslStream(serverTcpControl.GetStream(), false);
            await serverSslControl.AuthenticateAsServerAsync(serverOptions, token);

            // Leer mensaje de CHANNEL_ATTACH (37 bytes)
            byte[] attachBuffer = new byte[ChannelAttachmentMessage.MessageSize];
            int read = 0;
            while (read < attachBuffer.Length)
            {
                int r = await serverSslControl.ReadAsync(attachBuffer.AsMemory(read, attachBuffer.Length - read), token);
                if (r == 0) break;
                read += r;
            }

            bool attachParsed = ChannelAttachmentMessage.TryDeserialize(
                attachBuffer,
                out string attachSessionId,
                out string attachTokenId,
                out var attachChannel,
                out string? attachError);

            Assert.IsTrue(attachParsed);
            Assert.IsNull(attachError);

            var attachStatus = sessionAuthority.TryAttachChannel(attachTokenId, attachSessionId, viewerFingerprint, attachChannel);
            Assert.AreEqual(AttachmentStatus.Success, attachStatus);

            // Responder status 1 byte (0x00 = Success)
            await serverSslControl.WriteAsync(new byte[] { (byte)attachStatus }, token);

            // Leer paquete de control LSI2 (28 bytes)
            byte[] lsi2Buffer = new byte[Lsi2ControlPacket.PacketSize];
            read = 0;
            while (read < lsi2Buffer.Length)
            {
                int r = await serverSslControl.ReadAsync(lsi2Buffer.AsMemory(read, lsi2Buffer.Length - read), token);
                if (r == 0) break;
                read += r;
            }

            bool lsi2Parsed = Lsi2ControlPacket.TryReadPacket(
                lsi2Buffer,
                out ulong seq,
                out _,
                out var inputMsg,
                out string? lsi2Error);

            Assert.IsTrue(lsi2Parsed);
            Assert.IsNull(lsi2Error);
            Assert.AreEqual(1UL, seq);
            Assert.AreEqual(InputEventType.MouseDown, inputMsg!.Type);
            Assert.AreEqual(RemoteMouseButton.Left, inputMsg.Button);

            return tokenIssued;
        }, token);

        // Cliente conecta canal primario
        using var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync(IPAddress.Loopback, port, token);
        using var clientSsl = new SslStream(clientTcp.GetStream(), false);

        var clientOptions = TlsConfigurationHelper.CreateClientOptions(viewerCert, "localhost", deviceStore);
        await clientSsl.AuthenticateAsClientAsync(clientOptions, token);

        Assert.IsTrue(clientSsl.IsEncrypted);
        Assert.IsTrue(clientSsl.IsMutuallyAuthenticated);
        Assert.AreEqual(SslProtocols.Tls13, clientSsl.SslProtocol);

        // Esperar a que el servidor cree la sesión y emita el token
        var session = (await sessionAuthority.TryCreateSessionAsync(viewerFingerprint)).Session;
        var tokenForControl = sessionAuthority.IssueChannelToken(session!.SessionId, ChannelType.RemoteControl);

        // Cliente conecta canal secundario (Control)
        using var clientTcpControl = new TcpClient();
        await clientTcpControl.ConnectAsync(IPAddress.Loopback, port, token);
        using var clientSslControl = new SslStream(clientTcpControl.GetStream(), false);
        await clientSslControl.AuthenticateAsClientAsync(clientOptions, token);

        // Enviar CHANNEL_ATTACH
        byte[] attachBytes = ChannelAttachmentMessage.Serialize(session.SessionId, tokenForControl.TokenId, ChannelType.RemoteControl);
        await clientSslControl.WriteAsync(attachBytes, token);

        // Leer respuesta de status (1 byte)
        byte[] statusResponse = new byte[1];
        int statusRead = await clientSslControl.ReadAsync(statusResponse.AsMemory(0, 1), token);
        Assert.AreEqual(1, statusRead);
        Assert.AreEqual((byte)AttachmentStatus.Success, statusResponse[0]);

        // Enviar paquete LSI2 (28 bytes)
        byte[] packetBuffer = new byte[Lsi2ControlPacket.PacketSize];
        var inputToSend = new RemoteInputMessage
        {
            Type = InputEventType.MouseDown,
            Button = RemoteMouseButton.Left,
            NormalizedX = 0.25f,
            NormalizedY = 0.75f
        };
        Lsi2ControlPacket.WritePacket(packetBuffer, 1, 99999, inputToSend);
        await clientSslControl.WriteAsync(packetBuffer, token);

        await serverTask;
        listener.Stop();
    }
}
