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

    private readonly record struct ServerTestContext(
        TcpListener Listener,
        SslServerAuthenticationOptions Options,
        SessionAuthority Authority,
        string ViewerFingerprint,
        CancellationToken Token
    );

    private readonly record struct ClientTestContext(
        int Port,
        SslClientAuthenticationOptions Options,
        CancellationToken Token
    );

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

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;

        var serverOptions = TlsConfigurationHelper.CreateServerOptions(hostCert, deviceStore);
        var serverCtx = new ServerTestContext(listener, serverOptions, sessionAuthority, viewerFingerprint, token);
        var serverTask = RunServerLifecycleAsync(serverCtx);

        var clientOptions = TlsConfigurationHelper.CreateClientOptions(viewerCert, "localhost", deviceStore);
        var clientCtx = new ClientTestContext(port, clientOptions, token);
        await RunClientLifecycleAsync(clientCtx, sessionAuthority, viewerFingerprint);

        await serverTask;
        listener.Stop();
    }

    private static async Task<ChannelAttachmentToken> RunServerLifecycleAsync(ServerTestContext ctx)
    {
        using var serverTcp = await ctx.Listener.AcceptTcpClientAsync(ctx.Token);
        using var serverSsl = new SslStream(serverTcp.GetStream(), false);
        await serverSsl.AuthenticateAsServerAsync(ctx.Options, ctx.Token);

        Assert.IsTrue(serverSsl.IsEncrypted);
        Assert.IsTrue(serverSsl.IsMutuallyAuthenticated);
        Assert.AreEqual(SslProtocols.Tls13, serverSsl.SslProtocol);

        var (success, session, _) = await ctx.Authority.TryCreateSessionAsync(ctx.ViewerFingerprint);
        Assert.IsTrue(success);
        var tokenIssued = ctx.Authority.IssueChannelToken(session!.SessionId, ChannelType.RemoteControl);

        await HandleSecondaryControlChannelAsync(ctx);
        return tokenIssued;
    }

    private static async Task HandleSecondaryControlChannelAsync(ServerTestContext ctx)
    {
        using var serverTcpControl = await ctx.Listener.AcceptTcpClientAsync(ctx.Token);
        using var serverSslControl = new SslStream(serverTcpControl.GetStream(), false);
        await serverSslControl.AuthenticateAsServerAsync(ctx.Options, ctx.Token);

        byte[] attachBuffer = new byte[ChannelAttachmentMessage.MessageSize];
        await ReadFullBufferAsync(serverSslControl, attachBuffer, ctx.Token);

        bool attachParsed = ChannelAttachmentMessage.TryDeserialize(attachBuffer, out var attachPayload, out var attachError);
        Assert.IsTrue(attachParsed);
        Assert.IsNull(attachError);

        var attachStatus = ctx.Authority.TryAttachChannel(attachPayload.TokenId, attachPayload.SessionId, ctx.ViewerFingerprint, attachPayload.Channel);
        Assert.AreEqual(AttachmentStatus.Success, attachStatus);
        await serverSslControl.WriteAsync(new byte[] { (byte)attachStatus }, ctx.Token);

        byte[] lsi2Buffer = new byte[Lsi2ControlPacket.PacketSize];
        await ReadFullBufferAsync(serverSslControl, lsi2Buffer, ctx.Token);

        bool lsi2Parsed = Lsi2ControlPacket.TryReadPacket(lsi2Buffer, out var lsi2Header, out var inputMsg, out var lsi2Error);
        Assert.IsTrue(lsi2Parsed);
        Assert.IsNull(lsi2Error);
        Assert.AreEqual(1UL, lsi2Header.SequenceNumber);
        Assert.AreEqual(InputEventType.MouseDown, inputMsg!.Type);
        Assert.AreEqual(RemoteMouseButton.Left, inputMsg.Button);
    }

    private static async Task RunClientLifecycleAsync(
        ClientTestContext ctx,
        SessionAuthority sessionAuthority,
        string viewerFingerprint)
    {
        using var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync(IPAddress.Loopback, ctx.Port, ctx.Token);
        using var clientSsl = new SslStream(clientTcp.GetStream(), false);
        await clientSsl.AuthenticateAsClientAsync(ctx.Options, ctx.Token);

        Assert.IsTrue(clientSsl.IsEncrypted);
        Assert.IsTrue(clientSsl.IsMutuallyAuthenticated);
        Assert.AreEqual(SslProtocols.Tls13, clientSsl.SslProtocol);

        var session = (await sessionAuthority.TryCreateSessionAsync(viewerFingerprint)).Session;
        var tokenForControl = sessionAuthority.IssueChannelToken(session!.SessionId, ChannelType.RemoteControl);

        await RunClientSecondaryChannelAsync(ctx, session.SessionId, tokenForControl.TokenId);
    }

    private static async Task RunClientSecondaryChannelAsync(
        ClientTestContext ctx,
        string sessionId,
        string tokenId)
    {
        using var clientTcpControl = new TcpClient();
        await clientTcpControl.ConnectAsync(IPAddress.Loopback, ctx.Port, ctx.Token);
        using var clientSslControl = new SslStream(clientTcpControl.GetStream(), false);
        await clientSslControl.AuthenticateAsClientAsync(ctx.Options, ctx.Token);

        byte[] attachBytes = ChannelAttachmentMessage.Serialize(sessionId, tokenId, ChannelType.RemoteControl);
        await clientSslControl.WriteAsync(attachBytes, ctx.Token);

        byte[] statusResponse = new byte[1];
        int statusRead = await clientSslControl.ReadAsync(statusResponse.AsMemory(0, 1), ctx.Token);
        Assert.AreEqual(1, statusRead);
        Assert.AreEqual((byte)AttachmentStatus.Success, statusResponse[0]);

        byte[] packetBuffer = new byte[Lsi2ControlPacket.PacketSize];
        var inputToSend = new RemoteInputMessage
        {
            Type = InputEventType.MouseDown,
            Button = RemoteMouseButton.Left,
            NormalizedX = 0.25f,
            NormalizedY = 0.75f
        };
        Lsi2ControlPacket.WritePacket(packetBuffer, 1, 99999, inputToSend);
        await clientSslControl.WriteAsync(packetBuffer, ctx.Token);
    }

    private static async Task ReadFullBufferAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int r = await stream.ReadAsync(buffer.Slice(read), token);
            if (r == 0) break;
            read += r;
        }
    }

    [TestMethod]
    public async Task ProvisionalPairing_WhenNotPrePaired_TriggersCallback()
    {
        using var hostCert = CreateEcdsaCertificate("TestHost", TlsConfigurationHelper.ServerAuthOid);
        using var viewerCert = CreateEcdsaCertificate("TestViewer", TlsConfigurationHelper.ClientAuthOid);

        var deviceStore = new InMemoryPairedDeviceStore(); // Vacio: no emparejado previamente

        bool serverCallbackInvoked = false;
        bool clientCallbackInvoked = false;

        var serverOptions = TlsConfigurationHelper.CreateServerOptions(hostCert, deviceStore, (fingerprint, cert) =>
        {
            serverCallbackInvoked = true;
            return true; // Simular aprobación
        });

        var clientOptions = TlsConfigurationHelper.CreateClientOptions(viewerCert, "LanScreenShareHost", deviceStore, (fingerprint, cert) =>
        {
            clientCallbackInvoked = true;
            return true;
        });

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = cts.Token;

        var serverTask = Task.Run(async () =>
        {
            using var serverTcp = await listener.AcceptTcpClientAsync(token);
            using var serverSsl = new SslStream(serverTcp.GetStream(), false);
            await serverSsl.AuthenticateAsServerAsync(serverOptions, token);
            Assert.IsNotNull(serverSsl.RemoteCertificate);
        });

        using var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync(IPAddress.Loopback, port, token);
        using var clientSsl = new SslStream(clientTcp.GetStream(), false);
        await clientSsl.AuthenticateAsClientAsync(clientOptions, token);

        await serverTask;
        listener.Stop();

        Assert.IsTrue(serverCallbackInvoked);
        Assert.IsTrue(clientCallbackInvoked);
    }
}


