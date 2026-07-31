/*
    Copyright © 2002, The KPD-Team
    All rights reserved.
    http://www.mentalis.org/

  Redistribution and use in source and binary forms, with or without
  modification, are permitted provided that the following conditions
  are met:

    - Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer. 

    - Neither the name of the KPD-Team, nor the names of its contributors
       may be used to endorse or promote products derived from this
       software without specific prior written permission. 

  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
  LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
  FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
  THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
  (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
  SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
  HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
  STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
  OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Net;
using System.Net.Sockets;

// Implements a number of classes to allow Sockets to connect trough a firewall.
namespace Titanium.Web.Proxy.ProxySocket;

/// <summary>
///     Specifies the type of proxy servers that an instance of the ProxySocket class can use.
/// </summary>
internal enum ProxyTypes
{
    /// <summary>No proxy server; the ProxySocket object behaves exactly like an ordinary Socket object.</summary>
    None,

    /// <summary>A SOCKS4[A] proxy server.</summary>
    Socks4,

    /// <summary>A SOCKS5 proxy server.</summary>
    Socks5
}

/// <summary>
///     Implements a Socket class that can connect trough a SOCKS proxy server.
/// </summary>
/// <remarks>
///     This class implements SOCKS4[A] and SOCKS5.
///     <br>It does not, however, implement the BIND commands, so you cannot .</br>
/// </remarks>
internal class ProxySocket : Socket
{
    /// <summary>Holds the value of the ProxyPass property.</summary>
    private string proxyPass = string.Empty;

    // private variables

    /// <summary>Holds the value of the ProxyUser property.</summary>
    private string proxyUser = string.Empty;

    /// <summary>
    ///     Initializes a new instance of the ProxySocket class.
    /// </summary>
    /// <param name="addressFamily">One of the AddressFamily values.</param>
    /// <param name="socketType">One of the SocketType values.</param>
    /// <param name="protocolType">One of the ProtocolType values.</param>
    /// <exception cref="SocketException">
    ///     The combination of addressFamily, socketType, and protocolType results in an invalid
    ///     socket.
    /// </exception>
    public ProxySocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType) : this(
        addressFamily, socketType, protocolType, "")
    {
    }

    /// <summary>
    ///     Initializes a new instance of the ProxySocket class.
    /// </summary>
    /// <param name="addressFamily">One of the AddressFamily values.</param>
    /// <param name="socketType">One of the SocketType values.</param>
    /// <param name="protocolType">One of the ProtocolType values.</param>
    /// <param name="proxyUsername">The username to use when authenticating with the proxy server.</param>
    /// <exception cref="SocketException">
    ///     The combination of addressFamily, socketType, and protocolType results in an invalid
    ///     socket.
    /// </exception>
    /// <exception cref="ArgumentNullException"><c>proxyUsername</c> is null.</exception>
    public ProxySocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType,
        string proxyUsername) : this(addressFamily, socketType, protocolType, proxyUsername, "")
    {
    }

    /// <summary>
    ///     Initializes a new instance of the ProxySocket class.
    /// </summary>
    /// <param name="addressFamily">One of the AddressFamily values.</param>
    /// <param name="socketType">One of the SocketType values.</param>
    /// <param name="protocolType">One of the ProtocolType values.</param>
    /// <param name="proxyUsername">The username to use when authenticating with the proxy server.</param>
    /// <param name="proxyPassword">The password to use when authenticating with the proxy server.</param>
    /// <exception cref="SocketException">
    ///     The combination of addressFamily, socketType, and protocolType results in an invalid
    ///     socket.
    /// </exception>
    /// <exception cref="ArgumentNullException"><c>proxyUsername</c> -or- <c>proxyPassword</c> is null.</exception>
    public ProxySocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType,
        string proxyUsername, string proxyPassword) : base(addressFamily, socketType, protocolType)
    {
        ProxyUser = proxyUsername;
        ProxyPass = proxyPassword;
    }

    /// <summary>
    ///     Gets or sets the EndPoint of the proxy server.
    /// </summary>
    /// <value>An IPEndPoint object that holds the IP address and the port of the proxy server.</value>
    public IPEndPoint? ProxyEndPoint { get; set; }

    /// <summary>
    ///     Gets or sets the type of proxy server to use.
    /// </summary>
    /// <value>One of the ProxyTypes values.</value>
    public ProxyTypes ProxyType { get; set; } = ProxyTypes.None;

    /// <summary>
    ///     Gets or sets the username to use when authenticating with the proxy.
    /// </summary>
    /// <value>A string that holds the username that's used when authenticating with the proxy.</value>
    /// <exception cref="ArgumentNullException">The specified value is null.</exception>
    public string ProxyUser
    {
        get => proxyUser;
        set => proxyUser = value ?? throw new ArgumentNullException();
    }

    /// <summary>
    ///     Gets or sets the password to use when authenticating with the proxy.
    /// </summary>
    /// <value>A string that holds the password that's used when authenticating with the proxy.</value>
    /// <exception cref="ArgumentNullException">The specified value is null.</exception>
    public string ProxyPass
    {
        get => proxyPass;
        set => proxyPass = value ?? throw new ArgumentNullException();
    }

    /// <summary>
    ///     Begins an asynchronous request for a connection to a network device.
    /// </summary>
    /// <param name="address">An EndPoint address that represents the remote device.</param>
    /// <param name="port">An EndPoint port that represents the remote device.</param>
    /// <param name="callback">The AsyncCallback delegate.</param>
    /// <param name="state">An object that contains state information for this request.</param>
    /// <returns>An IAsyncResult that references the asynchronous connection.</returns>
    /// <exception cref="ArgumentNullException">The remoteEP parameter is a null reference (Nothing in Visual Basic).</exception>
    /// <exception cref="SocketException">An operating system error occurs while creating the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    public new IAsyncResult BeginConnect(IPAddress address, int port, AsyncCallback? callback, object? state)
    {
        var remoteEp = new IPEndPoint(address, port);
        return BeginConnect(remoteEp, callback, state);
    }

    /// <summary>
    ///     Begins an asynchronous request for a connection to a network device.
    /// </summary>
    /// <param name="remoteEp">An EndPoint that represents the remote device.</param>
    /// <param name="callback">The AsyncCallback delegate.</param>
    /// <param name="state">An object that contains state information for this request.</param>
    /// <returns>An IAsyncResult that references the asynchronous connection.</returns>
    /// <exception cref="ArgumentNullException">The remoteEP parameter is a null reference (Nothing in Visual Basic).</exception>
    /// <exception cref="SocketException">An operating system error occurs while creating the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    public new IAsyncResult BeginConnect(EndPoint remoteEp, AsyncCallback? callback, object? state)
    {
        if (remoteEp == null)
            throw new ArgumentNullException();

        if (ProtocolType != ProtocolType.Tcp || ProxyType == ProxyTypes.None || ProxyEndPoint == null)
            return base.BeginConnect(remoteEp, callback, state);

        var result = new AsyncProxyResult(state);
        HandShakeComplete protocolComplete = error => OnHandShakeComplete(result, callback, error);
        if (ProxyType == ProxyTypes.Socks4)
        {
            return new Socks4Handler(this, ProxyUser).BeginNegotiate((IPEndPoint)remoteEp,
                protocolComplete, ProxyEndPoint, result);
        }

        if (ProxyType == ProxyTypes.Socks5)
        {
            return new Socks5Handler(this, ProxyUser, ProxyPass).BeginNegotiate((IPEndPoint)remoteEp,
                protocolComplete, ProxyEndPoint, result);
        }

        throw new InvalidOperationException($"Unsupported proxy type: {ProxyType}.");
    }

    /// <summary>
    ///     Begins an asynchronous request for a connection to a network device.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port on the remote host to connect to.</param>
    /// <param name="callback">The AsyncCallback delegate.</param>
    /// <param name="state">An object that contains state information for this request.</param>
    /// <returns>An IAsyncResult that references the asynchronous connection.</returns>
    /// <exception cref="ArgumentNullException">The host parameter is a null reference (Nothing in Visual Basic).</exception>
    /// <exception cref="ArgumentException">The port parameter is invalid.</exception>
    /// <exception cref="SocketException">An operating system error occurs while creating the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    public new IAsyncResult BeginConnect(string host, int port, AsyncCallback? callback, object? state)
    {
        if (host == null)
            throw new ArgumentNullException();
        if (port <= 0 || port > 65535)
            throw new ArgumentException();
        var result = new AsyncProxyResult(state);
        HandShakeComplete protocolComplete = error => OnHandShakeComplete(result, callback, error);
        if (ProtocolType != ProtocolType.Tcp || ProxyType == ProxyTypes.None || ProxyEndPoint == null)
        {
            BeginDns(host, port, protocolComplete, result);
            return result;
        }

        if (ProxyType == ProxyTypes.Socks4)
        {
            return new Socks4Handler(this, ProxyUser).BeginNegotiate(host, port,
                protocolComplete, ProxyEndPoint, result);
        }

        if (ProxyType == ProxyTypes.Socks5)
        {
            return new Socks5Handler(this, ProxyUser, ProxyPass).BeginNegotiate(host, port,
                protocolComplete, ProxyEndPoint, result);
        }

        throw new InvalidOperationException($"Unsupported proxy type: {ProxyType}.");
    }

    /// <summary>
    ///     Ends a pending asynchronous connection request.
    /// </summary>
    /// <param name="asyncResult">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    /// <exception cref="ArgumentNullException">The asyncResult parameter is a null reference (Nothing in Visual Basic).</exception>
    /// <exception cref="ArgumentException">The asyncResult parameter was not returned by a call to the BeginConnect method.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    /// <exception cref="InvalidOperationException">EndConnect was previously called for the asynchronous connection.</exception>
    /// <exception cref="ProxyException">The proxy server refused the connection.</exception>
    public new void EndConnect(IAsyncResult asyncResult)
    {
        if (asyncResult == null)
            throw new ArgumentNullException();
        // In case we called Socket.BeginConnect() directly
        if (!(asyncResult is AsyncProxyResult proxyResult))
        {
            base.EndConnect(asyncResult);
            return;
        }

        if (!asyncResult.IsCompleted)
            asyncResult.AsyncWaitHandle.WaitOne();
        if (proxyResult.Error != null)
            throw proxyResult.Error;
    }

    /// <summary>
    ///     Begins an asynchronous request to resolve a DNS host name or IP address in dotted-quad notation to an IPAddress
    ///     instance.
    /// </summary>
    /// <param name="host">The host to resolve.</param>
    /// <param name="callback">The method to call when the hostname has been resolved.</param>
    /// <param name="state">The state.</param>
    /// <returns>An IAsyncResult instance that references the asynchronous request.</returns>
    /// <exception cref="SocketException">There was an error while trying to resolve the host.</exception>
    private void BeginDns(string host, int port, HandShakeComplete callback, AsyncProxyResult result)
    {
        try
        {
            Dns.BeginGetHostEntry(host, OnResolved, new DnsConnectState(port, callback, result));
        }
        catch
        {
            throw new SocketException();
        }
    }

    /// <summary>
    ///     Called when the specified hostname has been resolved.
    /// </summary>
    /// <param name="asyncResult">The result of the asynchronous operation.</param>
    private void OnResolved(IAsyncResult asyncResult)
    {
        var state = asyncResult.AsyncState as DnsConnectState
                    ?? throw new InvalidOperationException("DNS callback state is missing.");
        try
        {
            var dns = Dns.EndGetHostEntry(asyncResult);
            base.BeginConnect(new IPEndPoint(dns.AddressList[0], state.Port), OnConnect, state);
        }
        catch (Exception e)
        {
            state.Callback(e);
        }
    }

    /// <summary>
    ///     Called when the Socket is connected to the remote host.
    /// </summary>
    /// <param name="asyncResult">The result of the asynchronous operation.</param>
    private void OnConnect(IAsyncResult asyncResult)
    {
        var state = asyncResult.AsyncState as DnsConnectState
                    ?? throw new InvalidOperationException("Connect callback state is missing.");
        try
        {
            base.EndConnect(asyncResult);
            state.Callback(null);
        }
        catch (Exception e)
        {
            state.Callback(e);
        }
    }

    /// <summary>
    ///     Called when the Socket has finished talking to the proxy server and is ready to relay data.
    /// </summary>
    /// <param name="error">The error to throw when the EndConnect method is called.</param>
    private void OnHandShakeComplete(AsyncProxyResult result, AsyncCallback? callback, Exception? error)
    {
        if (error != null)
            Close();

        result.Complete(error);
        callback?.Invoke(result);
    }

    private sealed class DnsConnectState
    {
        internal DnsConnectState(int port, HandShakeComplete callback, AsyncProxyResult result)
        {
            Port = port;
            Callback = callback;
            Result = result;
        }

        internal int Port { get; }

        internal HandShakeComplete Callback { get; }

        internal AsyncProxyResult Result { get; }
    }
}