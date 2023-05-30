using System.IO;
using JamesFrowen.LargeFiles;
using Mirage;
using UnityEngine;

public class ExampleFileTransfer : MonoBehaviour
{
    public NetworkServer Server;
    public NetworkClient Client;

    public int KbToSend = 1;
    public string label = "Bytes.bin";
    public int MaxKbPerSecond = 60;

    private class Tracker : IFileTransferProgress
    {
        public void OnSend(long sent, long total)
        {
            Debug.Log($"Sent {sent} out of {total}");
        }
    }

    private void Awake()
    {
        Server.Connected.AddListener(OnServerConnect);
        Client.Started.AddListener(OnClientStarted);
        // need to set target rate rate, this will be used by MaxKbPerSecond
        Application.targetFrameRate = 60;
    }

    private void OnServerConnect(INetworkPlayer player)
    {
        var bytes = new byte[KbToSend * 1000];
        Debug.Log($"Sending {bytes.Length} bytes");
        _ = FileTransfer.Send(player, bytes, label, MaxKbPerSecond, new Tracker());
    }

    public void OnClientStarted()
    {
        FileTransfer.SetupMessageHandlers(Client.MessageHandler);
        FileTransfer.CreateReceiveStream = CreateReceiveStream;
        FileTransfer.OnStartReceive += FileTransfer_OnStartReceive;
        FileTransfer.OnChunkReceive += FileTransfer_OnChunkReceive;
        FileTransfer.OnFinishReceive += FileTransfer_OnFinishReceive;
    }

    private Stream CreateReceiveStream(INetworkPlayer player, FileTransfer.StartMessage msg)
    {
        // save to file, using label as name
        return new FileStream(msg.Label, FileMode.Create, FileAccess.Write);
    }

    private void FileTransfer_OnStartReceive(FileTransfer.Receiver obj)
    {
        // do any setup here
    }

    private void FileTransfer_OnChunkReceive(FileTransfer.Receiver receiver)
    {
        // flush to file
        // do this for large files to avoid using too much memory
        var fs = (FileStream)receiver.Stream;
        fs.Flush(flushToDisk: true);
    }

    private void FileTransfer_OnFinishReceive(FileTransfer.Receiver receiver)
    {
        var fs = (FileStream)receiver.Stream;
        fs.Flush(flushToDisk: true);
        fs.Close();
        fs.Dispose();

        Debug.Log($"Receiveed {receiver.Received} bytes");
        // do stuff with file here
        // load the file using label
    }
}
