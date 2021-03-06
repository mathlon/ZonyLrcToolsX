﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Zony.Lib.Infrastructures.Dependency;
using Zony.Lib.Infrastructures.EventBus.Factories;
using Zony.Lib.Infrastructures.EventBus.Factories.Internals;
using Zony.Lib.Infrastructures.EventBus.Handlers;
using Zony.Lib.Infrastructures.EventBus.Handlers.Internals;
using Zony.Lib.Infrastructures.Threading.Extensions;

namespace Zony.Lib.Infrastructures.EventBus
{
    public class EventBus : IEventBus
    {
        public static EventBus Default { get; } = new EventBus();

        public static void Init()
        {
            IocManager.Instance.IocContainer.Install(new EventBusInstaller(IocManager.Instance));
        }

        private readonly ConcurrentDictionary<Type, List<IEventHandlerFactory>> _handlerFactories;

        public EventBus()
        {
            _handlerFactories = new ConcurrentDictionary<Type, List<IEventHandlerFactory>>();
        }

        #region > 注册 <
        public IDisposable Register<TEventData>(Action<TEventData> action) where TEventData : IEventData
        {
            return Register(typeof(TEventData), new ActionEventHandler<TEventData>(action));
        }

        public IDisposable Register<TEventData>(IEventHandler<TEventData> handler) where TEventData : IEventData
        {
            return Register(typeof(TEventData), handler);
        }

        public IDisposable Register<TEventData, THandler>()
            where TEventData : IEventData
            where THandler : IEventHandler<TEventData>, new()
        {
            return Register(typeof(TEventData), new TransientEventHandlerFactory<THandler>());
        }

        public IDisposable Register(Type eventType, IEventHandler handlers)
        {
            return Register(eventType, new SingleInstanceHandlerFactory(handlers));
        }

        public IDisposable Register<TEventData>(IEventHandlerFactory handlerFactory) where TEventData : IEventData
        {
            return Register(typeof(TEventData), handlerFactory);
        }

        public IDisposable Register(Type eventType, IEventHandlerFactory handlerFactory)
        {
            GetOrCreateHandlerFactories(eventType).Locking(factories => factories.Add(handlerFactory));
            return new FactoryUnregistrar(this, eventType, handlerFactory);
        }

        #endregion

        #region > 取消注册 <
        public void Unregister<TEventData>(Action<TEventData> action) where TEventData : IEventData
        {
            GetOrCreateHandlerFactories(typeof(TEventData)).Locking(factories =>
            {
                factories.RemoveAll(factory =>
                {
                    var singleInstanceFactory = factory as SingleInstanceHandlerFactory;
                    if (singleInstanceFactory == null) return false;

                    var actionHandler = singleInstanceFactory.HandlerInstance as ActionEventHandler<TEventData>;
                    if (actionHandler == null) return false;

                    return actionHandler.Action == action;
                });
            });
        }

        public void Unregister<TEventData>(IEventHandler<TEventData> handler) where TEventData : IEventData
        {
            Unregister(typeof(TEventData), handler);
        }

        public void Unregister(Type eventType, IEventHandler handler)
        {
            GetOrCreateHandlerFactories(eventType).Locking(factories =>
            {
                factories.RemoveAll(factory =>
                {
                    return factory is SingleInstanceHandlerFactory && (factory as SingleInstanceHandlerFactory).HandlerInstance == handler;
                });
            });
        }

        public void Unregister<TEventData>(IEventHandlerFactory factory) where TEventData : IEventData
        {
            Unregister(typeof(TEventData), factory);
        }

        public void Unregister(Type eventType, IEventHandlerFactory factory)
        {
            GetOrCreateHandlerFactories(eventType).Locking(factories => factories.Remove(factory));
        }

        public void UnregisterAll<TEventData>() where TEventData : IEventData
        {
            UnregisterAll(typeof(TEventData));
        }

        public void UnregisterAll(Type eventType)
        {
            GetOrCreateHandlerFactories(eventType).Locking(factories => factories.Clear());
        }
        #endregion

        #region > 触发器 <
        public void Trigger<TEventData>(TEventData eventData) where TEventData : IEventData
        {
            Trigger((object)null, eventData);
        }

        public void Trigger<TEventData>(object eventSource, TEventData eventData) where TEventData : IEventData
        {
            Trigger(typeof(TEventData), eventSource, eventData);
        }

        public void Trigger(Type eventType, IEventData eventData)
        {
            Trigger(eventType, null, eventData);
        }

        public void Trigger(Type eventType, object eventSource, IEventData eventData)
        {
            var exceptions = new List<Exception>();

            TriggerHandlingException(eventType, eventSource, eventData, exceptions);

            if (exceptions.Any())
            {
                throw new AggregateException($"发生一个或者多个异常，调用失败，{eventType.Name}", exceptions);
            }

        }

        public Task TriggerAsync<TEventData>(TEventData eventData) where TEventData : IEventData
        {
            throw new NotImplementedException();
        }

        public Task TriggerAsync<TEventData>(object eventSource, TEventData eventData) where TEventData : IEventData
        {
            throw new NotImplementedException();
        }

        public Task TriggerAsync(Type eventType, IEventData eventData)
        {
            throw new NotImplementedException();
        }

        public Task TriggerAsync(Type eventType, object eventSource, IEventData eventData)
        {
            throw new NotImplementedException();
        }

        public void Trigger<TEventData>() where TEventData : IEventData
        {
            Trigger(typeof(TEventData), null);
        }

        public Task TriggerAsync<TEventData>() where TEventData : IEventData
        {
            throw new NotImplementedException();
        }
        #endregion

        /// <summary>
        /// 获取处理器工厂,
        /// 从工厂容器当中获得工厂。
        /// </summary>
        /// <param name="eventType">要获得的工厂的处理器类型</param>
        private List<IEventHandlerFactory> GetOrCreateHandlerFactories(Type eventType)
        {
            return _handlerFactories.GetOrAdd(eventType, (type) => new List<IEventHandlerFactory>());
        }

        private void TriggerHandlingException(Type eventType, object eventSource, IEventData eventData, List<Exception> exceptions)
        {
            if (eventData != null) eventData.EventSource = eventSource;

            foreach (var handlerFactories in GetHandlerFactories(eventType))
            {
                foreach (var handlerFactory in handlerFactories.EventHandlerFactories)
                {
                    var eventHandler = handlerFactory.GetHandler();

                    try
                    {
                        if (eventHandler == null) throw new Exception($"注册的处理器类型 {handlerFactories.EventType.Name} 未实现 IEventHandler 接口。");

                        var handlerType = typeof(IEventHandler<>).MakeGenericType(handlerFactories.EventType);
                        var method = handlerType.GetMethod("HandleEvent", new[] { handlerFactories.EventType });
                        method?.Invoke(eventHandler, new object[] { eventData });
                    }
                    catch (Exception e)
                    {
                        exceptions.Add(e);
                    }
                    finally
                    {
                        handlerFactory.ReleaseHandler(eventHandler);
                    }
                }
            }

            // TODO : 链式传递
            //if (eventType.GetTypeInfo().IsGenericType && eventType.GetGenericArguments().Length == 1)
            //{
            //    var _genericArg = eventType.GetGenericArguments()[0];
            //    var _baseArg = _genericArg.GetTypeInfo().BaseType;

            //    if (_baseArg != null)
            //    {
            //        var _baseEventType = eventType.GetGenericTypeDefinition().MakeGenericType(_baseArg);
            //    }
            //}
        }

        private IEnumerable<EventTypeWithEventHandlerFactories> GetHandlerFactories(Type eventType)
        {
            var handlerFactoryList = new List<EventTypeWithEventHandlerFactories>();

            foreach (var handlerFactory in _handlerFactories.Where(x => x.Key == eventType))
            {
                handlerFactoryList.Add(new EventTypeWithEventHandlerFactories(handlerFactory.Key, handlerFactory.Value));
            }

            return handlerFactoryList.ToArray();
        }

        private class EventTypeWithEventHandlerFactories
        {
            public Type EventType { get; }
            public List<IEventHandlerFactory> EventHandlerFactories { get; }

            public EventTypeWithEventHandlerFactories(Type eventType, List<IEventHandlerFactory> eventHandlerFactories)
            {
                EventType = eventType;
                EventHandlerFactories = eventHandlerFactories;
            }
        }
    }
}