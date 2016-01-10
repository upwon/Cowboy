﻿using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Cowboy.Buffer;
using Cowboy.Logging;

namespace Cowboy.Sockets
{
    public class AsyncTcpSocketClient
    {
        #region Fields

        private static readonly ILog _log = Logger.Get<AsyncTcpSocketClient>();
        private IBufferManager _bufferManager;
        private TcpClient _tcpClient;
        private readonly IAsyncTcpSocketClientMessageDispatcher _dispatcher;
        private readonly AsyncTcpSocketClientConfiguration _configuration;
        private readonly IPEndPoint _remoteEndPoint;
        private readonly IPEndPoint _localEndPoint;
        private Stream _stream;
        private byte[] _receiveBuffer;
        private byte[] _sessionBuffer;
        private int _sessionBufferCount = 0;

        private int _state;
        private const int _none = 0;
        private const int _connecting = 1;
        private const int _connected = 2;
        private const int _disposed = 5;

        #endregion

        #region Constructors

        public AsyncTcpSocketClient(IPAddress remoteAddress, int remotePort, IPAddress localAddress, int localPort, IAsyncTcpSocketClientMessageDispatcher dispatcher, AsyncTcpSocketClientConfiguration configuration = null)
            : this(new IPEndPoint(remoteAddress, remotePort), new IPEndPoint(localAddress, localPort), dispatcher, configuration)
        {
        }

        public AsyncTcpSocketClient(IPAddress remoteAddress, int remotePort, IPEndPoint localEP, IAsyncTcpSocketClientMessageDispatcher dispatcher, AsyncTcpSocketClientConfiguration configuration = null)
            : this(new IPEndPoint(remoteAddress, remotePort), localEP, dispatcher, configuration)
        {
        }

        public AsyncTcpSocketClient(IPAddress remoteAddress, int remotePort, IAsyncTcpSocketClientMessageDispatcher dispatcher, AsyncTcpSocketClientConfiguration configuration = null)
            : this(new IPEndPoint(remoteAddress, remotePort), dispatcher, configuration)
        {
        }

        public AsyncTcpSocketClient(IPEndPoint remoteEP, IAsyncTcpSocketClientMessageDispatcher dispatcher, AsyncTcpSocketClientConfiguration configuration = null)
            : this(remoteEP, null, dispatcher, configuration)
        {
        }

        public AsyncTcpSocketClient(IPEndPoint remoteEP, IPEndPoint localEP, IAsyncTcpSocketClientMessageDispatcher dispatcher, AsyncTcpSocketClientConfiguration configuration = null)
        {
            if (remoteEP == null)
                throw new ArgumentNullException("remoteEP");
            if (dispatcher == null)
                throw new ArgumentNullException("dispatcher");

            _remoteEndPoint = remoteEP;
            _localEndPoint = localEP;
            _dispatcher = dispatcher;
            _configuration = configuration ?? new AsyncTcpSocketClientConfiguration();

            this.ConnectTimeout = TimeSpan.FromSeconds(30);

            Initialize();
        }

        public AsyncTcpSocketClient(IPAddress remoteAddress, int remotePort, IPAddress localAddress, int localPort,
            Func<AsyncTcpSocketClient, byte[], int, int, Task> onServerDataReceived = null,
            Func<AsyncTcpSocketClient, Task> onServerConnected = null,
            Func<AsyncTcpSocketClient, Task> onServerDisconnected = null,
            AsyncTcpSocketClientConfiguration configuration = null)
            : this(new IPEndPoint(remoteAddress, remotePort), new IPEndPoint(localAddress, localPort),
                  onServerDataReceived, onServerConnected, onServerDisconnected, configuration)
        {
        }

        public AsyncTcpSocketClient(IPAddress remoteAddress, int remotePort, IPEndPoint localEP,
            Func<AsyncTcpSocketClient, byte[], int, int, Task> onServerDataReceived = null,
            Func<AsyncTcpSocketClient, Task> onServerConnected = null,
            Func<AsyncTcpSocketClient, Task> onServerDisconnected = null,
            AsyncTcpSocketClientConfiguration configuration = null)
            : this(new IPEndPoint(remoteAddress, remotePort), localEP,
                  onServerDataReceived, onServerConnected, onServerDisconnected, configuration)
        {
        }

        public AsyncTcpSocketClient(IPAddress remoteAddress, int remotePort,
            Func<AsyncTcpSocketClient, byte[], int, int, Task> onServerDataReceived = null,
            Func<AsyncTcpSocketClient, Task> onServerConnected = null,
            Func<AsyncTcpSocketClient, Task> onServerDisconnected = null,
            AsyncTcpSocketClientConfiguration configuration = null)
            : this(new IPEndPoint(remoteAddress, remotePort),
                  onServerDataReceived, onServerConnected, onServerDisconnected, configuration)
        {
        }

        public AsyncTcpSocketClient(IPEndPoint remoteEP,
            Func<AsyncTcpSocketClient, byte[], int, int, Task> onServerDataReceived = null,
            Func<AsyncTcpSocketClient, Task> onServerConnected = null,
            Func<AsyncTcpSocketClient, Task> onServerDisconnected = null,
            AsyncTcpSocketClientConfiguration configuration = null)
            : this(remoteEP, null,
                  onServerDataReceived, onServerConnected, onServerDisconnected, configuration)
        {
        }

        public AsyncTcpSocketClient(IPEndPoint remoteEP, IPEndPoint localEP,
            Func<AsyncTcpSocketClient, byte[], int, int, Task> onServerDataReceived = null,
            Func<AsyncTcpSocketClient, Task> onServerConnected = null,
            Func<AsyncTcpSocketClient, Task> onServerDisconnected = null,
            AsyncTcpSocketClientConfiguration configuration = null)
            : this(remoteEP, localEP,
                 new InternalAsyncTcpSocketClientMessageDispatcherImplementation(onServerDataReceived, onServerConnected, onServerDisconnected),
                 configuration)
        {
        }

        private void Initialize()
        {
            _bufferManager = new GrowingByteBufferManager(_configuration.InitialBufferAllocationCount, _configuration.ReceiveBufferSize);
        }

        #endregion

        #region Properties

        public TimeSpan ConnectTimeout { get; set; }
        public IPEndPoint RemoteEndPoint
        {
            get
            {
                return (_tcpClient != null && _tcpClient.Client.Connected) ?
                    (IPEndPoint)_tcpClient.Client.RemoteEndPoint : _remoteEndPoint;
            }
        }
        public IPEndPoint LocalEndPoint
        {
            get
            {
                return (_tcpClient != null && _tcpClient.Client.Connected) ?
                    (IPEndPoint)_tcpClient.Client.LocalEndPoint : null;
            }
        }

        public TcpSocketState State
        {
            get
            {
                switch (_state)
                {
                    case _none:
                        return TcpSocketState.None;
                    case _connecting:
                        return TcpSocketState.Connecting;
                    case _connected:
                        return TcpSocketState.Connected;
                    case _disposed:
                        return TcpSocketState.Closed;
                    default:
                        return TcpSocketState.Closed;
                }
            }
        }

        #endregion

        #region Connect

        public async Task Connect()
        {
            int origin = Interlocked.CompareExchange(ref _state, _connecting, _none);
            if (origin == _disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
            else if (origin != _none)
            {
                throw new InvalidOperationException("This tcp socket client has already connected to server.");
            }

            try
            {
                _tcpClient = _localEndPoint != null ? new TcpClient(_localEndPoint) : new TcpClient();

                var awaiter = _tcpClient.ConnectAsync(_remoteEndPoint.Address, _remoteEndPoint.Port);
                if (!awaiter.Wait(ConnectTimeout))
                {
                    throw new TimeoutException(string.Format(
                        "Connect to [{0}] timeout [{1}].", _remoteEndPoint, ConnectTimeout));
                }

                ConfigureClient();
                var negotiator = NegotiateStream(_tcpClient.GetStream());
                if (!negotiator.Wait(ConnectTimeout))
                {
                    throw new TimeoutException(string.Format(
                        "Negotiate SSL/TSL with remote [{0}] timeout [{1}].", _remoteEndPoint, ConnectTimeout));
                }
                _stream = negotiator.Result;

                _receiveBuffer = _bufferManager.BorrowBuffer();
                _sessionBuffer = _bufferManager.BorrowBuffer();
                _sessionBufferCount = 0;

                if (Interlocked.CompareExchange(ref _state, _connected, _connecting) != _connecting)
                {
                    throw new ObjectDisposedException(GetType().FullName);
                }

                _log.DebugFormat("Connected to server [{0}] with dispatcher [{1}] on [{2}].",
                    this.RemoteEndPoint,
                    _dispatcher.GetType().Name,
                    DateTime.UtcNow.ToString(@"yyyy-MM-dd HH:mm:ss.fffffff"));
                await _dispatcher.OnServerConnected(this);

                Task.Run(async () =>
                {
                    await Process();
                })
                .Forget();
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            when (ex is TimeoutException)
            {
                _log.Error(ex.Message, ex);
                await Close();
            }
        }

        public async Task Close()
        {
            if (Interlocked.Exchange(ref _state, _disposed) == _disposed)
            {
                return;
            }

            try
            {
                if (_stream != null)
                {
                    _stream.Dispose();
                    _stream = null;
                }
                if (_tcpClient != null && _tcpClient.Connected)
                {
                    _tcpClient.Dispose();
                    _tcpClient = null;
                }
            }
            catch (Exception) { }

            if (_receiveBuffer != null)
                _bufferManager.ReturnBuffer(_receiveBuffer);
            if (_sessionBuffer != null)
                _bufferManager.ReturnBuffer(_sessionBuffer);

            _log.DebugFormat("Disconnected from server [{0}] with dispatcher [{1}] on [{2}].",
                this.RemoteEndPoint,
                _dispatcher.GetType().Name,
                DateTime.UtcNow.ToString(@"yyyy-MM-dd HH:mm:ss.fffffff"));
            await _dispatcher.OnServerDisconnected(this);
        }

        private async Task Process()
        {
            try
            {
                while (State == TcpSocketState.Connected)
                {
                    int receiveCount = await _stream.ReadAsync(_receiveBuffer, 0, _receiveBuffer.Length);
                    if (receiveCount == 0)
                        break;

                    if (!_configuration.Framing)
                    {
                        await _dispatcher.OnServerDataReceived(this, _receiveBuffer, 0, receiveCount);
                    }
                    else
                    {
                        BufferDeflector.AppendBuffer(_bufferManager, ref _receiveBuffer, receiveCount, ref _sessionBuffer, ref _sessionBufferCount);

                        while (true)
                        {
                            var frameHeader = TcpFrameHeader.ReadHeader(_sessionBuffer);
                            if (TcpFrameHeader.HEADER_SIZE + frameHeader.PayloadSize <= _sessionBufferCount)
                            {
                                await _dispatcher.OnServerDataReceived(this, _sessionBuffer, TcpFrameHeader.HEADER_SIZE, frameHeader.PayloadSize);
                                BufferDeflector.ShiftBuffer(_bufferManager, TcpFrameHeader.HEADER_SIZE + frameHeader.PayloadSize, ref _sessionBuffer, ref _sessionBufferCount);
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (!ShouldThrow(ex)) { }
            finally
            {
                await Close();
            }
        }

        private bool ShouldThrow(Exception ex)
        {
            if (ex is ObjectDisposedException
                || ex is InvalidOperationException
                || ex is SocketException
                || ex is IOException)
            {
                return false;
            }
            return true;
        }

        private void ConfigureClient()
        {
            _tcpClient.ReceiveBufferSize = _configuration.ReceiveBufferSize;
            _tcpClient.SendBufferSize = _configuration.SendBufferSize;
            _tcpClient.ReceiveTimeout = (int)_configuration.ReceiveTimeout.TotalMilliseconds;
            _tcpClient.SendTimeout = (int)_configuration.SendTimeout.TotalMilliseconds;
            _tcpClient.NoDelay = _configuration.NoDelay;
            _tcpClient.LingerState = _configuration.LingerState;
        }

        private async Task<Stream> NegotiateStream(Stream stream)
        {
            if (!_configuration.UseSsl)
                return stream;

            var validateRemoteCertificate = new RemoteCertificateValidationCallback(
                (object sender,
                X509Certificate certificate,
                X509Chain chain,
                SslPolicyErrors sslPolicyErrors)
                =>
                {
                    if (sslPolicyErrors == SslPolicyErrors.None)
                        return true;

                    if (_configuration.SslPolicyErrorsBypassed)
                        return true;
                    else
                        _log.ErrorFormat("Error occurred when validating remote certificate: [{0}], [{1}].",
                            this.RemoteEndPoint, sslPolicyErrors);

                    return false;
                });

            var sslStream = new SslStream(
                stream,
                false,
                validateRemoteCertificate,
                null,
                _configuration.SslEncryptionPolicy);

            await sslStream.AuthenticateAsClientAsync(
                _configuration.SslTargetHost, // The name of the server that will share this SslStream.
                _configuration.SslClientCertificates, // The X509CertificateCollection that contains client certificates.
                _configuration.SslEnabledProtocols, // The SslProtocols value that represents the protocol used for authentication.
                _configuration.SslCheckCertificateRevocation); // A Boolean value that specifies whether the certificate revocation list is checked during authentication.

            // When authentication succeeds, you must check the IsEncrypted and IsSigned properties 
            // to determine what security services are used by the SslStream. 
            // Check the IsMutuallyAuthenticated property to determine whether mutual authentication occurred.
            _log.DebugFormat(
                "Ssl Stream: SslProtocol[{0}], IsServer[{1}], IsAuthenticated[{2}], IsEncrypted[{3}], IsSigned[{4}], IsMutuallyAuthenticated[{5}], "
                + "HashAlgorithm[{6}], HashStrength[{7}], KeyExchangeAlgorithm[{8}], KeyExchangeStrength[{9}], CipherAlgorithm[{10}], CipherStrength[{11}].",
                sslStream.SslProtocol,
                sslStream.IsServer,
                sslStream.IsAuthenticated,
                sslStream.IsEncrypted,
                sslStream.IsSigned,
                sslStream.IsMutuallyAuthenticated,
                sslStream.HashAlgorithm,
                sslStream.HashStrength,
                sslStream.KeyExchangeAlgorithm,
                sslStream.KeyExchangeStrength,
                sslStream.CipherAlgorithm,
                sslStream.CipherStrength);

            return sslStream;
        }

        #endregion

        #region Send

        public async Task Send(byte[] data)
        {
            await Send(data, 0, data.Length);
        }

        public async Task Send(byte[] data, int offset, int count)
        {
            BufferValidator.ValidateBuffer(data, offset, count, "data");

            if (State != TcpSocketState.Connected)
            {
                throw new InvalidOperationException("This client has not connected to server.");
            }

            try
            {
                if (_stream.CanWrite)
                {
                    if (!_configuration.Framing)
                    {
                        await _stream.WriteAsync(data, offset, count);
                    }
                    else
                    {
                        var frame = TcpFrame.FromPayload(data, offset, count);
                        var frameBuffer = frame.ToArray();
                        await _stream.WriteAsync(frameBuffer, 0, frameBuffer.Length);
                    }
                }
            }
            catch (Exception ex) when (!ShouldThrow(ex)) { }
        }

        #endregion
    }
}
