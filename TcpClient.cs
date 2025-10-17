using System;
using TcpClientLibrary;
using Crestron.SimplSharp;
using System.Reflection;
using System.Runtime.CompilerServices;


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
            string methodName = MethodInfo.GetCurrentMethod().Name;
            string className = this.GetType().Name;


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
                _client.Initialize();

                ConnectionStatusUpdateToSimpl(true);
                CrestronConsole.PrintLine($"{className}.{methodName}: Client Initialized Successfuly");
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine($"Error in {className}.{methodName} - {ex.Message}");
                ConnectionStatusUpdateToSimpl(false);
            }
        }

        public void QueueCommand(string cmd)
        {
            string methodName = MethodInfo.GetCurrentMethod().Name;
            string className = this.GetType().Name;


            try
            {
                if (_client == null)
                {
                    CrestronConsole.PrintLine($"{className}.{methodName}: Client is not initialized. Please initialize the client before sending commands.");
                    return;
                }

                if (!String.IsNullOrEmpty(cmd))
                {
                    _client.QueueCommand(cmd);
                }
            }
            catch (Exception)
            {
                CrestronConsole.PrintLine($"{className}.{methodName}: Client is not initialized. Please initialize the client before sending commands.");
            }
          
        }

        public void DisposeClient()
        {
            string methodName = MethodInfo.GetCurrentMethod().Name;
            string className = this.GetType().Name;

            if (_client != null)
            {
                try
                {
                    _client.ResponseReceived -= Client_ResponseReceived;
                    _client.ConnectionStatusChanged -= Client_ConnectionStatusChanged;
                    _client.Dispose();
                    _client = null;
                    CrestronConsole.PrintLine($"{className}.{methodName}: Client disposed");
                    ConnectionStatusUpdateToSimpl(false);
                }
                catch (Exception ex)
                {
                    CrestronConsole.PrintLine($"{className}.{methodName}: Error disposing client - {ex.Message}");
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
            CrestronConsole.PrintLine($"Connection status changed: {isConnected}");

            ConnectionStatusUpdateToSimpl(isConnected);
        }

    }
}

