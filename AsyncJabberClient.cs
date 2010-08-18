#region Copyright 2010 Boris Byk.
/*
 * Copyright 2010 Boris Byk.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may
 * not use this file except in compliance with the License. You may obtain
 * a copy of the License at
 *
 *  http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations
 * under the License.
 */
#endregion

using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Xml;

namespace XmppAsync
{
	public enum LoginResult
	{
		Success,
		FailAuth,
		ServerBroke,
		BrokenProtocol,
	}

	public class AsyncJabberClient : IDisposable
	{
		string _hostname;

		public string Hostname
		{
			get { return _hostname; }
			set { _hostname = value; }
		}

		int _port;

		public int Port
		{
			get { return _port; }
			set { _port = value; }
		}

		string _userName;

		public string UserName
		{
			get { return _userName; }
			set { _userName = value; }
		}

		string _password;

		public string Password
		{
			get { return _password; }
			set { _password = value; }
		}

		string _serverName;

		public string ServerName
		{
			get { return _serverName; }
			set { _serverName = value; }
		}

		string _resource;

		public string Resource
		{
			get { return _resource; }
			set { _resource = value; }
		}

		Action<Exception> _exProcessor;

		public Action<Exception> ExProcessor
		{
			get { return _exProcessor; }
			set { _exProcessor = value; }
		}

		public event EventHandler Disconnected;

		Socket _socket;
		byte[] _buffer;
		Encoding _encoding;
		char[] _charBuffer;
		StringBuilder _sb;
		Decoder _dec;
		bool _disposed, _disconnected = true, _loggedIn;
		object _syncObj = new Object();
		Filter _filter;
		SocketAsyncEventArgs _readArgs, _writeArgs;

		public AsyncJabberClient()
		{
			_encoding = new UTF8Encoding(false, true);
			_dec = _encoding.GetDecoder();
		}

		private void EnsureBuffer()
		{
			if (_buffer == null)
			{
				_buffer = new byte[_socket.ReceiveBufferSize];
				int maxCharsPerBuffer = _encoding.GetMaxCharCount(_buffer.Length);
				_charBuffer = new char[maxCharsPerBuffer];
				_sb = new StringBuilder();

				_readArgs = new SocketAsyncEventArgs();
				_readArgs.SetBuffer(_buffer, 0, _buffer.Length);
				_readArgs.Completed += OnReadBuffer;
			}
		}

		private void ProcessException(AsyncResult ar, SocketException ex)
		{
			if (ar.IsCompleted) return;
			Disconnect(false);
			ar.Complete(false, ex);
		}

		private void EnsureCredentials()
		{
			if (_hostname == null) throw new ArgumentNullException("Hostname");
			if (_password == null) throw new ArgumentNullException("Password");
			if (_userName == null) throw new ArgumentNullException("UserName");
			if (_serverName == null) throw new ArgumentNullException("ServerName");
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="cb"></param>
		/// <param name="state"></param>
		/// <returns></returns>
		/// <exception cref="SecurityException"></exception>
		/// <exception cref="ObjectDisposedException">The underlying socket has been closed.</exception>
		/// <exception cref="ArgumentNullException">host is null.</exception>
		/// <exception cref="SocketException">An error is encountered when resolving Hostname or connecting.</exception>
		/// <exception cref="ArgumentException">Hostname is an invalid IP address.</exception>
		/// <exception cref="ArgumentOutOfRangeException">The port number is not valid.</exception>
		public IAsyncResult BeginConnect(AsyncCallback cb, object state)
		{
			if (_disposed) throw new ObjectDisposedException(GetType().FullName);
			EnsureCredentials();

			var ar = new AsyncResult(WrapCallback(cb), state);

			_socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

			IPAddress addr = Dns.GetHostAddresses(_hostname).Where(a => a.AddressFamily == AddressFamily.InterNetwork).First();
			if (_writeArgs == null) _writeArgs = new SocketAsyncEventArgs();
			_writeArgs.RemoteEndPoint = new IPEndPoint(addr, _port);

			_writeArgs.Completed += OnConnected;
			_writeArgs.UserToken = ar;
			if (!_socket.ConnectAsync(_writeArgs))
			{
				OnConnected(_socket, _writeArgs);
			}

			return ar;
		}

		public void EndConnect(IAsyncResult ar)
		{
			AsyncResult.End<AsyncResult>(ar);
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="cb"></param>
		/// <param name="state"></param>
		/// <returns></returns>
		/// <exception cref="ObjectDisposedException">The underlying socket has been closed.</exception>
		/// <exception cref="ArgumentNullException">host is null.</exception>
		/// <exception cref="SocketException">An error is encountered when resolving Hostname or connecting.</exception>
		public IAsyncResult BeginLogin(AsyncCallback cb, object state)
		{
			if (_disposed) throw new ObjectDisposedException(GetType().FullName);
			if (_disconnected) throw ErrorWeAreDisconnected();

			var ar = new TypedAsyncResult<LoginResult>(WrapCallback(cb), state);

			string openStream = String.Format("<stream:stream to='{0}' xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams'>", _serverName);
			byte[] buffer = _encoding.GetBytes(openStream);
			_writeArgs.Completed += OnSentOpenStream;
			_writeArgs.SetBuffer(buffer, 0, buffer.Length);
			_writeArgs.UserToken = ar;
			if (!_socket.SendAsync(_writeArgs))
			{
				OnSentOpenStream(_socket, _writeArgs);
			}

			return ar;
		}

		private AsyncCallback WrapCallback(AsyncCallback cb)
		{
			if (cb == null) return cb;

			return ar =>
			{
				try
				{
					cb(ar);
				}
				catch (Exception ex)
				{
					if (_exProcessor == null) throw; else _exProcessor(ex);
				}
			};
		}

		/// <summary>
		/// 
		/// </summary>
		/// <exception cref="ArgumentNullException"></exception>
		/// <exception cref="ObjectDisposedException"></exception>
		/// <exception cref="ArgumentException"></exception>
		/// <exception cref="InvalidOperationException"></exception>
		/// <exception cref="SocketException"></exception>
		public LoginResult EndLogin(IAsyncResult ar)
		{
			return TypedAsyncResult<LoginResult>.End(ar);
		}

		private void OnConnected(object sender, SocketAsyncEventArgs e)
		{
			e.Completed -= OnConnected;
			if (_disposed) return;

			var ar = (AsyncResult)e.UserToken;

			try
			{
				if (e.SocketError != SocketError.Success)
				{
					throw new SocketException((int)e.SocketError);
				}

				_disconnected = false;
			}
			catch (SocketException ex)
			{
				ProcessException(ar, ex);
				return;
			}

			ar.Complete(false);
		}

		private void OnSentOpenStream(object sender, SocketAsyncEventArgs e)
		{
			e.Completed -= OnSentOpenStream;
			if (_disposed || _disconnected) return;

			var tar = (TypedAsyncResult<LoginResult>)e.UserToken;

			try
			{
				if (e.SocketError != SocketError.Success)
				{
					throw new SocketException((int)e.SocketError);
				}

				EnsureBuffer();

				_filter = new Filter { Ar = tar, Complete = false, Node = "stream:stream", ResultCallback = OnSendingAuth, ErrorCallback = (filter, ex) => ProcessException((TypedAsyncResult<LoginResult>)filter.Ar, ex), ZeroReadCallback = filter => ((TypedAsyncResult<LoginResult>)filter.Ar).Complete(LoginResult.ServerBroke, false) };
				if (!_socket.ReceiveAsync(_readArgs))
				{
					OnReadBuffer(_socket, _readArgs);
				}
			}
			catch (SocketException ex)
			{
				ProcessException(tar, ex);
			}
		}

		private void OnSendingAuth(Filter filter)
		{
			if (_disposed || _disconnected) return;
			var tar = (TypedAsyncResult<LoginResult>)filter.Ar;

			try
			{
				var doc = new XmlDocument();
				doc.LoadXml(filter.Excerpt + "</stream:stream>");

				XmlAttribute streamId = doc.DocumentElement.Attributes["id"];
				if (streamId == null)
				{
					tar.Complete(LoginResult.BrokenProtocol, false);
					return;
				}

				string id = NewId();

                string auth = String.Format(@"<iq id='{0}' type='set'><query xmlns='jabber:iq:auth'><username>{1}</username><digest>{2}</digest><resource>{3}</resource></query></iq>", id, _userName, Utils.ComputeSHA1Hash(streamId.Value + _password, _encoding), _resource ?? Guid.NewGuid().ToString("N"));
				byte[] buffer = _encoding.GetBytes(auth);
				_writeArgs.SetBuffer(buffer, 0, buffer.Length);
				_writeArgs.Completed += OnSentAuth;
				_writeArgs.UserToken = new object[] { tar, id };
				if (!_socket.SendAsync(_writeArgs))
				{
					OnSentAuth(_socket, _writeArgs);
				}
			}
			catch (SocketException ex)
			{
				ProcessException(tar, ex);
			}
		}

		private void OnSentAuth(object sender, SocketAsyncEventArgs e)
		{
			e.Completed -= OnSentAuth;
			if (_disposed || _disconnected) return;

			var pack = (object[])e.UserToken;
			var tar = (TypedAsyncResult<LoginResult>)pack[0];
			var id = (string)pack[1];

			try
			{
				if (e.SocketError != SocketError.Success)
				{
					throw new SocketException((int)e.SocketError);
				}


				_filter = new Filter { Ar = tar, Complete = true, Node = "iq", ResultCallback = OnReadAuthResult, Id = id, ErrorCallback = (filter, ex) => ProcessException((TypedAsyncResult<LoginResult>)filter.Ar, ex), ZeroReadCallback = filter => ((TypedAsyncResult<LoginResult>)filter.Ar).Complete(LoginResult.ServerBroke, false) };
				if (!_socket.ReceiveAsync(_readArgs))
				{
					OnReadBuffer(_socket, _readArgs);
				}
			}
			catch (SocketException ex)
			{
				ProcessException(tar, ex);
			}
		}

		private void OnReadAuthResult(Filter filter)
		{
			if (_disposed || _disconnected) return;

			var tar = (TypedAsyncResult<LoginResult>)filter.Ar;

			var doc = new XmlDocument();
			doc.LoadXml(filter.Excerpt);

			XmlAttribute attr = doc.DocumentElement.Attributes["id"];
			if (attr == null)
			{
				tar.Complete(LoginResult.BrokenProtocol, false);
				return;
			}

			if (attr.Value != filter.Id)
			{
				try
				{
					if (!_socket.ReceiveAsync(_readArgs))
					{
						ThreadPool.QueueUserWorkItem(st => OnReadBuffer(_socket, _readArgs));
					}
				}
				catch (SocketException ex)
				{
					ProcessException(tar, ex);
					return;
				}
			}

			attr = doc.DocumentElement.Attributes["type"];
			if (attr == null)
			{
				tar.Complete(LoginResult.BrokenProtocol, false);
				return;
			}
			var res = attr.Value == "error" ? LoginResult.FailAuth : LoginResult.Success;

			if (res == LoginResult.Success)
			{
				byte[] buffer = _encoding.GetBytes("<presence><status>ready</status><show>ready</show></presence>");

				try
				{
					_writeArgs.Completed += OnSentPresence;
					_writeArgs.SetBuffer(buffer, 0, buffer.Length);
					_writeArgs.UserToken = tar;
					if (!_socket.SendAsync(_writeArgs))
					{
						OnSentPresence(_socket, _writeArgs);
					}
				}
				catch (SocketException ex)
				{
					ProcessException(tar, ex);
				}
			}
			else
			{
				tar.Complete(res, false);
			}
		}

		private void OnSentPresence(object sender, SocketAsyncEventArgs e)
		{
			e.Completed -= OnSentPresence;
			if (_disposed || _disconnected) return;

			var tar = (TypedAsyncResult<LoginResult>)e.UserToken;

			try
			{
				if (e.SocketError != SocketError.Success)
				{
					throw new SocketException((int)e.SocketError);
				}

				// in the complete callback we can set filter. so we try to set it to null just before.
				_filter = null;
				_loggedIn = true;
			}
			catch (SocketException ex)
			{
				ProcessException(tar, ex);
				return;
			}

			tar.Complete(LoginResult.Success, false);

			try
			{
				if (!_socket.ReceiveAsync(_readArgs))
				{
					OnReadBuffer(this, _readArgs);
				}
			}
			catch (SocketException ex)
			{
				ProcessException(tar, ex);
			}
		}

		private static String NewId()
		{
			return Guid.NewGuid().ToString("N");
		}

		private void OnReadBuffer(object sender, SocketAsyncEventArgs e)
		{
			if (_disposed || _disconnected) return;

			Filter filter = _filter;

			try
			{
				if (e.SocketError != SocketError.Success)
				{
					throw new SocketException((int)e.SocketError);
				}

				int num = e.BytesTransferred;

				if (num == 0)
				{
					if (filter != null && filter.ZeroReadCallback != null)
					{
						filter.ZeroReadCallback(filter);
					}
					else
					{
						Disconnect(false);
					}

					return;
				}
				else if (filter != null && filter.Node != null)
				{
					int charCount = _dec.GetChars(e.Buffer, e.Offset, num, _charBuffer, 0, false);
					_sb.Append(_charBuffer, 0, charCount);

					if (HasResult(filter))
					{
						filter.ResultCallback(filter);
						return;
					}
				}

				if (!_socket.ReceiveAsync(e))
				{
					ThreadPool.QueueUserWorkItem(st => OnReadBuffer(this, e));
				}
			}
			catch (InvalidOperationException)
			{
				// the stream is disposed in an another IOCP thread or a controlling thread.
				return;
			}
			catch (SocketException ex)
			{
				if (filter != null && filter.ErrorCallback != null)
				{
					filter.ErrorCallback(filter, ex);
				}
				else
				{
					Disconnect(false);
				}
			}
		}

		private void ThreadPoolOnReadBuffer(AsyncJabberClient asyncJabberClient, SocketAsyncEventArgs e)
		{
			throw new NotImplementedException();
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="to"></param>
		/// <param name="message"></param>
		/// <param name="cb"></param>
		/// <param name="state"></param>
		/// <returns></returns>
		/// <exception cref="ObjectDisposedException"></exception>
		/// <exception cref="ArgumentNullException"></exception>
		/// <exception cref="SocketException"></exception>
		/// <exception cref="InvalidOperationException"></exception>
		public IAsyncResult BeginSendMessage(string to, string message, AsyncCallback cb, object state)
		{
			if (_disposed) throw new ObjectDisposedException(GetType().FullName);
			if (_disconnected) throw ErrorWeAreDisconnected();
			if (!_loggedIn) throw ErrorWeAreNotLoggedIn();
			if (to == null) throw new ArgumentNullException("to");
			if (message == null) throw new ArgumentNullException("message");

			var sb = new StringBuilder();
			var settings = new XmlWriterSettings();
			settings.ConformanceLevel = ConformanceLevel.Fragment;
			settings.Indent = false;

			using (XmlWriter wr = XmlWriter.Create(sb, settings))
			{
				wr.WriteStartElement("message", "jabber:client");
				wr.WriteAttributeString("type", "chat");
				wr.WriteAttributeString("to", String.Format("{0}@{1}", to, _serverName));
				wr.WriteStartElement("body");
				wr.WriteString(message);
				wr.WriteEndElement();
				wr.WriteEndElement();
			}

			var buffer = _encoding.GetBytes(sb.ToString());
			var ar = new AsyncResult(WrapCallback(cb), state);

			_writeArgs.Completed += OnSentMessage;
			_writeArgs.UserToken = ar;
			_writeArgs.SetBuffer(buffer, 0, buffer.Length);
			if (!_socket.SendAsync(_writeArgs))
			{
				OnSentMessage(_socket, _writeArgs);
			}

			return ar;
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="ar"></param>
		/// <exception cref="ArgumentNullException"></exception>
		/// <exception cref="ObjectDisposedException"></exception>
		/// <exception cref="ArgumentException"></exception>
		/// <exception cref="SocketException"></exception>		
		/// <exception cref="InvalidOperationException"></exception>
		public void EndSendMessage(IAsyncResult ar)
		{
			AsyncResult.End<AsyncResult>(ar);
		}

		public bool Connected { get { return !_disconnected; } }

		public bool LoggedIn { get { return _loggedIn; } }

		private void OnSentMessage(object sender, SocketAsyncEventArgs e)
		{
			e.Completed -= OnSentMessage;
			var tar = (AsyncResult)e.UserToken;

			try
			{
				if (e.SocketError != SocketError.Success)
				{
					throw new SocketException((int)e.SocketError);
				}
			}
			catch (SocketException ex)
			{
				ProcessException(tar, ex);
				return;
			}

			tar.Complete(false);
		}

		private bool HasResult(Filter filter)
		{
			try
			{
				var str = _sb.ToString();
				var ind = str.IndexOf('<' + filter.Node);
				if (ind == -1) return false;
				filter.Pointer = ind;

				if (!filter.Complete)
				{
					ind = str.IndexOf('>', ind);
					if (ind == -1) return false;

					var len = ind - filter.Pointer + 1;
					filter.Excerpt = str.Substring(filter.Pointer, len);
					filter.Pointer += len;
					return true;
				}

				int ind1 = str.IndexOf("</" + filter.Node, ind);
				if (ind1 != -1)
				{
					var len = ind1 - filter.Pointer + 3 + filter.Node.Length;
					filter.Excerpt = str.Substring(filter.Pointer, len);
					filter.Pointer += len;
					return true;
				}

				ind1 = str.IndexOf('>', ind);
				if (ind1 == -1) return false;

				if (str[--ind1] == '/')
				{
					var len = ind1 - filter.Pointer + 2;
					filter.Excerpt = str.Substring(filter.Pointer, len);
					filter.Pointer += len;
					return true;
				}

				return false;
			}
			finally
			{
				_sb.Remove(0, filter.Pointer);
			}
		}

		private static InvalidOperationException ErrorWeAreDisconnected()
		{
			return new InvalidOperationException("We are not connected.");
		}

		private static InvalidOperationException ErrorWeAreNotLoggedIn()
		{
			return new InvalidOperationException("We are not logged in.");
		}

		class Filter
		{
			public IAsyncResult Ar;
			public string Node;
			public bool Complete;
			public Action<Filter> ResultCallback;
			public Action<Filter, SocketException> ErrorCallback;
			public Action<Filter> ZeroReadCallback;
			public string Id;
			public string Excerpt;
			public int Pointer;
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="cb"></param>
		/// <param name="state"></param>
		/// <returns></returns>
		/// <exception cref="ObjectDisposedException"></exception>
		/// <exception cref="InvalidOperationException"></exception>
		/// <exception cref="SocketException"></exception>
		public IAsyncResult BeginLogout(AsyncCallback cb, object state)
		{
			if (_disposed) throw new ObjectDisposedException(GetType().FullName);
			if (_disconnected) throw ErrorWeAreDisconnected();

			var ar = new AsyncResult(WrapCallback(cb), state);
			byte[] buffer = _encoding.GetBytes("</stream:stream>");
			_writeArgs.SetBuffer(buffer, 0, buffer.Length);
			_writeArgs.Completed += OnEndStreamSent;
			_writeArgs.UserToken = ar;
			if (!_socket.SendAsync(_writeArgs))
			{
				OnEndStreamSent(_socket, _writeArgs);
			}
			return ar;
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="ar"></param>
		/// <exception cref="ArgumentNullException"></exception>
		/// <exception cref="ObjectDisposedException"></exception>
		/// <exception cref="ArgumentException"></exception>
		/// <exception cref="InvalidOperationException"></exception>
		/// <exception cref="SocketException"></exception>
		public void EndLogout(IAsyncResult ar)
		{
			AsyncResult.End<AsyncResult>(ar);
		}

		private void OnEndStreamSent(object sender, SocketAsyncEventArgs e)
		{
			e.Completed -= OnEndStreamSent;
			if (_disposed || _disconnected) return;

			var tar = (AsyncResult)e.UserToken;

			try
			{
				if (e.SocketError != SocketError.Success)
				{
					throw new SocketException((int)e.SocketError);
				}
			}
			catch (SocketException ex)
			{
				ProcessException(tar, ex);
			}

			_loggedIn = false;
			tar.Complete(false);
		}

		#region IDisposable Members

		public void Dispose()
		{
			// Iternal read IOCP thread can call this method along with a controlling thread.
			if (_disposed)
			{
				return;
			}

			// it shutdowns and close the socket.
			_socket.Close();
			_loggedIn = false;
			_disconnected = true;

			_disposed = true;
		}

		#endregion

		public void Disconnect()
		{
			Disconnect(true);
		}

		private void Disconnect(bool noraise)
		{
			// Iternal read IOCP thread can call this method along with a controlling thread.
			lock (_syncObj)
			{
				if (_disconnected) return;

				try
				{
					_socket.Shutdown(SocketShutdown.Both);
				}
				catch (SocketException) { }

				_socket.Close();

				bool flag = _loggedIn;
				_loggedIn = false;
				_disconnected = true;

				if (flag && !noraise)
				{
					var eh = Disconnected;
					if (eh != null) eh.BeginInvoke(this, EventArgs.Empty, null, null);
				}
			}
		}
	}
}
