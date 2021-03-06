using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PluginCore;
using PluginCore.Localization;
using PluginCore.Managers;

namespace FlashConnect
{
    public class XmlSocket
    {
        private readonly Socket server;
        private Socket client;
        private StringBuilder packets;
        public event XmlReceivedEventHandler XmlReceived;
        public event DataReceivedEventHandler DataReceived;
        private readonly string INCORRECT_PKT = TextHelper.GetString("Info.IncorrectPacket");
        private readonly string CONNECTION_FAILED = TextHelper.GetString("Info.ConnectionFailed");

        public XmlSocket(string address, int port)
        {
            try
            {
                IPAddress ipAddress = IPAddress.Parse(address);
                server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                server.Bind(new IPEndPoint(ipAddress, port));
                server.Listen(10);
                server.BeginAccept(OnConnectRequest, server);
            }
            catch (SocketException ex)
            {
                if (ex.ErrorCode == 10048) TraceManager.Add("FlashConnect: " + string.Format(CONNECTION_FAILED, port));
                else ErrorManager.ShowError(ex);
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError(ex);
            }
        }
        
        /// <summary>
        /// Accepts the connection request and sets a listener for the next one
        /// </summary>
        public void OnConnectRequest(IAsyncResult result)
        {
            try
            {
                Socket server = (Socket)result.AsyncState;
                client = server.EndAccept(result);
                SetupReceiveCallback(client);
                server.BeginAccept(OnConnectRequest, server);
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError(ex);
            }
        }
        
        /// <summary>
        /// Sets up the receive callback for the accepted connection
        /// </summary>
        public void SetupReceiveCallback(Socket client)
        {
            StateObject so = new StateObject(client);
            try
            {
                AsyncCallback receiveData = OnReceivedData;
                client.BeginReceive(so.Buffer, 0, so.Size, SocketFlags.None, receiveData, so);
            }
            catch (SocketException)
            {
                so.Client.Shutdown(SocketShutdown.Both);
                so.Client.Close();
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError(ex);
            }
        }
        
        /// <summary>
        /// Handles the received data and fires XmlReceived event
        /// </summary>
        public void OnReceivedData(IAsyncResult result)
        {
            StateObject so = (StateObject)result.AsyncState;
            try
            {
                int bytesReceived = so.Client.EndReceive(result);
                if (bytesReceived > 0)
                {
                    /**
                    * Recieve data 
                    */
                    so.Data.Append(Encoding.ASCII.GetString(so.Buffer, 0, bytesReceived));
                    string contents = so.Data.ToString();
                    DataReceived?.Invoke(this, new DataReceivedEventArgs(contents, so.Client));
                    /**
                    * Check packet
                    */
                    if (packets != null) packets.Append(contents);
                    else if (contents.StartsWith('<')) packets = new StringBuilder(contents);
                    else ErrorManager.ShowWarning(INCORRECT_PKT + contents, null);
                    /**
                    * Validate message
                    */
                    if (packets != null && contents.EndsWith('\0'))
                    {
                        string msg = packets.ToString(); packets = null; 
                        if (msg == "<policy-file-request/>\0") 
                        {
                            string policy = "<cross-domain-policy><site-control permitted-cross-domain-policies=\"master-only\"/><allow-access-from domain=\"*\" to-ports=\"*\" /></cross-domain-policy>\0";
                            so.Client.Send(Encoding.ASCII.GetBytes(policy));
                        }
                        else if (msg.EndsWithOrdinal("</flashconnect>\0")) XmlReceived?.Invoke(this, new XmlReceivedEventArgs(msg, so.Client));
                        else ErrorManager.ShowWarning(INCORRECT_PKT + msg, null);
                    }
                    SetupReceiveCallback(so.Client);
                }
                else
                {
                    so.Client.Shutdown(SocketShutdown.Both);
                    so.Client.Close();
                }
            }
            catch (SocketException)
            {
                so.Client.Shutdown(SocketShutdown.Both);
                so.Client.Close();
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError(ex);
            }
        }
    }
}