using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Security
{
    public class SessionAuthority : ISessionAuthority
    {
        private readonly IPairedDeviceStore? _deviceStore;
        private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new();
        private readonly ConcurrentDictionary<string, ChannelAttachmentToken> _attachmentTokens = new();
        private readonly ConcurrentDictionary<string, (int Failures, DateTime WindowStart)> _pairingRateLimits = new();
        private readonly object _controlLock = new();
        private string? _activeControllerSessionId;

        public SessionAuthority(IPairedDeviceStore? deviceStore = null)
        {
            _deviceStore = deviceStore;
        }

        public event EventHandler<string>? SessionTerminated;
        public string? ActiveControllerSessionId => GetActiveControllerSessionId();

        public ActiveSession CreateSession(string peerFingerprint, string peerName, TimeSpan? ttl = null)
        {
            if (string.IsNullOrWhiteSpace(peerFingerprint))
                throw new ArgumentException("Peer fingerprint cannot be null or empty", nameof(peerFingerprint));

            byte[] randomBytes = new byte[16];
            RandomNumberGenerator.Fill(randomBytes);
            string sessionId = new Guid(randomBytes).ToString("D");

            var duration = ttl ?? TimeSpan.FromHours(12);
            var session = new ActiveSession(sessionId, peerFingerprint, peerName, DateTime.UtcNow.Add(duration))
            {
                State = SessionState.Active,
                VideoAuthorized = true,
                AudioAuthorized = true
            };

            _sessions[sessionId] = session;
            return session;
        }

        public async Task<(bool Success, ActiveSession? Session, string? FailureReason)> TryCreateSessionAsync(
            string peerFingerprint,
            string peerName = "RemotePeer",
            TimeSpan? ttl = null)
        {
            if (string.IsNullOrWhiteSpace(peerFingerprint))
                return (false, null, "Peer fingerprint cannot be null or empty.");

            if (_deviceStore != null)
            {
                bool isPaired = await _deviceStore.IsDevicePairedAsync(peerFingerprint);
                if (!isPaired)
                {
                    return (false, null, "El dispositivo no está emparejado.");
                }

                bool isRevoked = await _deviceStore.IsDeviceRevokedAsync(peerFingerprint);
                if (isRevoked)
                {
                    return (false, null, "El dispositivo ha sido revocado.");
                }
            }

            var session = CreateSession(peerFingerprint, peerName, ttl);
            return (true, session, null);
        }

        public ActiveSession? GetSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                if (session.IsExpired)
                {
                    TerminateSession(sessionId);
                    return null;
                }
                return session;
            }
            return null;
        }

        public ChannelAttachmentToken IssueChannelToken(string sessionId, ChannelType channel, TimeSpan? validFor = null)
        {
            var session = GetSession(sessionId);
            if (session == null || session.State != SessionState.Active)
                throw new InvalidOperationException($"Cannot issue channel token for inactive or non-existent session: {sessionId}");

            byte[] tokenBytes = new byte[16];
            RandomNumberGenerator.Fill(tokenBytes);
            string tokenId = Convert.ToHexString(tokenBytes).ToLowerInvariant();

            var expiration = DateTime.UtcNow.Add(validFor ?? TimeSpan.FromHours(24));
            var token = new ChannelAttachmentToken(tokenId, sessionId, session.PeerFingerprint, channel, expiration);

            _attachmentTokens[tokenId] = token;
            return token;
        }

        public AttachmentStatus TryAttachChannel(ChannelAttachmentRequest request)
        {
            var session = GetSession(request.SessionId);
            if (session == null || session.State != SessionState.Active)
            {
                return AttachmentStatus.SessionNotFound;
            }

            if (!_attachmentTokens.TryGetValue(request.TokenId, out var token))
            {
                return AttachmentStatus.TokenNotFound;
            }

            var validation = ValidateTokenMatch(token, request);
            if (validation != AttachmentStatus.Success)
            {
                return validation;
            }

            if (DateTime.UtcNow > token.ExpiresAt)
            {
                _attachmentTokens.TryRemove(request.TokenId, out _);
                return AttachmentStatus.TokenExpired;
            }

            // Consumo atómico: sólo la primera operación exitosa lo retira
            if (!_attachmentTokens.TryRemove(request.TokenId, out _))
            {
                return AttachmentStatus.TokenAlreadyConsumed;
            }

            lock (session.AttachedChannels)
            {
                session.AttachedChannels.Add(request.Channel);
            }

            return AttachmentStatus.Success;
        }

        public AttachmentStatus TryAttachChannel(string tokenId, string sessionId, string peerFingerprint, ChannelType channel) =>
            TryAttachChannel(new ChannelAttachmentRequest(tokenId, sessionId, peerFingerprint, channel));

        public bool TryAttachChannel(ChannelAttachmentRequest request, out string? failureReason)
        {
            var status = TryAttachChannel(request);
            if (status == AttachmentStatus.Success)
            {
                failureReason = null;
                return true;
            }

            failureReason = status.ToString();
            return false;
        }

        private static AttachmentStatus ValidateTokenMatch(ChannelAttachmentToken token, ChannelAttachmentRequest request)
        {
            if (token.SessionId != request.SessionId || token.PeerFingerprint != request.PeerFingerprint)
            {
                return AttachmentStatus.TokenBindingMismatch;
            }

            if (token.AllowedChannel != request.Channel)
            {
                // Preservar el token si el tipo de canal no coincide para evitar ataques de invalidación maliciosa
                return AttachmentStatus.ChannelMismatch;
            }

            return AttachmentStatus.Success;
        }

        public RemoteControlState RequestRemoteControl(string sessionId)
        {
            lock (_controlLock)
            {
                if (IsConflictingControllerActive(sessionId))
                {
                    return RemoteControlState.DeniedConflict;
                }

                var session = GetSession(sessionId);
                if (session == null || session.State != SessionState.Active)
                {
                    return RemoteControlState.Disabled;
                }

                _activeControllerSessionId = sessionId;
                session.ControlState = RemoteControlState.Granted;
                return RemoteControlState.Granted;
            }
        }

        private bool IsConflictingControllerActive(string currentSessionId)
        {
            if (_activeControllerSessionId == null || _activeControllerSessionId == currentSessionId)
            {
                return false;
            }

            var existingSession = GetSession(_activeControllerSessionId);
            if (existingSession is { State: SessionState.Active, ControlState: RemoteControlState.Granted })
            {
                return true;
            }

            _activeControllerSessionId = null;
            return false;
        }

        public bool TryAcquireControl(string sessionId, out string? failureReason)
        {
            var result = RequestRemoteControl(sessionId);
            if (result == RemoteControlState.Granted)
            {
                failureReason = null;
                return true;
            }

            failureReason = result.ToString();
            return false;
        }

        public void ReleaseRemoteControl(string sessionId) => SetControlState(sessionId, RemoteControlState.Disabled);

        public void RevokeControl(string sessionId) => SetControlState(sessionId, RemoteControlState.Revoked);

        private void SetControlState(string sessionId, RemoteControlState targetState)
        {
            lock (_controlLock)
            {
                if (_activeControllerSessionId == sessionId)
                {
                    _activeControllerSessionId = null;
                }

                if (_sessions.TryGetValue(sessionId, out var session))
                {
                    session.ControlState = targetState;
                }
            }
        }

        public string? GetActiveControllerSessionId()
        {
            lock (_controlLock)
            {
                return _activeControllerSessionId;
            }
        }

        public void CloseSession(string sessionId)
        {
            TerminateSession(sessionId);
        }

        public void TerminateSession(string sessionId)
        {
            lock (_controlLock)
            {
                if (_activeControllerSessionId == sessionId)
                {
                    _activeControllerSessionId = null;
                }
            }

            if (_sessions.TryRemove(sessionId, out var session))
            {
                session.State = SessionState.Terminated;
                session.ControlState = RemoteControlState.Revoked;
            }

            // Purgar en cascada todos los tokens emitidos para esta sesión
            foreach (var kvp in _attachmentTokens)
            {
                if (kvp.Value.SessionId == sessionId)
                {
                    _attachmentTokens.TryRemove(kvp.Key, out _);
                }
            }

            SessionTerminated?.Invoke(this, sessionId);
        }

        public void PruneExpired()
        {
            var now = DateTime.UtcNow;
            PruneExpiredTokens(now);
            PruneExpiredSessions(now);
        }

        private void PruneExpiredTokens(DateTime now)
        {
            foreach (var (key, token) in _attachmentTokens)
            {
                if (now > token.ExpiresAt)
                {
                    _attachmentTokens.TryRemove(key, out _);
                }
            }
        }

        private void PruneExpiredSessions(DateTime now)
        {
            foreach (var (key, session) in _sessions)
            {
                if (now > session.ExpiresAt)
                {
                    TerminateSession(key);
                }
            }
        }

        public bool IsPairingBlocked(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress)) return false;

            if (_pairingRateLimits.TryGetValue(ipAddress, out var record))
            {
                if (DateTime.UtcNow - record.WindowStart > TimeSpan.FromMinutes(10))
                {
                    _pairingRateLimits.TryRemove(ipAddress, out _);
                    return false;
                }

                return record.Failures >= 3;
            }

            return false;
        }

        public void RecordPairingAttempt(string ipAddress, bool success)
        {
            if (string.IsNullOrWhiteSpace(ipAddress)) return;

            if (success)
            {
                _pairingRateLimits.TryRemove(ipAddress, out _);
                return;
            }

            _pairingRateLimits.AddOrUpdate(ipAddress,
                _ => (1, DateTime.UtcNow),
                (_, current) =>
                {
                    if (DateTime.UtcNow - current.WindowStart > TimeSpan.FromMinutes(10))
                    {
                        return (1, DateTime.UtcNow);
                    }
                    return (current.Failures + 1, current.WindowStart);
                });
        }
    }
}
