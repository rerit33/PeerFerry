using System.IO.Pipes;
using System.Text;
using Microsoft.Win32;

namespace PeerFerry.App;

internal static class DeepLinks
{
    public static void Register()
    {
        // Per-user registration; no elevation or system-wide registry changes.
        using var key = Registry.CurrentUser.CreateSubKey("Software\\Classes\\peerferry");
        key.SetValue("", "URL:PeerFerry invitation"); key.SetValue("URL Protocol", "");
        using var command = key.CreateSubKey("shell\\open\\command");
        command.SetValue("", $"\"{Environment.ProcessPath}\" \"%1\"");
    }
    public static void Forward(string pipeName, string invitation)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out); pipe.Connect(1500);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)); writer.WriteLine(invitation);
        }
        catch (IOException) { }
        catch (TimeoutException) { }
    }
    public static async Task ListenAsync(string pipeName, Action<string> received, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
                using var reader = new StreamReader(server, new UTF8Encoding(false));
                var text = new StringBuilder(); var buffer = new char[256];
                while (text.Length < 16_384)
                {
                    var count = await reader.ReadAsync(buffer, deadline.Token); if (count == 0) break;
                    text.Append(buffer, 0, count); if (text.ToString().Contains('\n')) break;
                }
                if (text.Length < 16_384) received(text.ToString().Trim());
            }
        }
        catch (OperationCanceledException) { }
    }
}
