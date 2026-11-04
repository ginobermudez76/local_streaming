using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
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

namespace LanScreenShare.Tests;

public class MockInputSimulatorService : IInputSimulatorService
{
    public RemoteInputMessage? LastInput { get; private set; }
    public bool InjectedInputsReleased { get; private set; }

    public void SimulateInput(RemoteInputMessage input)
    {
        LastInput = input;
    }

    public void SetTargetSource(CaptureSource source) { }

    public void ReleaseInjectedInputs()
    {
        InjectedInputsReleased = true;
    }

    public void Dispose()
    {
        ReleaseInjectedInputs();
    }
}

[TestClass]
public class ThreeChannelFullMtlsLifecycleTests
{
    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

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
        byte[] pfx = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }

    [TestMethod]
    public async Task CompleteThreeChannelLifecycle_mTLS_Tokens_AndCascadeInvalidation()
    {
        using var hostCert = CreateEcdsaCertificate("HostNode", TlsConfigurationHelper.ServerAuthOid);
        using var viewerCert = CreateEcdsaCertificate("ViewerNode", TlsConfigurationHelper.ClientAuthOid);

        string hostFingerprint = TlsConfigurationHelper.ComputeFingerprint(hostCert);
        string viewerFingerprint = TlsConfigurationHelper.ComputeFingerprint(viewerCert);

        var deviceStore = new InMemoryPairedDeviceStore();
        await deviceStore.AddPairedDeviceAsync(new PairedDevice("host-1", "Host", hostFingerprint, DateTime.UtcNow, DateTime.UtcNow, false));
        await deviceStore.AddPairedDeviceAsync(new PairedDevice("viewer-1", "Viewer", viewerFingerprint, DateTime.UtcNow, DateTime.UtcNow, false));

        var sessionAuthority = new SessionAuthority(deviceStore);
        var inputSimulator = new MockInputSimulatorService();

        var serverOptions = TlsConfigurationHelper.CreateServerOptions(hostCert, deviceStore);
        var clientOptions = TlsConfigurationHelper.CreateClientOptions(viewerCert, "localhost", deviceStore);

        int streamPort = GetAvailablePort();
        int audioPort = GetAvailablePort();
        int controlPort = GetAvailablePort();

        using var streamServer = new TcpStreamServer(sessionAuthority, serverOptions);
        using var audioServer = new TcpAudioServer(sessionAuthority, serverOptions);
        using var controlServer = new TcpControlServer(sessionAuthority, inputSimulator, serverOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = cts.Token;

        await streamServer.StartAsync(streamPort, token);
        await audioServer.StartAsync(audioPort, token);
        await controlServer.StartAsync(controlPort, token);

        // 1. Cliente conecta al canal primario (5001) con mTLS
        using var streamClient = new TcpStreamClient();
        CapturedFrame? receivedFrame = null;
        var frameReceivedEvent = new ManualResetEventSlim(false);
        streamClient.FrameReceived += (s, f) =>
        {
            receivedFrame = f;
            frameReceivedEvent.Set();
        };

        await streamClient.ConnectAsync("127.0.0.1", streamPort, clientOptions, token);
        Assert.IsTrue(streamClient.IsConnected);
        Assert.IsNotNull(streamClient.ActiveSessionHandshake);
        Assert.AreEqual("SESSION_OK", streamClient.ActiveSessionHandshake.Type);

        string sessionId = streamClient.ActiveSessionHandshake.SessionId;
        string audioToken = streamClient.ActiveSessionHandshake.AudioToken;
        string controlToken = streamClient.ActiveSessionHandshake.ControlToken;

        Assert.IsFalse(string.IsNullOrEmpty(sessionId));
        Assert.IsFalse(string.IsNullOrEmpty(audioToken));
        Assert.IsFalse(string.IsNullOrEmpty(controlToken));

        // 2. Cliente conecta al canal de Audio (5002) con el token de Audio
        using var audioClient = new TcpAudioClient();
        CapturedAudioChunk? receivedAudio = null;
        var audioReceivedEvent = new ManualResetEventSlim(false);
        audioClient.AudioReceived += (s, a) =>
        {
            receivedAudio = a;
            audioReceivedEvent.Set();
        };

        await audioClient.ConnectAsync("127.0.0.1", audioPort, clientOptions, sessionId, audioToken, token);
        Assert.IsTrue(audioClient.IsConnected);

        // 3. Cliente conecta al canal de Control (5003) con el token de Control
        using var controlClient = new TcpControlClient();
        await controlClient.ConnectAsync("127.0.0.1", controlPort, clientOptions, sessionId, controlToken, token);
        Assert.IsTrue(controlClient.IsConnected);

        // Otorgar control remoto a la sesión para permitir inyección
        sessionAuthority.RequestRemoteControl(sessionId);
        Assert.AreEqual(sessionId, sessionAuthority.ActiveControllerSessionId);

        // 4. Enviar entrada de control LSI2 (28 bytes)
        var testInput = new RemoteInputMessage
        {
            Type = InputEventType.MouseDown,
            Button = RemoteMouseButton.Left,
            NormalizedX = 0.5f,
            NormalizedY = 0.5f
        };
        await controlClient.SendInputAsync(testInput);

        // Pequeño retardo para procesamiento asíncrono
        await Task.Delay(100, token);
        Assert.IsNotNull(inputSimulator.LastInput);
        Assert.AreEqual(InputEventType.MouseDown, inputSimulator.LastInput.Type);

        // 5. Broadcast de frame de vídeo
        var testFrame = new CapturedFrame
        {
            Data = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02 },
            Width = 1920,
            Height = 1080,
            Format = "jpeg"
        };
        await streamServer.BroadcastFrameAsync(testFrame);
        Assert.IsTrue(frameReceivedEvent.Wait(TimeSpan.FromSeconds(3)));
        Assert.IsNotNull(receivedFrame);
        Assert.AreEqual(1920, receivedFrame.Width);

        // 6. Broadcast de audio
        var testAudio = new CapturedAudioChunk
        {
            Data = new byte[] { 0x01, 0x02, 0x03, 0x04 },
            SampleRate = 48000,
            Channels = 2,
            BitsPerSample = 16,
            AudioFormat = 1
        };
        await audioServer.BroadcastAudioAsync(testAudio);
        Assert.IsTrue(audioReceivedEvent.Wait(TimeSpan.FromSeconds(3)));
        Assert.IsNotNull(receivedAudio);
        Assert.AreEqual(48000, receivedAudio.SampleRate);

        // 7. Desconexión del canal primario (5001) -> debe desencadenar invalidación en cascada
        await streamClient.DisconnectAsync();
        await Task.Delay(150, token);

        // La sesión en SessionAuthority debe quedar terminada
        var closedSession = sessionAuthority.GetSession(sessionId);
        Assert.IsNull(closedSession); // Al estar terminada o expirada GetSession retorna null

        // Los inputs deben haber sido liberados limpiamente sin teclas pegadas
        Assert.IsTrue(inputSimulator.InjectedInputsReleased);

        // Limpiar recursos
        streamServer.Stop();
        audioServer.Stop();
        controlServer.Stop();
    }
}
