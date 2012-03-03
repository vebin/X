﻿using System;
using System.Collections.Generic;
using System.Threading;
using NewLife.Linq;

namespace NewLife.Messaging
{
    /// <summary>消息提供者接口</summary>
    public interface IMessageProvider
    {
        /// <summary>发送消息</summary>
        /// <param name="message"></param>
        void Send(Message message);

        /// <summary>消息到达时触发</summary>
        event EventHandler<MessageEventArgs> OnReceived;

        /// <summary>接收消息。</summary>
        /// <param name="millisecondsTimeout">等待的毫秒数，或为 <see cref="F:System.Threading.Timeout.Infinite" /> (-1)，表示无限期等待。默认0表示不等待</param>
        /// <returns></returns>
        Message Receive(Int32 millisecondsTimeout = 0);

        /// <summary>注册消息消费者，仅消费指定范围的消息</summary>
        /// <param name="start">消息范围的起始</param>
        /// <param name="end">消息范围的结束</param>
        /// <returns>消息消费者</returns>
        IMessageProvider Register(Byte start, Byte end);

        /// <summary>注册消息消费者，仅消费指定范围的消息</summary>
        /// <param name="kinds">消息类型的集合</param>
        /// <returns>消息消费者</returns>
        IMessageProvider Register(Byte[] kinds);
    }

    /// <summary>消息事件参数</summary>
    public class MessageEventArgs : EventArgs
    {
        private Message _Message;
        /// <summary>消息</summary>
        public Message Message { get { return _Message; } set { _Message = value; } }

        /// <summary>实例化</summary>
        /// <param name="message"></param>
        public MessageEventArgs(Message message) { Message = message; }
    }

    interface IMessageProvider2 : IMessageProvider
    {
        /// <summary>收到消息时调用该方法</summary>
        /// <param name="message"></param>
        void Process(Message message);
    }

    /// <summary>消息提供者基类</summary>
    public abstract class MessageProvider : DisposeBase, IMessageProvider2
    {
        #region 属性
        private IMessageProvider _Provider;
        /// <summary>消息提供者</summary>
        public IMessageProvider Provider { get { return _Provider; } set { _Provider = value; } }

        private Byte[] _Kinds;
        /// <summary>响应的消息类型集合</summary>
        public Byte[] Kinds { get { return _Kinds; } set { _Kinds = value; } }
        #endregion

        #region 基本收发
        /// <summary>发送消息</summary>
        /// <param name="message"></param>
        public abstract void Send(Message message);

        /// <summary>收到消息时调用该方法</summary>
        /// <param name="message"></param>
        public virtual void Process(Message message)
        {
            if (message == null) return;

            // 检查消息范围
            if (Kinds != null && Array.IndexOf(Kinds, message.Kind) < 0) return;

            // 为Receive准备的事件，只用一次
            EventHandler<MessageEventArgs> handler;
            do
            {
                handler = innerOnReceived;
            }
            while (handler != null && Interlocked.CompareExchange<EventHandler<MessageEventArgs>>(ref innerOnReceived, null, handler) != handler);

            if (handler != null) handler(this, new MessageEventArgs(message));

            if (OnReceived != null) OnReceived(this, new MessageEventArgs(message));

            // 记录已过期的，要删除
            var list = new List<WeakReference<IMessageProvider2>>();
            var cs = Consumers;
            foreach (var item in cs)
            {
                IMessageProvider2 mp;
                if (item.TryGetTarget(out mp) && mp != null)
                    mp.Process(message);
                else
                    list.Add(item);
            }

            if (list.Count > 0)
            {
                lock (cs)
                {
                    foreach (var item in list)
                    {
                        if (cs.Contains(item)) cs.Remove(item);
                    }
                }
            }
        }

        /// <summary>消息到达时触发。这里将得到所有消息</summary>
        public event EventHandler<MessageEventArgs> OnReceived;
        event EventHandler<MessageEventArgs> innerOnReceived;

        /// <summary>接收消息。这里将得到所有消息</summary>
        /// <param name="millisecondsTimeout">等待的毫秒数，或为 <see cref="F:System.Threading.Timeout.Infinite" /> (-1)，表示无限期等待。默认0表示不等待</param>
        /// <returns></returns>
        public virtual Message Receive(Int32 millisecondsTimeout = 0)
        {
            var _wait = new AutoResetEvent(true);
            _wait.Reset();

            Message msg = null;
            innerOnReceived += (s, e) => { msg = e.Message; _wait.Set(); };

            //if (!_wait.WaitOne(millisecondsTimeout, true)) return null;

            _wait.WaitOne(millisecondsTimeout, true);
            _wait.Close();

            return msg;
        }
        #endregion

        #region 注册消费者
        /// <summary>注册消息消费者，仅消费指定范围的消息</summary>
        /// <param name="start">消息范围的起始</param>
        /// <param name="end">消息范围的结束</param>
        /// <returns>消息消费者</returns>
        public virtual IMessageProvider Register(Byte start, Byte end)
        {
            if (start > end) throw new ArgumentOutOfRangeException("start", "起始不能大于结束！");
            return Register(Enumerable.Range(start, end - start + 1).Select(e => (Byte)e).ToArray());
        }

        /// <summary>注册消息消费者，仅消费指定范围的消息</summary>
        /// <param name="kinds">消息类型的集合</param>
        /// <returns>消息消费者</returns>
        public virtual IMessageProvider Register(Byte[] kinds)
        {
            if (kinds == null || kinds.Length < 1) throw new ArgumentNullException("kinds");
            kinds = kinds.Distinct().OrderBy(e => e).ToArray();
            if (kinds == null || kinds.Length < 1) throw new ArgumentNullException("kinds");

            // 检查注册范围是否有效
            var ks = Kinds;
            if (ks != null)
            {
                foreach (var item in kinds)
                {
                    if (Array.IndexOf(ks, item) < 0) throw new ArgumentOutOfRangeException("kinds", "当前消息提供者不支持Kind=" + item + "的消息！");
                }
            }

            var mc = new MessageConsumer() { Provider = this, Kinds = kinds };
            lock (Consumers)
            {
                Consumers.Add(mc);
            }
            mc.OnDisposed += (s, e) => Consumers.Remove(s as MessageConsumer);
            return mc;
        }

        private List<WeakReference<IMessageProvider2>> _Consumers = new List<WeakReference<IMessageProvider2>>();
        /// <summary>消费者集合</summary>
        private List<WeakReference<IMessageProvider2>> Consumers { get { return _Consumers; } }
        #endregion

        #region 消息消费者
        class MessageConsumer : MessageProvider
        {
            /// <summary>发送消息</summary>
            /// <param name="message"></param>
            public override void Send(Message message) { Provider.Send(message); }
        }
        #endregion
    }
}