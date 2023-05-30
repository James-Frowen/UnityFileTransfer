using System.IO;
using Mirror;
using UnityEngine;

using FileTransfer = JamesFrowen.LargeFiles.FileTransfer;

public class ExampleFileTransferManager : NetworkManager
{
    public int KbToSend = 1;
    public string label = "Bytes.bin";
    public int MaxKbPerSecond = 60;

    private class Tracker : JamesFrowen.LargeFiles.IFileTransferProgress
    {
        public void OnSend(int sent, int total)
        {
            Debug.Log($"Sent {sent} out of {total}");
        }
    }

    public override void OnServerConnect(NetworkConnectionToClient conn)
    {
        base.OnServerConnect(conn);

        // need to set target rate rate, this will be used by MaxKbPerSecond
        Application.targetFrameRate = 60;

        byte[] bytes = new byte[KbToSend * 1000];
        Debug.Log($"Sending {bytes.Length} bytes");
        _ = FileTransfer.Send(conn, bytes, label, MaxKbPerSecond, new Tracker());
    }

    public override void OnClientConnect()
    {
        base.OnClientConnect();
        FileTransfer.SetupMessageHandlers();
        FileTransfer.CreateReceiveStream = CreateReceiveStream;
        FileTransfer.OnStartReceive += FileTransfer_OnStartReceive;
        FileTransfer.OnChunkReceive += FileTransfer_OnChunkReceive;
        FileTransfer.OnFinishReceive += FileTransfer_OnFinishReceive;
    }



    private Stream CreateReceiveStream(NetworkConnection conn, FileTransfer.StartMessage msg)
    {
        // save to file, using label as name
        return new FileStream(msg.Label, FileMode.Create, FileAccess.Write);
    }

    private void FileTransfer_OnStartReceive(FileTransfer.Receiver obj)
    {
        throw new System.NotImplementedException();
    }

    private void FileTransfer_OnChunkReceive(FileTransfer.Receiver receiver)
    {
        // flush to file
        // do this for large files to avoid using too much memory
        FileStream fs = (FileStream)receiver.Stream;
        fs.Flush(flushToDisk: true);
    }

    private void FileTransfer_OnFinishReceive(FileTransfer.Receiver receiver)
    {
        Debug.Log($"Receiveed {receiver.Received} bytes");
        // do stuff with file here
        // load the file using label
    }
}

