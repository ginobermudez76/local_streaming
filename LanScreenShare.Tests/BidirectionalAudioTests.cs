using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Network.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests
{
    [TestClass]
    public class BidirectionalAudioTests
    {
        [TestMethod]
        public async Task ViewerMicrophone_TransmitsFromClientToServerSuccessfully()
        {
            using var server = new TcpAudioServer();
            int port = 15002;
            await server.StartAsync(port);

            var tcsReceived = new TaskCompletionSource<CapturedAudioChunk>();
            server.ClientAudioReceived += (s, chunk) =>
            {
                tcsReceived.TrySetResult(chunk);
            };

            using var client = new TcpAudioClient();
            await client.ConnectAsync("127.0.0.1", port);

            Assert.IsTrue(client.IsConnected, "El cliente de audio debe conectarse al servidor.");

            byte[] dummyPcm = new byte[128];
            Array.Fill(dummyPcm, (byte)0x7F);

            var sendChunk = new CapturedAudioChunk
            {
                Data = dummyPcm,
                SampleRate = 48000,
                Channels = 2,
                BitsPerSample = 16,
                AudioFormat = 1,
                Source = AudioSourceType.ViewerMicrophone,
                Timestamp = 123456789L
            };

            await client.SendAudioChunkAsync(sendChunk);

            var completedTask = await Task.WhenAny(tcsReceived.Task, Task.Delay(3000));
            Assert.AreEqual(tcsReceived.Task, completedTask, "El servidor de audio debió recibir el paquete de micrófono del visor.");

            var receivedChunk = await tcsReceived.Task;
            Assert.IsNotNull(receivedChunk);
            Assert.AreEqual(AudioSourceType.ViewerMicrophone, receivedChunk.Source);
            Assert.AreEqual(sendChunk.SampleRate, receivedChunk.SampleRate);
            Assert.AreEqual(sendChunk.Channels, receivedChunk.Channels);
            Assert.AreEqual(sendChunk.Timestamp, receivedChunk.Timestamp);
            CollectionAssert.AreEqual(sendChunk.Data, receivedChunk.Data);

            await client.DisconnectAsync();
            server.Stop();
        }
    }
}
