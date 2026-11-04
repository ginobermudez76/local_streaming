using System;
using LanScreenShare.Core.Models;
using LanScreenShare.Network.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class Lsi2ControlPacketTests
{
    [TestMethod]
    public void MouseMove_RoundTrip_PreservesFields()
    {
        var input = new RemoteInputMessage
        {
            Type = InputEventType.MouseMove,
            NormalizedX = 0.5432f,
            NormalizedY = 0.8765f
        };

        byte[] buffer = new byte[Lsi2ControlPacket.PacketSize];
        ulong seq = 42;
        ulong ts = 1234567890;

        Lsi2ControlPacket.WritePacket(buffer, seq, ts, input);

        bool parsed = Lsi2ControlPacket.TryReadPacket(
            buffer,
            out ulong readSeq,
            out ulong readTs,
            out var readInput,
            out string? error);

        Assert.IsTrue(parsed);
        Assert.IsNull(error);
        Assert.AreEqual(seq, readSeq);
        Assert.AreEqual(ts, readTs);
        Assert.IsNotNull(readInput);
        Assert.AreEqual(InputEventType.MouseMove, readInput.Type);
        Assert.AreEqual(input.NormalizedX, readInput.NormalizedX, 0.001f);
        Assert.AreEqual(input.NormalizedY, readInput.NormalizedY, 0.001f);
    }

    [TestMethod]
    public void KeyDown_RoundTrip_PreservesKeyCode()
    {
        var input = new RemoteInputMessage
        {
            Type = InputEventType.KeyDown,
            KeyCode = 0x41 // 'A'
        };

        byte[] buffer = new byte[Lsi2ControlPacket.PacketSize];
        Lsi2ControlPacket.WritePacket(buffer, 100, 200, input);

        bool parsed = Lsi2ControlPacket.TryReadPacket(
            buffer,
            out ulong readSeq,
            out ulong readTs,
            out var readInput,
            out string? error);

        Assert.IsTrue(parsed);
        Assert.AreEqual(100UL, readSeq);
        Assert.AreEqual(0x41, readInput!.KeyCode);
    }

    [TestMethod]
    public void LegacyLsi1Magic_StrictlyRejectedWithoutFallback()
    {
        byte[] buffer = new byte[28];
        // Escribir magic legacy LSI1: 0x4C534931 en Big-Endian
        buffer[0] = 0x4C;
        buffer[1] = 0x53;
        buffer[2] = 0x49;
        buffer[3] = 0x31;

        bool parsed = Lsi2ControlPacket.TryReadPacket(
            buffer,
            out _,
            out _,
            out _,
            out string? error);

        Assert.IsFalse(parsed);
        Assert.IsNotNull(error);
        Assert.IsTrue(error.Contains("BadProtocolVersion"));
        Assert.IsTrue(error.Contains("LSI1 legacy"));
    }

    [TestMethod]
    public void InvalidMagic_ReturnsError()
    {
        byte[] buffer = new byte[28];
        buffer[0] = 0xFF;

        bool parsed = Lsi2ControlPacket.TryReadPacket(
            buffer,
            out _,
            out _,
            out _,
            out string? error);

        Assert.IsFalse(parsed);
        Assert.IsTrue(error!.Contains("InvalidMagic"));
    }
}

