using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Graphite.App.Services;

/// <summary>
/// One Graphite per Windows session. The first instance owns a named mutex and listens on a
/// named pipe; a second launch (double-clicking another PDF in Explorer, "Open with") sends its
/// file paths down the pipe and exits, and the running window opens them as tabs and comes to
/// the front. If the running instance doesn't answer (hung, still starting), the second one
/// simply carries on as a normal window rather than losing the file.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string OpenVerb = "OPEN";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _listening;

    /// <summary>True for the instance that owns the session (the one that should show a window).</summary>
    public bool IsFirst { get; }

    public SingleInstance()
    {
        // Per user and per session: two people on one PC (or one person in two RDP sessions)
        // each get their own Graphite.
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        string id = $"Graphite-{user}-{Process.GetCurrentProcess().SessionId}";
        _pipeName = id;
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + id, out bool createdNew);
        IsFirst = createdNew;
    }

    /// <summary>Second instance: hand <paramref name="paths"/> (full paths) to the running one.
    /// True when it took them.</summary>
    public bool Forward(IReadOnlyList<string> paths)
    {
        try
        {
            // We are the process the user just started, so we may give the foreground away;
            // without this Windows only flashes the other window's taskbar button.
            AllowSetForegroundWindow(AsfwAny);
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(OpenVerb);
            foreach (string path in paths) writer.WriteLine(path);
            writer.Flush();
            client.WaitForPipeDrain();
            return true;
        }
        catch (Exception ex)
        {
            App.LogError("Couldn't hand the files to the running Graphite", ex);
            return false;
        }
    }

    /// <summary>First instance: call <paramref name="onOpen"/> (on a background thread) with the
    /// paths every later launch sends. An empty array means "just come to the front".</summary>
    public void Listen(Action<string[]> onOpen)
    {
        if (!IsFirst || _listening != null) return;
        var cts = _listening = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var lines = new List<string>();
                    while (await reader.ReadLineAsync(cts.Token) is { } line)
                        if (line.Length > 0) lines.Add(line);
                    if (lines.Count > 0 && lines[0] == OpenVerb)
                        onOpen(lines.Skip(1).Where(File.Exists).ToArray());
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    App.LogError("Single-instance pipe failed", ex);
                    try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
                }
            }
        });
    }

    public void Dispose()
    {
        _listening?.Cancel();
        try { if (IsFirst) _mutex.ReleaseMutex(); } catch { /* not owned by this thread any more */ }
        _mutex.Dispose();
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
