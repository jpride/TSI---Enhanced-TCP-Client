using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Crestron.SimplSharp;

namespace TcpClientLibrary
{
    public class TcpClientAsync : IDisposable
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private readonly ConcurrentQueue<string> _commandQueue;
        private CancellationTokenSource _cancellationTokenSource;

        private readonly string _ipAddress;
        private readonly int _port;

        // FIX #2: Use an int flag with Interlocked to make IsConnected thread-safe
        // and prevent HandleDisconnectionAsync from being entered by multiple threads simultaneously.
        private int _isConnected = 0; // 0 = false, 1 = true

        private readonly int _dequeueingDelay = 200;
        private readonly int _commandCheckDelay = 50;
        private readonly int _reconnectInterval = 5000;
        private readonly int _connectionMonitorInterval = 3000;

        public event EventHandler<string> ResponseReceived;
        public event EventHandler<bool> ConnectionStatusChanged;

        // FIX #2: IsConnected is now derived from the thread-safe _isConnected flag
        public bool IsConnected => System.Threading.Interlocked.CompareExchange(ref _isConnected, 0, 0) == 1;

        public TcpClientAsync(string ipAddress, int port)
        {
            _ipAddress = ipAddress;
            _port = port;
            _commandQueue = new ConcurrentQueue<string>();
        }

        public void Initialize()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            Task.Run(ManageConnectionAsync, _cancellationTokenSource.Token);
        }

        private async Task ManageConnectionAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                if (IsConnected)
                {
                    await Task.Delay(_connectionMonitorInterval);
                    continue;
                }

                try
                {
                    CrestronConsole.PrintLine($"Attempting to connect to {_ipAddress}:{_port}...");
                    _client = new TcpClient();
                    await _client.ConnectAsync(_ipAddress, _port);
                    _stream = _client.GetStream();

                    // FIX #2: Use Interlocked.Exchange to atomically set connected state
                    System.Threading.Interlocked.Exchange(ref _isConnected, 1);
                    OnConnectionStatusChanged(true);
                    CrestronConsole.PrintLine("Connection successful.");

                    var sendTask = StartSendingCommandsAsync();
                    var receiveTask = StartReceivingResponsesAsync();
                    var monitorTask = MonitorConnectionAsync();

                    await Task.WhenAny(sendTask, receiveTask, monitorTask);
                }
                catch (OperationCanceledException)
                {
                    // FIX #4: Cancellation is intentional (Dispose was called) — exit cleanly
                    CrestronConsole.PrintLine("Connection loop cancelled.");
                    break;
                }
                catch (Exception ex)
                {
                    CrestronConsole.PrintLine($"Connection failed: {ex.Message}");
                    OnConnectionStatusChanged(false);
                }
                finally
                {
                    HandleDisconnection();

                    // FIX #4: Check for cancellation before delaying to avoid
                    // OperationCanceledException propagating unhandled out of the loop
                    if (!_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(_reconnectInterval, _cancellationTokenSource.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // Cancelled during reconnect wait — exit cleanly
                            CrestronConsole.PrintLine($"OperationCanceledException in ManageConnectionAsync");
                        }
                    }
                }
            }
        }

        public void QueueCommand(string command)
        {
            if (!string.IsNullOrEmpty(command))
            {
                _commandQueue.Enqueue(command);
            }
        }

        private async Task StartSendingCommandsAsync()
        {
            while (IsConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    if (_commandQueue.TryDequeue(out string command))
                    {
                        if (!(command.EndsWith("\r\n") || command.EndsWith("\n") || command.EndsWith("\r")))
                        {
                            command += "\r\n";
                        }

                        byte[] data = Encoding.UTF8.GetBytes(command);
                        await _stream.WriteAsync(data, 0, data.Length, _cancellationTokenSource.Token);
                        await Task.Delay(_dequeueingDelay);
                    }
                    else
                    {
                        await Task.Delay(_commandCheckDelay);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException ioEx)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                        CrestronConsole.PrintLine("Send loop stopped: Local disconnect.");
                    else
                        CrestronConsole.PrintLine($"Error in Send loop (likely remote disconnect): {ioEx.Message}");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    CrestronConsole.PrintLine("Send loop stopped: Client has been disposed.");
                    break;
                }
                catch (Exception e)
                {
                    CrestronConsole.PrintLine($"Error in StartSendingCommands: {e.Message}");
                }
            }
        }

        // FIX #6: Replaced DataAvailable polling with a continuous blocking ReadAsync.
        // This eliminates up to 100ms response latency and is more reliable on slow networks
        // where DataAvailable can return false even when data is in transit.
        private async Task StartReceivingResponsesAsync()
        {
            var buffer = new byte[65535];
            while (IsConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token);
                    if (bytesRead > 0)
                    {
                        string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        OnResponseReceived(response);
                    }
                    else
                    {
                        // Zero-byte read = graceful shutdown by remote host
                        CrestronConsole.PrintLine("Remote host closed the connection.");
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException ioEx)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                        CrestronConsole.PrintLine("Receive loop stopped: Local disconnect.");
                    else
                        CrestronConsole.PrintLine($"Error in Receive loop (likely remote disconnect): {ioEx.Message}");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    CrestronConsole.PrintLine("Receive loop stopped: Client has been disposed.");
                    break;
                }
                catch (Exception e)
                {
                    CrestronConsole.PrintLine($"Error in StartReceivingResponses: {e.Message}");
                }
            }
        }

        private async Task MonitorConnectionAsync()
        {
            while (IsConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    if (_client.Client.Poll(1, SelectMode.SelectRead) && _client.Client.Available == 0)
                    {
                        CrestronConsole.PrintLine("Connection monitor detected a dead socket.");
                        break;
                    }
                    await Task.Delay(_connectionMonitorInterval);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    CrestronConsole.PrintLine($"Connection monitor error: {ex.Message}");
                    break;
                }
            }
        }

        // FIX #2: Now synchronous. Uses Interlocked.CompareExchange as an atomic
        // "test-and-set" — only the first thread to flip _isConnected from 1→0 proceeds
        // with cleanup. All subsequent callers return immediately, preventing double-disposal
        // and multiple ConnectionStatusChanged(false) events.
        // FIX #3: Removed the async Task wrapper and the .Wait() call in Disconnect() —
        // the method never awaited anything meaningful, and .Wait() carried deadlock risk.
        private void HandleDisconnection()
        {
            // Atomically swap _isConnected from 1 to 0.
            // If the return value is not 1, another thread already handled disconnection.
            if (System.Threading.Interlocked.CompareExchange(ref _isConnected, 0, 1) != 1)
                return;

            OnConnectionStatusChanged(false);

            _stream?.Close();
            _client?.Close();

            _stream = null;
            _client = null;

            CrestronConsole.PrintLine("Connection lost. Will attempt to reconnect.");
        }

        protected virtual void OnResponseReceived(string response)
        {
            ResponseReceived?.Invoke(this, response);
        }

        protected virtual void OnConnectionStatusChanged(bool status)
        {
            ConnectionStatusChanged?.Invoke(this, status);
        }

        // FIX #3: Disconnect() is now clean — no .Wait() needed since HandleDisconnection is synchronous
        public void Disconnect()
        {
            _cancellationTokenSource?.Cancel();
            HandleDisconnection();
        }

        public void Dispose()
        {
            Disconnect();
            _cancellationTokenSource?.Dispose();
        }
    }
}
