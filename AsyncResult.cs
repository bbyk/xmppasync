using System;
using System.Diagnostics;
using System.Threading;

namespace XmppAsync
{
	/// <summary>
	/// A generic base class for IAsyncResult implementations
	/// that wraps a ManualResetEvent.
	/// </summary>
	class AsyncResult : IAsyncResult
	{
		AsyncCallback _callback;
		object _state;
		bool _completedSynchronously;
		bool _endCalled;
		Exception _exception;
		bool _isCompleted;
		ManualResetEvent _manualResetEvent;
		object _thisLock;

		internal AsyncResult(AsyncCallback callback, object state)
		{
			_callback = callback;
			_state = state;
			_thisLock = new object();
		}

		internal Exception Exception
		{
			get { return _exception; }
		}

		public object AsyncState
		{
			get
			{
				return _state;
			}
		}

        public WaitHandle AsyncWaitHandle
		{
			get
			{
				if (_manualResetEvent != null)
				{
					return _manualResetEvent;
				}

				lock (ThisLock)
				{
					if (_manualResetEvent == null)
					{
						_manualResetEvent = new ManualResetEvent(_isCompleted);
					}
				}

				return _manualResetEvent;
			}
		}

        public bool CompletedSynchronously
		{
			get
			{
				return _completedSynchronously;
			}
		}

        public bool IsCompleted
		{
			get
			{
				return _isCompleted;
			}
		}

		object ThisLock
		{
			get
			{
				return _thisLock;
			}
		}

		// Call this version of complete when your asynchronous operation is complete.  This will update the state
		// of the operation and notify the callback.
		internal void Complete(bool completedSynchronously)
		{
			if (_isCompleted)
			{
				// It's a bug to call Complete twice.
				throw new InvalidOperationException("Cannot call Complete twice");
			}

			_completedSynchronously = completedSynchronously;

			if (completedSynchronously)
			{
				// If we completedSynchronously, then there's no chance that the manualResetEvent was created so
				// we don't need to worry about a race
				Debug.Assert(_manualResetEvent == null, "No ManualResetEvent should be created for a synchronous AsyncResult.");
				_isCompleted = true;
			}
			else
			{
				lock (ThisLock)
				{
					_isCompleted = true;
					if (_manualResetEvent != null)
					{
						_manualResetEvent.Set();
					}
				}
			}

			// If the callback throws, there is a bug in the callback implementation
			if (_callback != null)
			{
				_callback(this);
			}
		}

		// Call this version of complete if you raise an exception during processing.  In addition to notifying
		// the callback, it will capture the exception and store it to be thrown during AsyncResult.End.
		internal void Complete(bool completedSynchronously, Exception exception)
		{
			_exception = exception;
			Complete(completedSynchronously);
		}

		// End should be called when the End function for the asynchronous operation is complete.  It
		// ensures the asynchronous operation is complete, and does some common validation.
		internal static TAsyncResult End<TAsyncResult>(IAsyncResult result)
			where TAsyncResult : AsyncResult
		{
			if (result == null)
			{
				throw new ArgumentNullException("result");
			}

			TAsyncResult asyncResult = result as TAsyncResult;

			if (asyncResult == null)
			{
				throw new ArgumentException("Invalid async result.", "result");
			}

			if (asyncResult._endCalled)
			{
				throw new InvalidOperationException("Async object already ended.");
			}

			asyncResult._endCalled = true;

			if (!asyncResult._isCompleted)
			{
				asyncResult.AsyncWaitHandle.WaitOne();
			}

			if (asyncResult._manualResetEvent != null)
			{
				asyncResult._manualResetEvent.Close();
			}

			if (asyncResult._exception != null)
			{
				throw asyncResult._exception;
			}

			return asyncResult;
		}
	}

	//An AsyncResult that completes as soon as it is instantiated.
	class CompletedAsyncResult : AsyncResult
	{
		internal CompletedAsyncResult(AsyncCallback callback, object state)
			: base(callback, state)
		{
			Complete(true);
		}

		internal static void End(IAsyncResult result)
		{
			AsyncResult.End<CompletedAsyncResult>(result);
		}
	}

	//A strongly typed AsyncResult
	class TypedAsyncResult<T> : AsyncResult
	{
		T _data;

		internal TypedAsyncResult(AsyncCallback callback, object state)
			: base(callback, state)
		{
		}

		internal T Data
		{
			get { return _data; }
		}

		internal void Complete(T data, bool completedSynchronously)
		{
			_data = data;
			Complete(completedSynchronously);
		}

		internal static T End(IAsyncResult result)
		{
			TypedAsyncResult<T> typedResult = AsyncResult.End<TypedAsyncResult<T>>(result);
			return typedResult.Data;
		}
	}

	//A strongly typed AsyncResult that completes as soon as it is instantiated.
	class TypedCompletedAsyncResult<T> : TypedAsyncResult<T>
	{
		internal TypedCompletedAsyncResult(T data, AsyncCallback callback, object state)
			: base(callback, state)
		{
			Complete(data, true);
		}

		internal new static T End(IAsyncResult result)
		{
			TypedCompletedAsyncResult<T> completedResult = result as TypedCompletedAsyncResult<T>;
			if (completedResult == null)
			{
				throw new ArgumentException("Invalid async result.", "result");
			}

			return TypedAsyncResult<T>.End(completedResult);
		}
	}
}
