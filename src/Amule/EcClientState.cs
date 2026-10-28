namespace AmuleModern.Amule;

public sealed partial class EcClient
{
    // RequestAsync disposes the socket after any I/O failure; callers use this to tell a
    // rejected command from a dead connection.
    public bool IsConnected
    {
        get
        {
            try { return socket.Connected; }
            catch (ObjectDisposedException) { return false; }
        }
    }
}
