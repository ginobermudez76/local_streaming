using System;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class ChannelAttachmentMessageTests
{
    [TestMethod]
    public void SerializeAndDeserialize_AudioChannel_RoundTripSuccess()
    {
        string sessionId = Guid.NewGuid().ToString("D");
        string tokenId = "0123456789abcdef0123456789abcdef"; // 32 hex chars = 16 bytes
        var channel = ChannelType.Audio;

        byte[] serialized = ChannelAttachmentMessage.Serialize(sessionId, tokenId, channel);
        Assert.AreEqual(ChannelAttachmentMessage.MessageSize, serialized.Length);

        bool success = ChannelAttachmentMessage.TryDeserialize(
            serialized,
            out var payload,
            out string? error);

        Assert.IsTrue(success);
        Assert.IsNull(error);
        Assert.AreEqual(sessionId, payload.SessionId);
        Assert.AreEqual(tokenId, payload.TokenId);
        Assert.AreEqual(ChannelType.Audio, payload.Channel);
    }

    [TestMethod]
    public void SerializeAndDeserialize_RemoteControlChannel_RoundTripSuccess()
    {
        string sessionId = Guid.NewGuid().ToString("D");
        string tokenId = "abcdef0123456789abcdef0123456789";
        var channel = ChannelType.RemoteControl;

        byte[] serialized = ChannelAttachmentMessage.Serialize(sessionId, tokenId, channel);

        bool success = ChannelAttachmentMessage.TryDeserialize(
            serialized,
            out var payload,
            out string? error);

        Assert.IsTrue(success);
        Assert.IsNull(error);
        Assert.AreEqual(sessionId, payload.SessionId);
        Assert.AreEqual(tokenId, payload.TokenId);
        Assert.AreEqual(ChannelType.RemoteControl, payload.Channel);
    }

    [TestMethod]
    public void TruncatedData_ReturnsError()
    {
        byte[] buffer = new byte[36]; // 1 byte less than required 37

        bool success = ChannelAttachmentMessage.TryDeserialize(
            buffer,
            out _,
            out string? error);

        Assert.IsFalse(success);
        Assert.IsTrue(error!.Contains("too short"));
    }

    [TestMethod]
    public void InvalidMagic_ReturnsError()
    {
        byte[] buffer = new byte[37];
        buffer[0] = 0xFF;

        bool success = ChannelAttachmentMessage.TryDeserialize(
            buffer,
            out _,
            out string? error);

        Assert.IsFalse(success);
        Assert.IsTrue(error!.Contains("Invalid attachment magic"));
    }
}
