using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace YTPlayer.Core.Reactive
{
    /// <summary>
    /// 可订阅的响应式字段包装器
    /// 支持线程安全的读写和值变化通知
    /// </summary>
    /// <typeparam name="T">字段值类型</typeparam>
    public sealed class ObservableField<T>
    {
        private T _value;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private readonly List<Action<T>> _subscribers = new List<Action<T>>();
        private readonly object _subscribersLock = new object();

        /// <summary>
        /// 创建可观察字段
        /// </summary>
        /// <param name="initialValue">初始值</param>
        public ObservableField(T initialValue = default(T))
        {
            _value = initialValue;
        }

        /// <summary>
        /// 获取或设置字段值
        /// 设置时如果值发生变化会异步通知所有订阅者
        /// </summary>
        public T Value
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    return _value;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
            set
            {
                Action<T>[] handlersToNotify = null;
                T newValue = value;
                bool valueChanged = false;

                _lock.EnterWriteLock();
                try
                {
                    // 检查值是否真正发生变化
                    if (!EqualityComparer<T>.Default.Equals(_value, newValue))
                    {
                        _value = newValue;
                        valueChanged = true;

                        // 复制订阅者列表（避免在通知期间死锁）
                        lock (_subscribersLock)
                        {
                            if (_subscribers.Count > 0)
                            {
                                handlersToNotify = _subscribers.ToArray();
                            }
                        }
                    }
                }
                finally
                {
                    _lock.ExitWriteLock();
                }

                // 在锁外异步通知订阅者
                if (valueChanged && handlersToNotify != null && handlersToNotify.Length > 0)
                {
                    Task.Run(() =>
                    {
                        foreach (var handler in handlersToNotify)
                        {
                            try
                            {
                                handler?.Invoke(newValue);
                            }
                            catch (Exception ex)
                            {
                                // 订阅者异常不应影响其他订阅者
                                Utils.DebugLogger.Log(Utils.LogLevel.Error, "ObservableField",
                                    $"Subscriber callback exception: {ex.Message}");
                            }
                        }
                    });
                }
            }
        }

        /// <summary>
        /// 订阅值变化事件
        /// </summary>
        /// <param name="onChange">值变化时的回调函数</param>
        /// <returns>取消订阅令牌（Dispose时取消订阅）</returns>
        public IDisposable Subscribe(Action<T> onChange)
        {
            if (onChange == null)
                throw new ArgumentNullException(nameof(onChange));

            lock (_subscribersLock)
            {
                _subscribers.Add(onChange);
            }

            return new Subscription(this, onChange);
        }

        /// <summary>
        /// 取消订阅
        /// </summary>
        private void Unsubscribe(Action<T> onChange)
        {
            lock (_subscribersLock)
            {
                _subscribers.Remove(onChange);
            }
        }

        /// <summary>
        /// 订阅令牌（用于取消订阅）
        /// </summary>
        private sealed class Subscription : IDisposable
        {
            private ObservableField<T> _field;
            private Action<T> _callback;

            public Subscription(ObservableField<T> field, Action<T> callback)
            {
                _field = field;
                _callback = callback;
            }

            public void Dispose()
            {
                if (_field != null && _callback != null)
                {
                    _field.Unsubscribe(_callback);
                    _field = null;
                    _callback = null;
                }
            }
        }

        /// <summary>
        /// 获取当前订阅者数量（用于调试）
        /// </summary>
        public int SubscriberCount
        {
            get
            {
                lock (_subscribersLock)
                {
                    return _subscribers.Count;
                }
            }
        }

        /// <summary>
        /// 静态方法：尝试获取值（线程安全）
        /// </summary>
        public bool TryGetValue(out T value)
        {
            _lock.EnterReadLock();
            try
            {
                value = _value;
                return true;
            }
            catch
            {
                value = default(T);
                return false;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 强制通知所有订阅者（即使值未变化）
        /// </summary>
        public void NotifySubscribers()
        {
            Action<T>[] handlersToNotify;
            T currentValue;

            _lock.EnterReadLock();
            try
            {
                currentValue = _value;
            }
            finally
            {
                _lock.ExitReadLock();
            }

            lock (_subscribersLock)
            {
                if (_subscribers.Count == 0)
                    return;

                handlersToNotify = _subscribers.ToArray();
            }

            Task.Run(() =>
            {
                foreach (var handler in handlersToNotify)
                {
                    try
                    {
                        handler?.Invoke(currentValue);
                    }
                    catch (Exception ex)
                    {
                        Utils.DebugLogger.Log(Utils.LogLevel.Error, "ObservableField",
                            $"Subscriber callback exception during forced notification: {ex.Message}");
                    }
                }
            });
        }
    }
}
