using System;


namespace TSI_Enhanced_TCP_Client
{
    public class ConnectionStatusEventArgs : EventArgs
    {
        public ushort IsConnected { get; set; }
    }

    public class ResponseEventArgs : EventArgs
    {
        public string Response { get; set; }
    }
}
