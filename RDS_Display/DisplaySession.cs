using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RDS_Display;

static class DisplaySession
{
    public static async Task RunAsync(
        int port,
        Func<string, Task> onLine,
        Func<string, Task> onStatus,
        CancellationToken cancellationToken)
    {
        TcpListener? listener = null;
        try
        {
            listener = Listen(port);
            await onStatus($"Listening on port {port}.").ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                client.NoDelay = true;
                await onStatus("RDS Results connected.").ConfigureAwait(false);
                try
                {
                    using (client)
                    using (var reader = new StreamReader(client.GetStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024))
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                            if (line == null)
                                break;
                            await onLine(line).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested || ex is OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                }

                await onStatus($"RDS Results disconnected. Listening on port {port}.").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested || ex is OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            var busy = ex is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse;
            await onStatus(busy
                ? $"Listen port {port} is already in use."
                : "Display listener failed. " + ex.Message).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                listener?.Stop();
            }
            catch (Exception)
            {
            }
        }
    }

    static TcpListener Listen(int port)
    {
        var dual = new TcpListener(IPAddress.IPv6Any, port);
        try
        {
            dual.Server.DualMode = true;
            dual.Start();
            return dual;
        }
        catch (Exception)
        {
            try
            {
                dual.Stop();
            }
            catch (Exception)
            {
            }

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            return listener;
        }
    }
}
