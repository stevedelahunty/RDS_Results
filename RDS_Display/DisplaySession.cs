using System.IO;
using System.Net.Sockets;
using System.Text;

namespace RDS_Display;

static class DisplaySession
{
    public static async Task RunAsync(
        string host,
        int port,
        Func<string, Task> onLine,
        Func<string, Task> onStatus,
        CancellationToken cancellationToken)
    {
        var connectHost = ConnectHost(host);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await onStatus($"Connecting to {connectHost}:{port}").ConfigureAwait(false);
                using var client = new TcpClient();
                await client.ConnectAsync(connectHost, port, cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                await onStatus("Connected.").ConfigureAwait(false);
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line == null)
                        break;
                    await onLine(line).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested || ex is OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await onStatus("Display connection failed. " + ex.Message).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    static string ConnectHost(string host)
    {
        var trimmed = host.Trim();
        if (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("::1", StringComparison.Ordinal) ||
            trimmed.Equals("[::1]", StringComparison.Ordinal))
            return "127.0.0.1";
        return trimmed;
    }
}
