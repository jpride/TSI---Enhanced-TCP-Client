using System;
using TcpClientLibrary;
using Crestron.SimplSharp;

namespace TSI_Enhanced_TCP_Client
{
    public class TcpClientObject
    {
        //private 
        private TcpClientAsync _client;

        private string _ipaddress;
        private ushort _port;
        //private

        //events
        public event EventHandler<ResponseEventArgs> ResponseReceived;
        public event EventHandler<ConnectionStatusEventArgs> ConnectionStatusChanged;
        //events

        
        public string IPAddress
        {
            get { return _ipaddress; }
            set { _ipaddress = value; }
        }

        public ushort Port
        {
            get { return _port; }
            set { _port = value; }
        }

        public void Initialize(string ipaddress, ushort port)
        {
            IPAddress = ipaddress;
            Port = port;

            try
            {
                if (_client != null)
                {
                    DisposeClient();
                }

                _client = new TcpClientAsync(IPAddress, Port);
                _client.ResponseReceived += Client_ResponseReceived;
                _client.ConnectionStatusChanged += Client_ConnectionStatusChanged;

                ConnectionStatusUpdateToSimpl(true);
                CrestronConsole.PrintLine($"Class library - Client Initialized Successfuly");
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine($"Error in Initialize - {ex.Message}");
                ConnectionStatusUpdateToSimpl(false);
            }
        }

        public void QueueCommand(string cmd)
        {
            if (cmd !=  null)
            {
                _client.QueueCommand(cmd);
            }           
        }

        public void DisposeClient()
        {
            if (_client != null)
            {
                try
                {
                    _client.ResponseReceived -= Client_ResponseReceived;
                    _client.ConnectionStatusChanged -= Client_ConnectionStatusChanged;
                    _client.Dispose();
                    _client = null;
                    CrestronConsole.PrintLine("Client disposed");
                    ConnectionStatusUpdateToSimpl(false);
                }
                catch (Exception ex)
                {
                    CrestronConsole.PrintLine($"Error disposing client - {ex.Message}");
                }
            }
        }

        public void ConnectionStatusUpdateToSimpl(bool isConnected)
        {
            ConnectionStatusEventArgs args = new ConnectionStatusEventArgs
            {
                IsConnected = (ushort)(isConnected ? 1 : 0) //must convert bools to ushorts for simpl+
            };

            ConnectionStatusChanged?.Invoke(this, args);
        }

        private void Client_ResponseReceived(object sender, string response)
        {
            ResponseEventArgs args = new ResponseEventArgs
            {
                Response = response,
            };
            ResponseReceived?.Invoke(this, args);   
        }

        private void Client_ConnectionStatusChanged(object sender, bool isConnected)
        {
            //CrestronConsole.PrintLine($"Connection status changed: {isConnected}");

            ConnectionStatusUpdateToSimpl(isConnected);
        }

    }
}

