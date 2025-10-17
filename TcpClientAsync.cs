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

        // --- NEW: Configurable delays for robustness ---
        private readonly int _dequeueingDelay = 400;
        private readonly int _commandCheckDelay = 50;
        private readonly int _responseCheckInterval = 100;
        private readonly int _reconnectInterval = 5000; // Attempt to reconnect every 5 seconds
        private readonly int _connectionMonitorInterval = 3000; // Check connection status every 3 seconds

        public event EventHandler<string> ResponseReceived;
        public event EventHandler<bool> ConnectionStatusChanged;

        public bool IsConnected { get; private set; }

        /// <summary>
        /// Initializes the TCP client but does not connect. Call ConnectAsync to start the connection.
        /// </summary>
        public TcpClientAsync(string ipAddress, int port)
        {
            _ipAddress = ipAddress;
            _port = port;
            _commandQueue = new ConcurrentQueue<string>();
        }

        /// <summary>
        /// Initiates connection and starts all background tasks for sending, receiving, and monitoring.
        /// </summary>
        public void Initialize()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            // Start the connection and reconnection loop in the background.
            Task.Run(ManageConnectionAsync, _cancellationTokenSource.Token);
        }

        /// <summary>
        /// Central method to manage the connection lifecycle, including initial connection and reconnection.
        /// </summary>
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

                    IsConnected = true;
                    OnConnectionStatusChanged(true);
                    CrestronConsole.PrintLine("Connection successful.");

                    // Start the tasks for this connection instance
                    var sendTask = StartSendingCommandsAsync();
                    var receiveTask = StartReceivingResponsesAsync();
                    var monitorTask = MonitorConnectionAsync();

                    // Wait for any of the tasks to complete (which indicates a disconnection)
                    await Task.WhenAny(sendTask, receiveTask, monitorTask);

                }
                catch (Exception ex)
                {
                    CrestronConsole.PrintLine($"Connection failed: {ex.Message}");
                    OnConnectionStatusChanged(false);
                }
                finally
                {
                    // If we are here, it means a disconnection occurred.
                    await HandleDisconnectionAsync();
                    await Task.Delay(_reconnectInterval, _cancellationTokenSource.Token);
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
                            command += "\r\n"; // Standard terminator
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
                catch (IOException ioEx)
                {
                    CrestronConsole.PrintLine($"Error in Send loop (likely disconnect): {ioEx.Message}");
                    break; // Exit loop to trigger reconnection
                }
                catch (ObjectDisposedException)
                {
                    CrestronConsole.PrintLine("Send loop stopped: Client has been disposed.");
                    break; // Exit loop
                }
                catch (Exception e)
                {
                    CrestronConsole.PrintLine($"Error in StartSendingCommands: {e.Message}");
                    // Depending on the error, you might want to break here as well.
                }
            }
        }

        private async Task StartReceivingResponsesAsync()
        {
            var buffer = new byte[65535];
            while (IsConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    if (_stream.DataAvailable)
                    {
                        int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token);
                        if (bytesRead > 0)
                        {
                            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                            OnResponseReceived(response);
                        }
                        else
                        {
                            // A zero-byte read indicates a graceful shutdown by the remote host.
                            CrestronConsole.PrintLine("Remote host closed the connection.");
                            break; // Exit loop to trigger reconnection
                        }
                    }
                    await Task.Delay(_responseCheckInterval);
                }
                catch (IOException ioEx)
                {
                    CrestronConsole.PrintLine($"Error in Receive loop (likely disconnect): {ioEx.Message}");
                    break; // Exit loop to trigger reconnection
                }
                catch (ObjectDisposedException)
                {
                    CrestronConsole.PrintLine("Receive loop stopped: Client has been disposed.");
                    break; // Exit loop
                }
                catch (Exception e)
                {
                    CrestronConsole.PrintLine($"Error in StartReceivingResponses: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Monitors the socket to detect if it has been closed remotely without notice.
        /// </summary>
        private async Task MonitorConnectionAsync()
        {
            while (IsConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // This is a common way to check for a dead socket. Sending 0 bytes
                    // will throw an exception if the connection is closed.
                    if (_client.Client.Poll(1, SelectMode.SelectRead) && _client.Client.Available == 0)
                    {
                        CrestronConsole.PrintLine("Connection monitor detected a dead socket.");
                        break; // Exit to trigger reconnection.
                    }
                    await Task.Delay(_connectionMonitorInterval);
                }
                catch (Exception ex)
                {
                    CrestronConsole.PrintLine($"Connection monitor error: {ex.Message}");
                    break;
                }
            }
        }

        /// <summary>
        /// Handles the cleanup process upon disconnection.
        /// </summary>
        private Task HandleDisconnectionAsync()
        {
            if (!IsConnected) return Task.CompletedTask; // Already handled

            IsConnected = false;
            OnConnectionStatusChanged(false);

            _stream?.Close();
            _client?.Close();

            _stream = null;
            _client = null;

            CrestronConsole.PrintLine("Connection lost. Will attempt to reconnect.");
            return Task.CompletedTask;
        }

        protected virtual void OnResponseReceived(string response)
        {
            ResponseReceived?.Invoke(this, response);
        }

        protected virtual void OnConnectionStatusChanged(bool status)
        {
            ConnectionStatusChanged?.Invoke(this, status);
        }

        /// <summary>
        /// Gracefully disconnects and stops all operations.
        /// </summary>
        public void Disconnect()
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
            }
            HandleDisconnectionAsync().Wait(); // Ensure cleanup is finished
        }

        public void Dispose()
        {
            Disconnect();
            _cancellationTokenSource?.Dispose();
        }
    }
}