using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace NGUAdvisorCompanion;

/// <summary>
/// Named-pipe client for the injector, using TWO unidirectional synchronous pipes that mirror the
/// injector side:
///   * READS snapshots from "NGUAdvisorUI" on a background reconnect loop (<see cref="LineReceived"/>).
///   * WRITES command lines to "NGUAdvisorUICmd" from ONE ordered sender thread (<see cref="Send"/>).
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

    // A7: outbound commands are delivered by ONE long-lived worker draining this queue in submission order.
    // Previously every Send() got its own Task.Run, so two clicks a moment apart raced: the thread pool gives
    // no ordering guarantee between two queued work items, and each could additionally be delayed by its own
    // connect timeout / retry sleep. Click ADVISOR then MANUAL and the injector could apply them backwards,
    // leaving the UI and the injector permanently disagreeing (the optimistic toggle never re-reconciles to a
    // value it already shows). The collection is UNBOUNDED on purpose: Add() must never block the UI thread.
    private readonly BlockingCollection<string> _outbox = new BlockingCollection<string>(new ConcurrentQueue<string>());
    private readonly Thread _sendThread;

    // Backlog guard. Only reachable when the injector is gone and the user keeps clicking (each undelivered
    // command burns ~3s of retries). Drops instead of blocking the caller; 256 pending clicks is already a
    // pathological state, and dropping the OLDEST would be the one thing worse than dropping the newest —
    // it would silently reorder the queue this whole class exists to keep ordered.
    private const int MaxBacklog = 256;

    /// <summary>Raised for each snapshot line received from the injector (reader thread).</summary>
    public event Action<string> LineReceived;

    /// <summary>Raised only on snapshot-connection transitions (reader thread).</summary>
    public event Action<bool> ConnectionChanged;

    public PipeClient(string pipeName)
    {
        _snapPipe = pipeName;
        _cmdPipe = pipeName + "Cmd";

        // Started here rather than in Start() so a command submitted before the reader loop is running (or
        // without one at all, as in the tests) is still queued and delivered in order. Parked on Take() when
        // idle, so an unused instance costs nothing.
        _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "NGUAdvisorPipeSender" };
        _sendThread.Start();
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
    /// Queue a command line for the injector. Returns immediately (never blocks the UI thread); the sender
    /// thread delivers queued commands one at a time, in submission order.
    /// </summary>
    public void Send(string line)
    {
        if (line == null || _stopping) return;
        if (_outbox.Count >= MaxBacklog) return;           // injector gone and the user is spamming — see MaxBacklog
        try { _outbox.Add(line); }
        catch (ObjectDisposedException) { /* disposed */ }
        catch (InvalidOperationException) { /* CompleteAdding raced with Dispose */ }
    }

    // Ordered delivery worker. One connection at a time, fully closed before the next is opened, so the
    // injector's single-slot command server sees commands in exactly the order the user issued them.
    private void SendLoop()
    {
        try
        {
            foreach (var line in _outbox.GetConsumingEnumerable())
            {
                if (_stopping) break;                      // shutting down: abandon the backlog, don't stall close
                Deliver(line);
            }
        }
        catch (ObjectDisposedException) { /* disposed during shutdown */ }
        catch (InvalidOperationException) { /* collection completed underneath us */ }
        catch (Exception) { /* a sender crash must never take the app down */ }
    }

    // Connect-per-command (WRITE-only handle), retrying briefly if the command server is mid re-accept.
    private void Deliver(string line)
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
            catch
            {
                if (_stopping) return;                    // don't burn the teardown budget on retry sleeps
                Thread.Sleep(150);                        // server busy re-accepting — retry
            }
        }
    }

    public void Dispose()
    {
        // Order matters: _stopping first, so an in-flight Deliver() abandons its remaining retries and the
        // drain loop discards the backlog instead of holding the close open. CompleteAdding then releases the
        // sender from Take(). Both threads are unblocked before either Join, so they run down concurrently.
        _stopping = true;
        try { _outbox.CompleteAdding(); } catch { }
        try { _client?.Dispose(); } catch { }              // unblock a parked snapshot ReadLine
        var senderDone = false;
        try { senderDone = _sendThread == null || _sendThread.Join(1000); } catch { }
        try { _thread?.Join(1000); } catch { }
        // Only safe to dispose once the consumer has actually left GetConsumingEnumerable().
        if (senderDone) { try { _outbox.Dispose(); } catch { } }
    }
}
