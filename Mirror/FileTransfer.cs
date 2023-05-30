using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Mirror;
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
    /// <para>Can be used on server or client. Just use the NetworkConnection on of the connection you want to send to, or NetworkClient.Connection to send to server</para>
    ///
    /// <para>Use maxKilobytesPerSecond to limit max rate so that Transport buffers do not get full</para>
    /// <para>Uses 1k chunks to avoid fragmentation by transport</para>
    /// <para>Uses reliable channel to make send/receive logic simple</para>
    /// </summary>
    public static class FileTransfer
    {
        public static Task Send(NetworkConnection conn, byte[] rawBytes, string label, int maxKilobytesPerSecond, IFileTransferProgress tracker = null)
        {
            return Send(new List<NetworkConnection> { conn }, rawBytes, label, maxKilobytesPerSecond, tracker);
        }
        public static Task Send(NetworkConnection conn, Stream stream, string label, int maxKilobytesPerSecond, IFileTransferProgress tracker = null)
        {
            return Send(new List<NetworkConnection> { conn }, stream, label, maxKilobytesPerSecond, tracker);
        }
        public static Task Send(List<NetworkConnection> connections, byte[] rawBytes, string label, int maxKilobytesPerSecond, IFileTransferProgress tracker = null)
        {
            MemoryStream stream = new MemoryStream(rawBytes);
            return Send(connections, stream, label, maxKilobytesPerSecond, tracker);
        }
        public static async Task Send(List<NetworkConnection> connections, Stream stream, string label, int maxKilobytesPerSecond, IFileTransferProgress tracker = null)
        {
            try
            {
                List<int> sendIds = new List<int>();
                foreach (NetworkConnection conn in connections)
                {
                    int sendId = GetNextIndex(conn);
                    sendIds.Add(sendId);
                }

                // read chunks from stream, and then send them using Chunk message
                // send in chucks of 1000 bytes,
                // and make sure sending does not exceed maxKilobytesPerSecond

                long length = stream.Length;

                int frameRate = Application.targetFrameRate;
                // also convert to bytes from Kb
                int maxPerFrame = 1000 * maxKilobytesPerSecond / frameRate;
                Debug.Log($"Sending {length} bytes, at max {maxPerFrame} per frame");

                const int ChunkSize = 1024;

                for (int i = 0; i < connections.Count; i++)
                    connections[i].Send(new StartMessage { SendId = sendIds[i], Label = label, Length = length });

                byte[] buffer = new byte[ChunkSize];
                int sentThisFrame = 0;
                long sentTotal = 0;
                while (sentTotal < length)
                {


                    // create from stream into buffer
                    int read = await stream.ReadAsync(buffer, 0, ChunkSize);

                    ChunkMessage chunk = new ChunkMessage
                    {
                        Label = label,
                        Data = new ArraySegment<byte>(buffer, 0, read)
                    };

                    for (int i = 0; i < connections.Count; i++)
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
                        // todo check if connection is still connected
                        //      if it isn't then remove it (and its send id)
                        //for (int i = connections.Count - 1; i >= 0; i--)
                        //{
                        //    if (!connections[i].Isconnected)
                        //    {
                        //        connections.RemoveAt(i);
                        //        sendIds.RemoveAt(i);
                        //    }
                        //}
                    }
                }

                // send finished message
                for (int i = 0; i < connections.Count; i++)
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

        private static int GetNextIndex(NetworkConnection conn)
        {
            int previousIndex = 0;
            if (PreviousSendIndex.TryGetValue(conn, out int index))
            {
                previousIndex = index;
            }

            previousIndex++;
            PreviousSendIndex[conn] = previousIndex;
            return previousIndex;
        }

        private static readonly Dictionary<NetworkConnection, int> PreviousSendIndex = new Dictionary<NetworkConnection, int>();
        private static readonly Dictionary<ReceiveKey, Receiver> Receive = new Dictionary<ReceiveKey, Receiver>();


        public static void SetupMessageHandlers()
        {
            NetworkServer.RegisterHandler<StartMessage>(HandleMessage);
            NetworkServer.RegisterHandler<ChunkMessage>(HandleMessage);
            NetworkServer.RegisterHandler<FinishedMessage>(HandleMessage);

            // use ReplaceHandler here for client because mirror is dumb and doesn't have RegisterHandler for client
            NetworkClient.ReplaceHandler<StartMessage>(HandleMessage);
            NetworkClient.ReplaceHandler<ChunkMessage>(HandleMessage);
            NetworkClient.ReplaceHandler<FinishedMessage>(HandleMessage);
        }
        public static void HandleMessage(NetworkConnection conn, StartMessage msg)
        {
            ReceiveKey key = new ReceiveKey(conn, msg);
            Receiver receive = new Receiver(conn, msg);
            receive.Stream = CreateReceiveStream(conn, msg);
            Receive.Add(key, receive);
            OnStartReceive?.Invoke(receive);
        }
        public static void HandleMessage(NetworkConnection conn, ChunkMessage msg)
        {
            ReceiveKey key = new ReceiveKey(conn, msg);
            Receiver receiver = Receive[key];
            receiver.ReceiveChunk(msg);
            OnChunkReceive?.Invoke(receiver);
        }
        public static void HandleMessage(NetworkConnection conn, FinishedMessage msg)
        {
            ReceiveKey key = new ReceiveKey(conn, msg);
            Receiver receiver = Receive[key];
            OnFinishReceive?.Invoke(receiver);
            Receive.Remove(key);
            receiver.Stream.Dispose();
        }

        public static event Action<Receiver> OnStartReceive;
        public static event Action<Receiver> OnChunkReceive;
        public static event Action<Receiver> OnFinishReceive;

        public static Func<NetworkConnection, StartMessage, Stream> CreateReceiveStream = DefaultReceiveStream;
        public static Stream DefaultReceiveStream(NetworkConnection conn, StartMessage startMessage)
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
            public readonly NetworkConnection Connection;

            public Stream Stream;
            public long Received;
            public bool Finished;

            public Receiver(NetworkConnection connection, StartMessage msg)
            {
                Connection = connection;
                Label = msg.Label;
                SendId = msg.SendId;
            }

            public void ReceiveChunk(ChunkMessage msg)
            {
                Stream.Write(msg.Data);
                Received += msg.Data.Count;
            }
        }

        public struct ReceiveKey : IEquatable<ReceiveKey>
        {
            public string Label;
            public int SendId;
            public NetworkConnection Connection;

            public ReceiveKey(NetworkConnection connection, StartMessage msg)
            {
                Label = msg.Label;
                SendId = msg.SendId;
                Connection = connection;
            }
            public ReceiveKey(NetworkConnection connection, ChunkMessage msg)
            {
                Label = msg.Label;
                SendId = msg.SendId;
                Connection = connection;
            }
            public ReceiveKey(NetworkConnection connection, FinishedMessage msg)
            {
                Label = msg.Label;
                SendId = msg.SendId;
                Connection = connection;
            }

            public override int GetHashCode()
            {
                int hash = 13;
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

        public struct StartMessage : NetworkMessage
        {
            public string Label;
            public int SendId;
            public long Length;
        }
        public struct ChunkMessage : NetworkMessage
        {
            public string Label;
            public int SendId;
            public ArraySegment<byte> Data;
        }
        public struct FinishedMessage : NetworkMessage
        {
            public string Label;
            public int SendId;
        }
    }
}

