using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Mirage;
using UnityEngine;

namespace JamesFrowen.LargeFiles
{
    public interface IFileTransferProgress
    {
        void OnSend(long sent, long total);
    }
    /// <summary>
    /// Helper class to send large files or streams over the network
    /// <para>Can send raw bytes or Stream</para>
    /// <para>Can be used on server or client. Just use the INetworkPlayer on of the connection you want to send to, or NetworkClient.Connection to send to server</para>
    ///
    /// <para>Use maxKilobytesPerSecond to limit max rate so that Transport buffers do not get full</para>
    /// <para>Uses 1k chunks to avoid fragmentation by transport</para>
    /// <para>Uses reliable channel to make send/receive logic simple</para>
    /// </summary>
    public static class FileTransfer
    {
        public static Task Send(INetworkPlayer conn, byte[] rawBytes, string label, int maxKilobytesPerSecond, IFileTransferProgress tracker = null)
        {
            return Send(new List<INetworkPlayer> { conn }, rawBytes, label, maxKilobytesPerSecond, tracker);
        }
        public static Task Send(INetworkPlayer conn, Stream stream, string label, int maxKilobytesPerSecond, IFileTransferProgress tracker = null)
        {
            return Send(new List<INetworkPlayer> { conn }, stream, label, maxKilobytesPerSecond, tracker);
        }
        public static Task Send(List<INetworkPlayer> connections, byte[] rawBytes, string label, int maxKilobytesPerSecond, IFileTransferProgress tracker = null)
        {
            var stream = new MemoryStream(rawBytes);
            return Send(connections, stream, label, maxKilobytesPerSecond, tracker);
        }
        public static async Task Send(List<INetworkPlayer> connections, Stream stream, string label, int maxKilobytesPerSecond, IFileTransferProgress tracker = null)
        {
            try
            {
                var sendIds = new List<int>();
                foreach (var conn in connections)
                {
                    var sendId = GetNextIndex(conn);
                    sendIds.Add(sendId);
                }

                // read chunks from stream, and then send them using Chunk message
                // send in chucks of 1000 bytes,
                // and make sure sending does not exceed maxKilobytesPerSecond

                var length = stream.Length;

                var frameRate = Application.targetFrameRate;
                // also convert to bytes from Kb
                var maxPerFrame = 1000 * maxKilobytesPerSecond / frameRate;
                Debug.Log($"Sending {length} bytes, at max {maxPerFrame} per frame");

                const int ChunkSize = 1024;

                for (var i = 0; i < connections.Count; i++)
                    connections[i].Send(new StartMessage { SendId = sendIds[i], Label = label, Length = length });

                var buffer = new byte[ChunkSize];
                var sentThisFrame = 0;
                long sentTotal = 0;
                while (sentTotal < length)
                {


                    // create from stream into buffer
                    var read = await stream.ReadAsync(buffer, 0, ChunkSize);

                    var chunk = new ChunkMessage
                    {
                        Label = label,
                        Data = new ArraySegment<byte>(buffer, 0, read)
                    };

                    for (var i = 0; i < connections.Count; i++)
                    {
                        chunk.SendId = sendIds[i];
                        connections[i].Send(chunk);
                    }

                    sentTotal += read;
                    sentThisFrame += read;
                    if (tracker != null)
                        tracker.OnSend(sentTotal, length);

                    if (sentThisFrame > maxPerFrame)
                    {
                        sentThisFrame = 0;
                        await Task.Yield();
#if UNITY_EDITOR
                        // stop if not playing
                        if (!Application.isPlaying)
                            return;
#endif
                        // check connected
                        for (var i = connections.Count - 1; i >= 0; i--)
                        {
                            if (connections[i].Connection.State != Mirage.SocketLayer.ConnectionState.Connected)
                            {
                                connections.RemoveAt(i);
                                sendIds.RemoveAt(i);
                            }
                        }
                    }
                }

                // send finished message
                for (var i = 0; i < connections.Count; i++)
                    connections[i].Send(new FinishedMessage { SendId = sendIds[i], Label = label });
            }
            catch (Exception e)
            {
                // need to log with Task, because unity will hide the exception otherwise
                Debug.LogException(e);
                // we still want to re-throw after so that user can use await to catch
                throw;
            }
        }

        private static int GetNextIndex(INetworkPlayer conn)
        {
            var previousIndex = 0;
            if (PreviousSendIndex.TryGetValue(conn, out var index))
            {
                previousIndex = index;
            }

            previousIndex++;
            PreviousSendIndex[conn] = previousIndex;
            return previousIndex;
        }

        private static readonly Dictionary<INetworkPlayer, int> PreviousSendIndex = new Dictionary<INetworkPlayer, int>();
        private static readonly Dictionary<ReceiveKey, Receiver> Receive = new Dictionary<ReceiveKey, Receiver>();

        public static void SetupMessageHandlers(IMessageReceiver handler)
        {
            handler.RegisterHandler<StartMessage>(HandleMessage);
            handler.RegisterHandler<ChunkMessage>(HandleMessage);
            handler.RegisterHandler<FinishedMessage>(HandleMessage);
        }

        private static void HandleMessage(INetworkPlayer conn, StartMessage msg)
        {
            var key = new ReceiveKey(conn, msg);
            var receive = new Receiver(conn, msg);
            receive.Stream = CreateReceiveStream(conn, msg);
            Receive.Add(key, receive);
            OnStartReceive?.Invoke(receive);
        }

        private static void HandleMessage(INetworkPlayer conn, ChunkMessage msg)
        {
            var key = new ReceiveKey(conn, msg);
            var receiver = Receive[key];
            receiver.ReceiveChunk(msg);
            OnChunkReceive?.Invoke(receiver);
        }

        private static void HandleMessage(INetworkPlayer conn, FinishedMessage msg)
        {
            var key = new ReceiveKey(conn, msg);
            var receiver = Receive[key];
            OnFinishReceive?.Invoke(receiver);
            Receive.Remove(key);
            receiver.Stream.Dispose();
        }

        public static event Action<Receiver> OnStartReceive;
        public static event Action<Receiver> OnChunkReceive;
        public static event Action<Receiver> OnFinishReceive;

        public static Func<INetworkPlayer, StartMessage, Stream> CreateReceiveStream = DefaultReceiveStream;
        public static Stream DefaultReceiveStream(INetworkPlayer conn, StartMessage startMessage)
        {
            if (startMessage.Length < int.MaxValue)
                return new MemoryStream(capacity: (int)startMessage.Length);
            else
                throw new InvalidOperationException($"Can't receive message over {int.MaxValue} bytes with default handler, Please override and CreateReceiveStream and save to file");
        }

        public class Receiver
        {
            public readonly string Label;
            public readonly int SendId;
            public readonly INetworkPlayer Connection;
            public readonly long ExpectedLength;

            public Stream Stream;
            public long Received;
            public bool Finished;

            public Receiver(INetworkPlayer connection, StartMessage msg)
            {
                Connection = connection;
                Label = msg.Label;
                SendId = msg.SendId;
                ExpectedLength = msg.Length;
            }

            public void ReceiveChunk(ChunkMessage msg)
            {
                var array = msg.Data.Array;
                var offset = msg.Data.Offset;
                var count = msg.Data.Count;
                Stream.Write(array, offset, count);
                Received += count;
            }
        }

        public struct ReceiveKey : IEquatable<ReceiveKey>
        {
            public string Label;
            public int SendId;
            public INetworkPlayer Connection;

            public ReceiveKey(INetworkPlayer connection, StartMessage msg)
            {
                Label = msg.Label;
                SendId = msg.SendId;
                Connection = connection;
            }
            public ReceiveKey(INetworkPlayer connection, ChunkMessage msg)
            {
                Label = msg.Label;
                SendId = msg.SendId;
                Connection = connection;
            }
            public ReceiveKey(INetworkPlayer connection, FinishedMessage msg)
            {
                Label = msg.Label;
                SendId = msg.SendId;
                Connection = connection;
            }

            public override int GetHashCode()
            {
                var hash = 13;
                hash = (hash * 7) + SendId;
                hash = (hash * 7) + Label.GetHashCode();
                hash = (hash * 7) + Connection.GetHashCode();
                return hash;
            }
            public bool Equals(ReceiveKey other)
            {
                return
                    SendId == other.SendId &&
                    Label == other.Label &&
                    Connection == other.Connection;
            }
        }

        [NetworkMessage]
        public struct StartMessage
        {
            public string Label;
            public int SendId;
            public long Length;
        }
        [NetworkMessage]
        public struct ChunkMessage
        {
            public string Label;
            public int SendId;
            public ArraySegment<byte> Data;
        }
        [NetworkMessage]
        public struct FinishedMessage
        {
            public string Label;
            public int SendId;
        }
    }
}

