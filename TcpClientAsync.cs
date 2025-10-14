using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TcpClientLibrary
{
    public class TcpClientAsync : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly ConcurrentQueue<string> _commandQueue;
        private readonly CancellationTokenSource _cancellationTokenSource;

        private readonly int DequeueingDelay = 400;
        private readonly int CommandCheckDelay = 50;
        private readonly int ResponseCheckInterval = 100;

        public event EventHandler<string> ResponseReceived;
        public event EventHandler<bool> ConnectionStatusChanged; // New event for connection status

        private bool _isConnected;

        public TcpClientAsync(string ipAddress, int port)
        {
            _client = new TcpClient();
            _client.Connect(ipAddress, port);
            _stream = _client.GetStream();
            _commandQueue = new ConcurrentQueue<string>();
            _cancellationTokenSource = new CancellationTokenSource();
            _isConnected = true;
                     
            StartSendingCommands();
            StartReceivingResponses();

            OnConnectionStatusChanged(true);
         }

        public void QueueCommand(string command)
        {
            if (!String.IsNullOrEmpty(command))
            {
                _commandQueue.Enqueue(command);
            }
        }

        private async void StartSendingCommands()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                if (_commandQueue.TryDequeue(out string command))
                {
                    if (!(command.EndsWith(Environment.NewLine) || command.EndsWith("\r")))
                    {
                        command += Environment.NewLine;
                    }

                    try
                    {
                        byte[] data = Encoding.UTF8.GetBytes(command);
                        await _stream.WriteAsync(data, 0, data.Length, _cancellationTokenSource.Token);
                        await Task.Delay(DequeueingDelay); // delay between messages
                    }
                    catch (Exception e)
                    {
                        throw new Exception($"Error in StartSendingCommands: {e.Message}");
                    }

                }
                else
                {
                    await Task.Delay(CommandCheckDelay); // Delay between message checks
                }

                
            }
        }

        private async void StartReceivingResponses()
        {
            byte[] buffer = new byte[65535];

            try
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    if (_stream.DataAvailable)
                    {
                        int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token);
                        if (bytesRead > 0)
                        {
                            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                            OnResponseReceived(response);
                        }
                    }

                    await Task.Delay(ResponseCheckInterval); // Check for new responses every 50 milliseconds
                }
            }
            catch (Exception e)
            {
                throw new Exception($"Error in StartReceivingResponses: {e.Message}");
            }

        }

        protected virtual void OnResponseReceived(string response)
        {
            ResponseReceived?.Invoke(this, response);
        }

        protected virtual void OnConnectionStatusChanged(bool isConnected)
        {
            if (isConnected != _isConnected)
            {
                _isConnected = isConnected;
                ConnectionStatusChanged?.Invoke(this, isConnected);
            }
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();
            _stream.Close();
            _client.Close(); 

            OnConnectionStatusChanged(false);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
