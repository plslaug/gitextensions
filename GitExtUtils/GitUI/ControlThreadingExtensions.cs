﻿using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.Threading;

namespace GitUI
{
    public static class ControlThreadingExtensions
    {
        private static readonly CancellationToken _preCancelledToken;
        private static readonly ConditionalWeakTable<IComponent, StrongBox<CancellationToken>> _controlDisposed;

        static ControlThreadingExtensions()
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                _preCancelledToken = cts.Token;
            }

            _controlDisposed = new ConditionalWeakTable<IComponent, StrongBox<CancellationToken>>();
        }

        public static ControlMainThreadAwaitable SwitchToMainThreadAsync(this ToolStripItem control)
        {
            if (control.IsDisposed)
            {
                return new ControlMainThreadAwaitable(ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_preCancelledToken), _preCancelledToken);
            }

            var disposedCancellationToken = ToolStripItemDisposedCancellationFactory.Instance.GetOrCreateCancellationToken(control);
            var mainThreadAwaiter = ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(disposedCancellationToken);
            return new ControlMainThreadAwaitable(mainThreadAwaiter, disposedCancellationToken);
        }

        public static ControlMainThreadAwaitable SwitchToMainThreadAsync(this Control control)
        {
            if (control.IsDisposed)
            {
                return new ControlMainThreadAwaitable(ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_preCancelledToken), _preCancelledToken);
            }

            var disposedCancellationToken = ControlIsDisposedCancellationFactory.Instance.GetOrCreateCancellationToken(control);
            var mainThreadAwaiter = ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(disposedCancellationToken);
            return new ControlMainThreadAwaitable(mainThreadAwaiter, disposedCancellationToken);
        }

        public struct ControlMainThreadAwaitable
        {
            private readonly JoinableTaskFactory.MainThreadAwaitable _awaitable;
            private readonly CancellationToken _cancellationToken;

            internal ControlMainThreadAwaitable(JoinableTaskFactory.MainThreadAwaitable awaitable, CancellationToken cancellationToken)
            {
                _awaitable = awaitable;
                _cancellationToken = cancellationToken;
            }

            public ControlMainThreadAwaiter GetAwaiter()
            {
                return new ControlMainThreadAwaiter(_awaitable.GetAwaiter(), _cancellationToken);
            }
        }

        public struct ControlMainThreadAwaiter : INotifyCompletion
        {
            private readonly JoinableTaskFactory.MainThreadAwaiter _awaiter;
            private readonly CancellationToken _cancellationToken;

            internal ControlMainThreadAwaiter(JoinableTaskFactory.MainThreadAwaiter awaiter, CancellationToken cancellationToken)
            {
                _awaiter = awaiter;
                _cancellationToken = cancellationToken;
            }

            public bool IsCompleted => _awaiter.IsCompleted;

            public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

            public void GetResult()
            {
                _awaiter.GetResult();

                // The default MainThreadAwaiter only throws an exception if we fail to reach the main thread. This call
                // ensures we always cancel the continuation if we somehow reach the UI thread after the control was
                // disposed.
                _cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private sealed class ControlIsDisposedCancellationFactory : IsDisposedCancellationFactory<Control>
        {
            public static readonly ControlIsDisposedCancellationFactory Instance = new ControlIsDisposedCancellationFactory();

            protected override bool IsDisposed(Control component) => component.IsDisposed;
        }

        private sealed class ToolStripItemDisposedCancellationFactory : IsDisposedCancellationFactory<ToolStripItem>
        {
            public static readonly ToolStripItemDisposedCancellationFactory Instance = new ToolStripItemDisposedCancellationFactory();

            protected override bool IsDisposed(ToolStripItem component) => component.IsDisposed;
        }

        private abstract class IsDisposedCancellationFactory<T>
            where T : class, IComponent
        {
            private readonly ConditionalWeakTable<IComponent, StrongBox<CancellationToken>>.CreateValueCallback _disposedCancellationTokenFactory;

            protected IsDisposedCancellationFactory()
            {
                _disposedCancellationTokenFactory = control =>
                {
                    if (IsDisposed((T)control))
                    {
                        return new StrongBox<CancellationToken>(_preCancelledToken);
                    }

                    var cts = new CancellationTokenSource();

                    // Get a copy of the CancellationToken before the source can be disposed. After the source is cancelled
                    // and disposed, the CancellationToken will continue to behave properly, but
                    // CancellationTokenSource.Token will start to throw an ObjectDisposedException.
                    var token = cts.Token;

                    control.Disposed += delegate
                    {
                        CancelAndDispose(cts);
                    };

                    if (IsDisposed((T)control))
                    {
                        // Handle control disposed on another thread while registering event handler
                        CancelAndDispose(cts);
                    }

                    return new StrongBox<CancellationToken>(token);
                };
            }

            public CancellationToken GetOrCreateCancellationToken(T component)
            {
                return _controlDisposed.GetValue(component, _disposedCancellationTokenFactory).Value;
            }

            protected abstract bool IsDisposed(T component);

            private static void CancelAndDispose(CancellationTokenSource cancellationTokenSource)
            {
                try
                {
                    cancellationTokenSource.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // This can occur in race conditions
                }

                cancellationTokenSource.Dispose();
            }
        }
    }
}
