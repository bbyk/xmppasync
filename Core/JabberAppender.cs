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
using System.Collections.Generic;
using System.Text;
using System.Threading;
using log4net.Appender;
using log4net.Core;

namespace XmppAsync
{
	public class JabberAppender : AppenderSkeleton
	{
		public string Hostname { get; set; }
		public int Port { get; set; }
		public string ServerName { get; set; }
		public string UserName { get; set; }
		public string Password { get; set; }
		public string Resource { get; set; }
		public int BufferSize { get; set; }
		public string To { get; set; }

		Queue<LoggingEvent> _buffer;
		AsyncJabberClient _jc;
		Timer _tl, _ts;
		int _obs,_inR;
		string[] _to;
		static readonly TimeSpan _tsPollInterval = TimeSpan.FromMilliseconds(50);
		static readonly TimeSpan _tlPollInterval = TimeSpan.FromSeconds(2);
		bool _initiated;

		public override void ActivateOptions()
		{
			base.ActivateOptions();

			if (Hostname == null || ServerName == null || UserName == null || Password == null || To == null)
			{
				ErrorHandler.Error("Unable to initialize the appender. Some of required parameters are missed. Ensure you set <hostname/>, <serverName/>, <userName/>, <password/> and <to/> parameters.");
				return;
			}

			if (Resource == null)
			{
				var sb = new StringBuilder();
				sb.Append(Environment.MachineName.ToLowerInvariant())
					.Append('.')
					.Append(Utils.ComputeSHA1Hash(AppDomain.CurrentDomain.BaseDirectory, Encoding.UTF8).Substring(0, 7));

				Resource = sb.ToString();
			}

			_jc = new AsyncJabberClient
			{
				Hostname = Hostname,
				Port = Port,
				ServerName = ServerName,
				UserName = UserName,
				Password = Password,
				Resource = Resource,
				ExProcessor = ex => ErrorHandler.Error("An unhandled error just occurred.", ex),
			};

			_tl = new Timer(OnNextLogin);
			_ts = new Timer(OnNextSend);

			_jc.Disconnected += delegate
			{
				OnNextLogin(null);
			};

			_obs = BufferSize == default(int) ? 10 : BufferSize;
			_to = To.Split(',');
			if (_to.Length == 0)
			{
				ErrorHandler.Error("Unable to initialize the appender. <to/> parameter is empty. You have to provide at least one entry here.");
				return;
			}
			_buffer = new Queue<LoggingEvent>(_obs);

			_initiated = true;

			OnNextLogin(null);
		}

		void OnNextLogin(object state)
		{
			try
			{
				_jc.BeginConnect(OnConnected, null);
			}
			catch (Exception ex)
			{
				ErrorHandler.Error("Unable to initiate login.", ex);
				AppointReconnect();
			}
		}

		void OnConnected(IAsyncResult ar)
		{
			try
			{
				_jc.EndConnect(ar);
				_jc.BeginLogin(OnLoggedIn, null);
			}
			catch (Exception ex)
			{
				ErrorHandler.Error("Unable to get logged in.", ex);
				AppointReconnect();
			}
		}

		void OnLoggedIn(IAsyncResult ar)
		{
			try
			{
				LoginResult res = _jc.EndLogin(ar);
				switch (res)
				{
					case LoginResult.Success:
						OnNextSend(null);
						break;
					case LoginResult.FailAuth:
						ErrorHandler.Error("Failed auth.");
						return;
					case LoginResult.BrokenProtocol:
						ErrorHandler.Error("Brocken protocol");
						return;
					default:
						_jc.Disconnect();
						AppointReconnect();
						break;
				}
			}
			catch (Exception ex)
			{
				ErrorHandler.Error("Unable to log in.", ex);
				_jc.Disconnect();
				AppointReconnect();
			}
		}

		void AppointReconnect()
		{
			_tl.Change(_tlPollInterval, TimeSpan.FromMilliseconds(-1));
		}

		void OnNextSend(object state)
		{
			if (!_jc.LoggedIn) return;
			if (Interlocked.CompareExchange(ref _inR, 1, 0) != 0) return;

			LoggingEvent @event = null;

			lock (_buffer)
			{
				if (_buffer.Count > 0)
				{
					@event = _buffer.Peek();
				}
			}

			if (@event == null)
			{
				AppointNextSend();
				return;
			}

			BeginSendMessage(@event, ar =>
			{
				bool shouldAppoint;

				try
				{
					EndSendMessage(ar);
					lock (_buffer)
					{
						_buffer.Dequeue();
					}

					shouldAppoint = false;
				}
				catch (Exception ex)
				{
					ErrorHandler.Error("Unable to send buffer", ex);
					shouldAppoint = true;
				}

				Interlocked.Exchange(ref _inR, 0);
				if (shouldAppoint) AppointNextSend(); else ThreadPool.QueueUserWorkItem(OnNextSend);
			},
			null);
		}

		void AppointNextSend()
		{
			Interlocked.Exchange(ref _inR, 0);
			_ts.Change(_tsPollInterval, TimeSpan.FromMilliseconds(-1));
		}

		protected override void Append(LoggingEvent loggingEvent)
		{
			if (!_initiated)
			{
				ErrorHandler.Error("The appender wasn't initiated gracefullly.");
				return;
			}

			if (loggingEvent == null)
			{
				ErrorHandler.Error("loggingEvent array is null");
				return;
			}

			if (_jc == null)
			{
				ErrorHandler.Error("Jabber client is not created.");
				return;
			}

			lock (_buffer)
			{
				if (_buffer.Count < _obs)
				{
					_buffer.Enqueue(loggingEvent);
				}
			}
		}

		IAsyncResult BeginSendMessage(LoggingEvent e, AsyncCallback cb, object state)
		{
			AsyncResult ar = new AsyncResult(cb, state);
			BeginSendMessage(RenderLoggingEvent(e), 0, ar);
			return ar;
		}

		void EndSendMessage(IAsyncResult ar)
		{
			AsyncResult.End<AsyncResult>(ar);
		}

		void BeginSendMessage(string msg, int i, AsyncResult rar)
		{
			try
			{
				_jc.BeginSendMessage(_to[i], msg, ar =>
				{
					try
					{
						_jc.EndSendMessage(ar);
					}
					catch (Exception ex)
					{
						rar.Complete(i == 0, ex);
						return;
					}

					if (++i >= _to.Length)
					{
						rar.Complete(false);
					}
					else
					{
						BeginSendMessage(msg, i, rar);
					}
				},
				null);
			}
			catch (Exception ex)
			{
				rar.Complete(i == 0, ex);
			}
		}

		protected override void OnClose()
		{
			base.OnClose();

			if (_jc.Connected)
			{
				try
				{
					IAsyncResult sar = _jc.BeginLogout(ar => {
						try
						{
							_jc.EndLogout(ar);
							_jc.Disconnect();
						}
						catch (Exception) { }
						finally
						{
							_jc.Dispose();
						}
					}, null);

					if (!sar.CompletedSynchronously)
					{
						// let's wait if we finished gracefully in a sychronous manner.
						sar.AsyncWaitHandle.WaitOne(200, false);
					}
				}
				catch (Exception ex)
				{
					ErrorHandler.Error("Unable to initiate closing of the appender gracefully.", ex);
				}
			}
		}
	}
}
