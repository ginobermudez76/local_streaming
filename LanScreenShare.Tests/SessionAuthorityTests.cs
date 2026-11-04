using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LanScreenShare.Core.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

public class InMemoryPairedDeviceStore : IPairedDeviceStore
{
    private readonly ConcurrentDictionary<string, PairedDevice> _devices = new();

    public Task<bool> IsDevicePairedAsync(string fingerprint)
    {
        return Task.FromResult(_devices.ContainsKey(fingerprint));
    }

    public Task<bool> IsDeviceRevokedAsync(string fingerprint)
    {
        if (_devices.TryGetValue(fingerprint, out var device))
        {
            return Task.FromResult(device.IsRevoked);
        }
        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<PairedDevice>> GetAllPairedDevicesAsync()
    {
        IReadOnlyList<PairedDevice> list = _devices.Values.ToList();
        return Task.FromResult(list);
    }

    public Task AddPairedDeviceAsync(PairedDevice device)
    {
        _devices[device.PublicKeyFingerprint] = device;
        return Task.CompletedTask;
    }

    public Task RevokeDeviceAsync(string fingerprint)
    {
        if (_devices.TryGetValue(fingerprint, out var device))
        {
            _devices[fingerprint] = device with { IsRevoked = true };
        }
        return Task.CompletedTask;
    }

    public Task RemoveDeviceAsync(string fingerprint)
    {
        _devices.TryRemove(fingerprint, out _);
        return Task.CompletedTask;
    }
}

[TestClass]
public class SessionAuthorityTests
{
    private InMemoryPairedDeviceStore _store = null!;
    private SessionAuthority _authority = null!;
    private readonly string _trustedFingerprint = "SHA256:112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00";
    private readonly string _untrustedFingerprint = "SHA256:9999999999999999999999999999999999999999999999999999999999999999";

    [TestInitialize]
    public async Task Setup()
    {
        _store = new InMemoryPairedDeviceStore();
        await _store.AddPairedDeviceAsync(new PairedDevice(
            "dev-1",
            "Laptop-Trusted",
            _trustedFingerprint,
            DateTime.UtcNow,
            DateTime.UtcNow,
            false
        ));
        _authority = new SessionAuthority(_store);
    }

    [TestMethod]
    public async Task TryCreateSessionAsync_UntrustedPeer_ReturnsFalse()
    {
        var (success, session, reason) = await _authority.TryCreateSessionAsync(_untrustedFingerprint);
        Assert.IsFalse(success);
        Assert.IsNull(session);
        Assert.IsNotNull(reason);
        Assert.IsTrue(reason.Contains("no está emparejado"));
    }

    [TestMethod]
    public async Task TryCreateSessionAsync_TrustedPeer_ReturnsActiveSession()
    {
        var (success, session, reason) = await _authority.TryCreateSessionAsync(_trustedFingerprint);
        Assert.IsTrue(success);
        Assert.IsNotNull(session);
        Assert.AreEqual(SessionState.Active, session.State);
        Assert.AreEqual(_trustedFingerprint, session.PeerFingerprint);
    }

    [TestMethod]
    public async Task ChannelToken_Attachment_AtomicAndSingleUse()
    {
        var (_, session, _) = await _authority.TryCreateSessionAsync(_trustedFingerprint);
        Assert.IsNotNull(session);

        var token = _authority.IssueChannelToken(session.SessionId, ChannelType.Audio);
        Assert.IsNotNull(token);
        Assert.AreEqual(ChannelType.Audio, token.AllowedChannel);

        // 1. Successful attach
        var status = _authority.TryAttachChannel(token.TokenId, session.SessionId, _trustedFingerprint, ChannelType.Audio);
        Assert.AreEqual(AttachmentStatus.Success, status);

        // 2. Replay of already consumed token fails
        var replayStatus = _authority.TryAttachChannel(token.TokenId, session.SessionId, _trustedFingerprint, ChannelType.Audio);
        Assert.AreEqual(AttachmentStatus.TokenNotFound, replayStatus);
    }

    [TestMethod]
    public async Task ChannelToken_MismatchChannel_DoesNotConsumeToken()
    {
        var (_, session, _) = await _authority.TryCreateSessionAsync(_trustedFingerprint);
        Assert.IsNotNull(session);

        var token = _authority.IssueChannelToken(session.SessionId, ChannelType.Audio);

        // Intenta adjuntar en canal de Control
        var mismatchStatus = _authority.TryAttachChannel(token.TokenId, session.SessionId, _trustedFingerprint, ChannelType.RemoteControl);
        Assert.AreEqual(AttachmentStatus.ChannelMismatch, mismatchStatus);

        // El token NO debe haber sido consumido; ahora se adjunta en Audio correctamente
        var successStatus = _authority.TryAttachChannel(token.TokenId, session.SessionId, _trustedFingerprint, ChannelType.Audio);
        Assert.AreEqual(AttachmentStatus.Success, successStatus);
    }

    [TestMethod]
    public async Task RemoteControl_SingleActiveControllerEnforced()
    {
        // Dos dispositivos emparejados
        string peer2Fingerprint = "SHA256:2222222222222222222222222222222222222222222222222222222222222222";
        await _store.AddPairedDeviceAsync(new PairedDevice("dev-2", "Tablet-Trusted", peer2Fingerprint, DateTime.UtcNow, DateTime.UtcNow, false));

        var (_, session1, _) = await _authority.TryCreateSessionAsync(_trustedFingerprint);
        var (_, session2, _) = await _authority.TryCreateSessionAsync(peer2Fingerprint);

        // 1. Session 1 requests and gets control
        var state1 = _authority.RequestRemoteControl(session1!.SessionId);
        Assert.AreEqual(RemoteControlState.Granted, state1);
        Assert.AreEqual(session1.SessionId, _authority.ActiveControllerSessionId);

        // 2. Session 2 requests control while session 1 holds it -> conflict denied
        var state2 = _authority.RequestRemoteControl(session2!.SessionId);
        Assert.AreEqual(RemoteControlState.DeniedConflict, state2);
        Assert.AreEqual(session1.SessionId, _authority.ActiveControllerSessionId);

        // 3. Session 1 requests again -> already granted
        var state1Again = _authority.RequestRemoteControl(session1.SessionId);
        Assert.AreEqual(RemoteControlState.Granted, state1Again);

        // 4. Session 1 releases control
        _authority.ReleaseRemoteControl(session1.SessionId);
        Assert.IsNull(_authority.ActiveControllerSessionId);

        // 5. Now Session 2 can acquire control
        var state2New = _authority.RequestRemoteControl(session2.SessionId);
        Assert.AreEqual(RemoteControlState.Granted, state2New);
        Assert.AreEqual(session2.SessionId, _authority.ActiveControllerSessionId);
    }

    [TestMethod]
    public async Task CascadeSessionTermination_PurgesTokensAndRevokesControl()
    {
        var (_, session, _) = await _authority.TryCreateSessionAsync(_trustedFingerprint);
        Assert.IsNotNull(session);

        _authority.RequestRemoteControl(session.SessionId);
        var token = _authority.IssueChannelToken(session.SessionId, ChannelType.RemoteControl);

        // Terminar sesión
        _authority.CloseSession(session.SessionId);

        Assert.AreEqual(SessionState.Terminated, session.State);
        Assert.IsNull(_authority.ActiveControllerSessionId);

        // Token fue purgado en cascada
        var status = _authority.TryAttachChannel(token.TokenId, session.SessionId, _trustedFingerprint, ChannelType.RemoteControl);
        Assert.AreEqual(AttachmentStatus.SessionNotFound, status);
    }

    [TestMethod]
    public void ProvisionalPairing_RateLimiting_BlocksAfterThreeFailures()
    {
        string peerIp = "192.168.1.50";
        Assert.IsFalse(_authority.IsPairingBlocked(peerIp));

        _authority.RecordPairingAttempt(peerIp, false);
        _authority.RecordPairingAttempt(peerIp, false);
        Assert.IsFalse(_authority.IsPairingBlocked(peerIp));

        _authority.RecordPairingAttempt(peerIp, false);
        // 3 fallos consecutivos -> Bloqueado
        Assert.IsTrue(_authority.IsPairingBlocked(peerIp));
    }
}
