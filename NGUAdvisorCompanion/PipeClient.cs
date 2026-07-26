using System.IO.Pipes;
using System.Text;

namespace NGUAdvisorCompanion;

/// <summary>
/// Named-pipe client for the injector, using TWO unidirectional synchronous pipes that mirror the
/// injector side:
///   * READS snapshots from "NGUAdvisorUI" on a background reconnect loop (<see cref="LineReceived"/>).
///   * WRITES command lines to "NGUAdvisorUICmd" by connecting per-command (<see cref="Send"/>).
/// Separate handles mean no single handle ever does concurrent read+write — a synchronous duplex handle
/// serializes and deadlocks (observed as an AppHang), and async pipes are unreliable under the injector's
/// Mono runtime. Events fire on the reader thread; the form marshals them to the UI thread.
/// </summary>
public sealed class PipeClient : IDisposable
{
    private readonly string _snapPipe;
    private readonly string _cmdPipe;
    private Thread _thread;
    private volatile bool _stopping;
    private volatile NamedPipeClientStream _client;   // snapshot reader; disposed to unblock a parked ReadLine

    /// <summary>Raised for each snapshot line received from the injector (reader thread).</summary>
    public event Action<string> LineReceived;

    /// <summary>Raised only on snapshot-connection transitions (reader thread).</summary>
    public event Action<bool> ConnectionChanged;

    public PipeClient(string pipeName)
    {
        _snapPipe = pipeName;
        _cmdPipe = pipeName + "Cmd";
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "NGUAdvisorPipeClient" };
        _thread.Start();
    }

    // Snapshot reader loop (READ-only handle).
    private void Loop()
    {
        while (!_stopping)
        {
            var connected = false;
            try
            {
                var client = new NamedPipeClientStream(".", _snapPipe, PipeDirection.In, PipeOptions.None);
                _client = client;
                client.Connect(1000);                       // TimeoutException if the injector isn't up yet
                connected = true;
                ConnectionChanged?.Invoke(true);

                var reader = new StreamReader(client, new UTF8Encoding(false));
                string line;
                while (!_stopping && (line = reader.ReadLine()) != null)
                    LineReceived?.Invoke(line);
            }
            catch (TimeoutException) { /* server not listening yet — retry */ }
            catch (IOException) { /* peer dropped */ }
            catch (ObjectDisposedException) { /* disposed during shutdown */ }
            catch (Exception) { /* keep the loop alive */ }
            finally
            {
                try { _client?.Dispose(); } catch { }
                _client = null;
                if (connected) ConnectionChanged?.Invoke(false);
            }

            if (!_stopping) Thread.Sleep(700);              // backoff before reconnecting
        }
    }

    /// <summary>
    /// Send a command line to the injector on the command pipe. Connect-per-command (WRITE-only handle),
    /// off the caller's thread so the UI thread never blocks; retries briefly if the command server is
    /// mid re-accept.
    /// </summary>
    public void Send(string line)
    {
        Task.Run(() =>
        {
            for (var attempt = 0; attempt < 5 && !_stopping; attempt++)
            {
                try
                {
                    using var c = new NamedPipeClientStream(".", _cmdPipe, PipeDirection.Out, PipeOptions.None);
                    c.Connect(500);
                    using var w = new StreamWriter(c, new UTF8Encoding(false)) { AutoFlush = true };
                    w.WriteLine(line);
                    return;                                   // delivered
                }
                catch { Thread.Sleep(150); }                  // server busy re-accepting — retry
            }
        });
    }

    public void Dispose()
    {
        _stopping = true;
        try { _client?.Dispose(); } catch { }              // unblock a parked snapshot ReadLine
        try { _thread?.Join(1000); } catch { }
    }
}
