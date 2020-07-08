﻿using JMS.GenerateCode;
using JMS.Impls;
using JMS.Interfaces;
using JMS.ScheduleTask;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Way.Lib;
using System.Reflection;
using JMS.Common.Dtos;

namespace JMS
{
    public class MicroServiceHost
    {
        public string Id { get; private set; }
        ILogger<MicroServiceHost> _logger;
        IGatewayConnector _GatewayConnector;
        internal IGatewayConnector GatewayConnector => _GatewayConnector;
        public NetAddress MasterGatewayAddress { internal set; get; }
        public NetAddress[] AllGatewayAddresses { get; private set; }

        internal Dictionary<string, ControllerTypeInfo> ServiceNames = new Dictionary<string, ControllerTypeInfo>();
        internal int ServicePort;
        internal MicroServiceOption Option;
        /// <summary>
        /// 当前处理中的请求数
        /// </summary>
        internal int ClientConnected;
        public IServiceProvider ServiceProvider { private set; get; }
        ServiceCollection _services;
        IRequestReception _RequestReception;
        ScheduleTaskManager _scheduleTaskManager;
        bool _registerMyServicesed = false;
        public MicroServiceHost(ServiceCollection services)
        {
            this.Id = Guid.NewGuid().ToString("N");
            _services = services;
            _scheduleTaskManager = new ScheduleTaskManager(this);
        }

        internal void DisconnectGateway()
        {
            _GatewayConnector.DisconnectGateway();
        }


        /// <summary>
        /// 向网关注册服务
        /// </summary>
        /// <typeparam name="T">Controller</typeparam>
        /// <param name="serviceName">服务名称</param>
        public void Register<T>(string serviceName) where T : MicroServiceControllerBase
        {
            this.Register(typeof(T), serviceName);
        }

        /// <summary>
        /// 向网关注册服务
        /// </summary>
        /// <param name="contollerType">Controller类型</param>
        /// <param name="serviceName">服务名称</param>
        public void Register(Type contollerType, string serviceName)
        {
            registerServices();

            _services.AddTransient(contollerType);
            ServiceNames[serviceName] = new ControllerTypeInfo()
            {
                Type = contollerType,
                Methods = contollerType.GetTypeInfo().DeclaredMethods.Where(m =>
                    m.IsStatic == false &&
                    m.IsPublic &&
                    m.DeclaringType != typeof(MicroServiceControllerBase)
                    && m.DeclaringType != typeof(object)).ToArray()
            };
        }

        /// <summary>
        /// 注册定时任务
        /// </summary>
        /// <param name="task"></param>
        public void RegisterScheduleTask(IScheduleTask task)
        {
            _scheduleTaskManager.AddTask(task);
        }

        /// <summary>
        /// 注销定时任务
        /// </summary>
        /// <param name="task"></param>
        public void UnRegisterScheduleTask(IScheduleTask task)
        {
            _scheduleTaskManager.RemoveTask(task);
        }

        void registerServices()
        {
            if (_registerMyServicesed)
                return;
            _registerMyServicesed = true;

            _services.AddSingleton<ITransactionRecorder, TransactionRecorder>();
            _services.AddSingleton<ScheduleTaskManager>(_scheduleTaskManager);
            _services.AddTransient<ScheduleTaskController>();
            _services.AddSingleton<IKeyLocker, KeyLocker>();
            _services.AddSingleton<ICodeBuilder, CodeBuilder>();
            _services.AddSingleton<IGatewayConnector, GatewayConnector>();
            _services.AddSingleton<IRequestReception, RequestReception>();
            _services.AddSingleton<InvokeRequestHandler>();
            _services.AddSingleton<GenerateInvokeCodeRequestHandler>();
            _services.AddSingleton<CommitRequestHandler>();
            _services.AddSingleton<RollbackRequestHandler>();
            _services.AddSingleton<ProcessExitHandler>();
            _services.AddSingleton<MicroServiceHost>(this);
            _services.AddSingleton<TransactionDelegateCenter>();
        }

        public void Run(MicroServiceOption option)
        {
            this.Option = option;
            ServiceProvider = _services.BuildServiceProvider();

            _logger = ServiceProvider.GetService<ILogger<MicroServiceHost>>();
            _GatewayConnector = ServiceProvider.GetService<IGatewayConnector>();

            AllGatewayAddresses = option.GatewayAddresses;
            ServicePort = option.Port;

            _GatewayConnector.ConnectAsync();
            
            _RequestReception = ServiceProvider.GetService<IRequestReception>();
            _scheduleTaskManager.StartTasks();

            TcpListener listener = new TcpListener(ServicePort);
            listener.Start();

            using (var processExitHandler = ServiceProvider.GetService<ProcessExitHandler>())
            {
                processExitHandler.Listen(this);

                while (true)
                {
                    var socket = listener.AcceptSocket();
                    if (processExitHandler.ProcessExited)
                        break;

                    Task.Run(() => _RequestReception.Interview(socket));
                }
            }
        }


      

    }
}