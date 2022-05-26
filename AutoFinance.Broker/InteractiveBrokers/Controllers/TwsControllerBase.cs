// Licensed under the Apache License, Version 2.0.

namespace AutoFinance.Broker.InteractiveBrokers.Controllers
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using AutoFinance.Broker.InteractiveBrokers.Constants;
    using AutoFinance.Broker.InteractiveBrokers.EventArgs;
    using AutoFinance.Broker.InteractiveBrokers.Exceptions;
    using AutoFinance.Broker.InteractiveBrokers.Wrappers;
    using IBApi;

    /// <summary>
    /// The controller at the level that directly wraps TWS apis
    /// </summary>
    public class TwsControllerBase : ITwsControllerBase
    {
        /// <summary>
        /// Holds the state of the next valid order Id for TWS
        /// </summary>
        private static long nextValidOrderId = -1;

        /// <summary>
        /// The account updates dictionary
        /// </summary>
        private ConcurrentDictionary<string, string> accountUpdates;

        /// <summary>
        /// The signal for TWS
        /// </summary>
        private EReaderSignal signal;

        /// <summary>
        /// The client socket
        /// </summary>
        private ITwsClientSocket clientSocket;

        /// <summary>
        /// The TWS callback handler
        /// </summary>
        private ITwsCallbackHandler twsCallbackHandler;

        /// <summary>
        /// The request Id generator
        /// </summary>
        private ITwsRequestIdGenerator twsRequestIdGenerator;

        /// <summary>
        /// The host of tws
        /// </summary>
        private string host;

        /// <summary>
        /// The port of tws
        /// </summary>
        private int port;

        /// <summary>
        /// Tws client id, see their docs
        /// </summary>
        private int clientId;

        /// <summary>
        /// If the client is connected to tws
        /// </summary>
        private bool connected;

        /// <summary>
        /// The background thread that will await the signal and send events to the callback handler
        /// </summary>
        private Thread readerThread;

        /// <summary>
        /// Initializes a new instance of the <see cref="TwsControllerBase"/> class.
        /// </summary>
        /// <param name="clientSocket">The client socket</param>
        /// <param name="twsCallbackHandler">The callback handler</param>
        /// <param name="host">The host</param>
        /// <param name="port">The port</param>
        /// <param name="clientId">The client id</param>
        public TwsControllerBase(ITwsClientSocket clientSocket, ITwsCallbackHandler twsCallbackHandler, string host, int port, int clientId)
        {
            this.clientSocket = clientSocket;
            this.twsCallbackHandler = twsCallbackHandler;
            this.host = host;
            this.port = port;
            this.clientId = clientId;

            this.accountUpdates = new ConcurrentDictionary<string, string>();
            this.signal = new EReaderMonitorSignal();
            this.twsRequestIdGenerator = new TwsRequestIdGenerator();

            // Some events come in unrequested, this will subscribe before the connection is created
            this.twsCallbackHandler.NextValidIdEvent += this.OnNextValidId;
            this.twsCallbackHandler.UpdateAccountValueEvent += this.OnUpdateAccountValueEvent;
        }

        /// <summary>
        /// Ensures the connection is active
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task EnsureConnectedAsync()
        {
            if (!this.connected)
            {
                await this.ConnectAsync();

                // Sometimes TWS flushes the socket on a new connection
                // And the socket will get really fucked up any commands come in during that time
                // Just wait 5 seconds for it to finish
                await Task.Delay(5000);
                this.connected = true;
            }
        }

        /// <summary>
        /// Disconnect from the TWS socket
        /// Note: It appears something is wrong with this API. Need to look into it.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task DisconnectAsync()
        {
            var taskSource = new TaskCompletionSource<bool>();
            this.twsCallbackHandler.ConnectionClosedEvent += (sender, eventArgs) =>
            {
                // Abort the reader thread
                taskSource.TrySetResult(true);
            };

            // Set the operation to cancel after 5 seconds
            CancellationTokenSource tokenSource = new CancellationTokenSource(5000);
            tokenSource.Token.Register(() =>
            {
                taskSource.TrySetCanceled();
            });

            this.clientSocket.Disconnect();
            return taskSource.Task;
        }

        /// <summary>
        /// Get an account detail
        /// </summary>
        /// <param name="accountId">The account Id</param>
        /// <returns>The account values</returns>
        public Task<ConcurrentDictionary<string, string>> GetAccountDetailsAsync(string accountId)
        {
            // Set the operation to cancel after 5 seconds
            CancellationTokenSource tokenSource = new CancellationTokenSource(5000);
            return this.GetAccountDetailsAsync(accountId, tokenSource.Token);
        }

        /// <summary>
        /// Get an account detail
        /// </summary>
        /// <param name="accountId">The account Id</param>
        /// <param name="cancellationToken">The cancellation token used to cancel the request</param>
        /// <returns>The account values</returns>
        public Task<ConcurrentDictionary<string, string>> GetAccountDetailsAsync(string accountId, CancellationToken cancellationToken)
        {
            string value = string.Empty;

            var taskSource = new TaskCompletionSource<ConcurrentDictionary<string, string>>();
            this.twsCallbackHandler.AccountDownloadEndEvent += (sender, args) =>
            {
                taskSource.TrySetResult(this.accountUpdates);
            };

            cancellationToken.Register(() =>
            {
                taskSource.TrySetCanceled();
            });

            this.clientSocket.ReqAccountDetails(true, accountId);

            return taskSource.Task;
        }

        /// <summary>
        /// Gets a contract by request.
        /// </summary>
        /// <param name="contract">The requested contract.</param>
        /// <returns>The details of the contract</returns>
        public Task<List<ContractDetails>> GetContractAsync(Contract contract)
        {
            int requestId = this.twsRequestIdGenerator.GetNextRequestId();
            List<ContractDetails> contractDetailsList = new List<ContractDetails>();

            var taskSource = new TaskCompletionSource<List<ContractDetails>>();

            EventHandler<ContractDetailsEventArgs> contractDetailsEventHandler = null;
            EventHandler<ContractDetailsEndEventArgs> contractDetailsEndEventHandler = null;
            EventHandler<ErrorEventArgs> errorEventHandler = null;

            contractDetailsEventHandler += (sender, args) =>
            {
                // When the contract details end event is fired, check if it's for this request ID and return it.
                contractDetailsList.Add(args.ContractDetails);
            };

            contractDetailsEndEventHandler += (sender, args) =>
            {
                taskSource.TrySetResult(contractDetailsList);
            };

            errorEventHandler = (sender, args) =>
            {
                if (args.Id == requestId)
                {
                    // The error is associated with this request
                    this.twsCallbackHandler.ContractDetailsEvent -= contractDetailsEventHandler;
                    this.twsCallbackHandler.ContractDetailsEndEvent -= contractDetailsEndEventHandler;
                    this.twsCallbackHandler.ErrorEvent -= errorEventHandler;
                    taskSource.TrySetException(new TwsException(args.ErrorMessage));
                }
            };

            // Set the operation to cancel after 5 seconds
            CancellationTokenSource tokenSource = new CancellationTokenSource(5000);
            tokenSource.Token.Register(() =>
            {
                taskSource.TrySetCanceled();
            });

            this.twsCallbackHandler.ContractDetailsEvent += contractDetailsEventHandler;
            this.twsCallbackHandler.ContractDetailsEndEvent += contractDetailsEndEventHandler;
            this.twsCallbackHandler.ErrorEvent += errorEventHandler;

            this.clientSocket.ReqContractDetails(requestId, contract);
            return taskSource.Task;
        }

        /// <summary>
        /// Get a list of all the executions from TWS
        /// </summary>
        /// <returns>A list of execution details events from TWS.</returns>
        public Task<List<ExecutionDetailsEventArgs>> RequestExecutions()
        {
            List<ExecutionDetailsEventArgs> executionDetailsEvents = new List<ExecutionDetailsEventArgs>();
            var taskSource = new TaskCompletionSource<List<ExecutionDetailsEventArgs>>();
            EventHandler<ExecutionDetailsEventArgs> executionDetailsEvent = null;
            EventHandler<ExecutionDetailsEndEventArgs> executionDetailsEndEvent = null;

            executionDetailsEvent = (sender, args) =>
            {
                executionDetailsEvents.Add(args);
            };

            executionDetailsEndEvent = (sender, args) =>
            {
                // Cleanup the event handlers when the positions end event is called.
                this.twsCallbackHandler.ExecutionDetailsEvent -= executionDetailsEvent;
                this.twsCallbackHandler.ExecutionDetailsEndEvent -= executionDetailsEndEvent;

                taskSource.TrySetResult(executionDetailsEvents);
            };

            this.twsCallbackHandler.ExecutionDetailsEvent += executionDetailsEvent;
            this.twsCallbackHandler.ExecutionDetailsEndEvent += executionDetailsEndEvent;

            // Set the operation to cancel after 5 seconds
            CancellationTokenSource tokenSource = new CancellationTokenSource(5000);
            tokenSource.Token.Register(() =>
            {
                taskSource.TrySetCanceled();
            });

            this.clientSocket.RequestExecutions(this.twsRequestIdGenerator.GetNextRequestId());

            return taskSource.Task;
        }

        /// <summary>
        /// Gets historical data from TWS.
        /// </summary>
        /// <param name="contract">The contract type</param>
        /// <param name="endDateTime">The end date of the request</param>
        /// <param name="duration">The duration of the request</param>
        /// <param name="barSizeSetting">The bar size to request</param>
        /// <param name="whatToShow">The historical data request type</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task<List<HistoricalDataEventArgs>> GetHistoricalDataAsync(Contract contract, DateTime endDateTime, TwsDuration duration, TwsBarSizeSetting barSizeSetting, TwsHistoricalDataRequestType whatToShow)
        {
            int requestId = this.twsRequestIdGenerator.GetNextRequestId();
            int useRth = 1;
            int formatDate = 1;
            List<TagValue> chartOptions = null;

            string value = string.Empty;

            var taskSource = new TaskCompletionSource<List<HistoricalDataEventArgs>>();

            EventHandler<HistoricalDataEventArgs> historicalDataEventHandler = null;
            EventHandler<HistoricalDataEndEventArgs> historicalDataEndEventHandler = null;
            EventHandler<ErrorEventArgs> errorEventHandler = null;

            List<HistoricalDataEventArgs> historicalDataList = new List<HistoricalDataEventArgs>();

            historicalDataEventHandler = (sender, args) =>
            {
                historicalDataList.Add(args);
            };

            historicalDataEndEventHandler = (sender, args) =>
            {
                this.twsCallbackHandler.HistoricalDataEvent -= historicalDataEventHandler;
                this.twsCallbackHandler.HistoricalDataEndEvent -= historicalDataEndEventHandler;
                this.twsCallbackHandler.ErrorEvent -= errorEventHandler;
                taskSource.TrySetResult(historicalDataList);
            };

            errorEventHandler = (sender, args) =>
            {
                if (args.Id == requestId)
                {
                    // The error is associated with this request
                    this.twsCallbackHandler.HistoricalDataEvent -= historicalDataEventHandler;
                    this.twsCallbackHandler.HistoricalDataEndEvent -= historicalDataEndEventHandler;
                    this.twsCallbackHandler.ErrorEvent -= errorEventHandler;
                    taskSource.TrySetException(new TwsException(args.ErrorMessage));
                }
            };

            // Set the operation to cancel after 1 minute
            CancellationTokenSource tokenSource = new CancellationTokenSource(60 * 1000);
            tokenSource.Token.Register(() =>
            {
                this.twsCallbackHandler.HistoricalDataEvent -= historicalDataEventHandler;
                this.twsCallbackHandler.HistoricalDataEndEvent -= historicalDataEndEventHandler;
                this.twsCallbackHandler.ErrorEvent -= errorEventHandler;

                taskSource.TrySetCanceled();
            });

            this.twsCallbackHandler.HistoricalDataEvent += historicalDataEventHandler;
            this.twsCallbackHandler.HistoricalDataEndEvent += historicalDataEndEventHandler;
            this.twsCallbackHandler.ErrorEvent += errorEventHandler;

            this.clientSocket.ReqHistoricalData(requestId, contract, endDateTime.ToString("yyyyMMdd HH:mm:ss"), duration.ToTwsParameter(), barSizeSetting.ToTwsParameter(), whatToShow.ToTwsParameter(), useRth, formatDate, chartOptions);
            return taskSource.Task;
        }

        /// <summary>
        /// Gets news providers from TWS
        /// </summary>
        /// <returns>News providers</returns>
        public Task<NewsProviderEventArgs> GetNewsProviders()
        {
            var taskSource = new TaskCompletionSource<NewsProviderEventArgs>();

            EventHandler<NewsProviderEventArgs> newsProviderCallback = null;
            newsProviderCallback = (sender, eventArgs) =>
            {
                // Unregister the callbacks
                this.twsCallbackHandler.NewsProviderEvent -= newsProviderCallback;
                taskSource.TrySetResult(eventArgs);
            };

            this.twsCallbackHandler.NewsProviderEvent += newsProviderCallback;
            this.clientSocket.EClientSocket.reqNewsProviders();

            return taskSource.Task;
        }

        /// <summary>
        /// Get the next valid order Id.
        /// </summary>
        /// <returns>The next valid order Id</returns>
        public Task<int> GetNextValidIdAsync()
        {
            // Set the operation to cancel after 5 seconds
            CancellationTokenSource tokenSource = new CancellationTokenSource(5000);
            return this.GetNextValidIdAsync(tokenSource.Token);
        }

        /// <summary>
        /// Get the next valid order Id.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token used to cancel the request</param>
        /// <returns>The next valid order Id</returns>
        public Task<int> GetNextValidIdAsync(CancellationToken cancellationToken)
        {
            var taskSource = new TaskCompletionSource<int>();

            long nextId = Interlocked.Read(ref nextValidOrderId);

            if (nextId == -1)
            {
                // The next valid order id has not been sent by TWS yet.
                // It may be taking some time for TWS to initialize or send the event.
                // Wait for it before handling an order status or open order event
                this.twsCallbackHandler.NextValidIdEvent += (sender, eventArgs) =>
                {
                    Interlocked.Exchange(ref nextValidOrderId, eventArgs.OrderId);
                    nextId = Interlocked.Read(ref nextValidOrderId);
                    Interlocked.Increment(ref nextValidOrderId);
                    taskSource.TrySetResult((int)nextId);
                };

                // Set the operation to cancel after 5 seconds
                cancellationToken.Register(() =>
                {
                    taskSource.TrySetCanceled();
                });
            }
            else
            {
                // The next valid Id has already been assigned. Increment it and return.
                Interlocked.Increment(ref nextValidOrderId);
                taskSource.TrySetResult((int)nextId);
            }

            return taskSource.Task;
        }

        /// <summary>
        /// Get a list of all the executions from TWS
        /// </summary>
        /// <returns>A list of execution details events from TWS.</returns>
        public async Task<List<OpenOrderEventArgs>> RequestOpenOrders()
        {
            // Set the operation to cancel after 5 seconds
            CancellationTokenSource tokenSource = new CancellationTokenSource(5000);
            return await this.RequestOpenOrders(tokenSource.Token);
        }

        /// <summary>
        /// Get a list of all the executions from TWS
        /// </summary>
        /// <param name="cancellationToken">The cancellation token used to cancel the request</param>
        /// <returns>A list of execution details events from TWS.</returns>
        public Task<List<OpenOrderEventArgs>> RequestOpenOrders(CancellationToken cancellationToken)
        {
            List<OpenOrderEventArgs> openOrderEvents = new List<OpenOrderEventArgs>();
            var taskSource = new TaskCompletionSource<List<OpenOrderEventArgs>>();
            EventHandler<OpenOrderEventArgs> openOrderEventHandler = null;
            EventHandler<OpenOrderEndEventArgs> openOrderEndEventHandler = null;

            openOrderEventHandler = (sender, args) =>
            {
                openOrderEvents.Add(args);
            };

            openOrderEndEventHandler = (sender, args) =>
            {
                // Cleanup the event handlers when the positions end event is called.
                this.twsCallbackHandler.OpenOrderEvent -= openOrderEventHandler;
                this.twsCallbackHandler.OpenOrderEndEvent -= openOrderEndEventHandler;

                taskSource.TrySetResult(openOrderEvents);
            };

            this.twsCallbackHandler.OpenOrderEvent += openOrderEventHandler;
            this.twsCallbackHandler.OpenOrderEndEvent += openOrderEndEventHandler;

            // Set the operation to cancel after 5 seconds
            cancellationToken.Register(() =>
            {
                taskSource.TrySetCanceled();
            });

            this.clientSocket.RequestAllOpenOrders();

            return taskSource.Task;
        }

        /// <summary>
        /// Cancels an order in TWS.
        /// </summary>
        /// <param name="orderId">The order Id to cancel</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation. True if the broker acknowledged the cancelation request, false otherwise.</returns>
        public Task<bool> CancelOrderAsync(int orderId)
        {
            // Set the operation to cancel after 5 seconds
            CancellationTokenSource tokenSource = new CancellationTokenSource(5000);
            return this.CancelOrderAsync(orderId, tokenSource.Token);
        }

        /// <summary>
        /// Cancels an order in TWS.
        /// </summary>
        /// <param name="orderId">The order Id to cancel</param>
        /// <param name="cancellationToken">The cancellation token used to cancel the request</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation. True if the broker acknowledged the cancelation request, false otherwise.</returns>
        public Task<bool> CancelOrderAsync(int orderId, CancellationToken cancellationToken)
        {
            var taskSource = new TaskCompletionSource<bool>();

            EventHandler<OrderStatusEventArgs> orderStatusEventCallback = null;
            EventHandler<ErrorEventArgs> errorEventCallback = null;

            orderStatusEventCallback = (sender, eventArgs) =>
            {
                if (eventArgs.OrderId == orderId)
                {
                    if (eventArgs.Status == "Cancelled")
                    {
                        // Unregister the callbacks
                        this.twsCallbackHandler.OrderStatusEvent -= orderStatusEventCallback;

                        // Set the result
                        taskSource.TrySetResult(true);
                    }
                }
            };

            errorEventCallback = (sender, eventArgs) =>
            {
                if (eventArgs.ErrorCode == TwsErrorCodes.OrderCancelled)
                {
                    this.twsCallbackHandler.ErrorEvent -= errorEventCallback;
                    taskSource.TrySetResult(true);
                }

                if (eventArgs.ErrorCode == TwsErrorCodes.OrderCannotBeCancelled ||
                    eventArgs.ErrorCode == TwsErrorCodes.OrderCannotBeCancelled2)
                {
                    this.twsCallbackHandler.ErrorEvent -= errorEventCallback;
                    taskSource.TrySetException(new TwsException($"Order {eventArgs.Id} cannot be canceled"));
                }
            };

            // Set the operation to cancel after 5 seconds
            cancellationToken.Register(() =>
            {
                taskSource.TrySetCanceled();
            });

            this.twsCallbackHandler.OrderStatusEvent += orderStatusEventCallback;
            this.twsCallbackHandler.ErrorEvent += errorEventCallback;
            this.clientSocket.CancelOrder(orderId);
            return taskSource.Task;
        }

        /// <summary>
        /// Places an order and returns whether the order placement was successful or not.
        /// </summary>
        /// <param name="orderId">The order Id</param>
        /// <param name="contract">The contract to trade</param>
        /// <param name="order">The order</param>
        /// <returns>True if the order was acknowledged, false otherwise</returns>
        public Task<bool> PlaceOrderAsync(int orderId, Contract contract, Order order)
        {
            // Set the operation to cancel after 5 seconds
            CancellationTokenSource tokenSource = new CancellationTokenSource(5000);
            return this.PlaceOrderAsync(orderId, contract, order, tokenSource.Token);
        }

        /// <summary>
        /// Places an order and returns whether the order placement was successful or not.
        /// </summary>
        /// <param name="orderId">The order Id</param>
        /// <param name="contract">The contract to trade</param>
        /// <param name="order">The order</param>
        /// <param name="cancellationToken">The cancellation token used to cancel the request</param>
        /// <returns>True if the order was acknowledged, false otherwise</returns>
        public Task<bool> PlaceOrderAsync(int orderId, Contract contract, Order order, CancellationToken cancellationToken)
        {
            var taskSource = new TaskCompletionSource<bool>();

            EventHandler<OpenOrderEventArgs> openOrderEventCallback = null;

            openOrderEventCallback = (sender, eventArgs) =>
            {
                if (eventArgs.OrderId == orderId)
                {
                    if (eventArgs.OrderState.Status == TwsOrderStatus.Submitted ||
                        eventArgs.OrderState.Status == TwsOrderStatus.Presubmitted)
                    {
                        // Unregister the callbacks
                        this.twsCallbackHandler.OpenOrderEvent -= openOrderEventCallback;

                        taskSource.TrySetResult(true);
                    }
                }
            };

            EventHandler<ErrorEventArgs> orderErrorEventCallback = null;
            orderErrorEventCallback = (sender, eventArgs) =>
            {
                if (orderId == eventArgs.Id)
                {
                    if (eventArgs.ErrorCode == TwsErrorCodes.InvalidOrderType ||
                        eventArgs.ErrorCode == TwsErrorCodes.AmbiguousContract)
                    {
                        // Unregister the callbacks
                        this.twsCallbackHandler.OpenOrderEvent -= openOrderEventCallback;
                        this.twsCallbackHandler.ErrorEvent -= orderErrorEventCallback;
                        taskSource.TrySetResult(false);
                    }
                }
            };

            this.twsCallbackHandler.ErrorEvent += orderErrorEventCallback;
            this.twsCallbackHandler.OpenOrderEvent += openOrderEventCallback;

            cancellationToken.Register(() =>
            {
                taskSource.TrySetCanceled();
            });

            this.clientSocket.PlaceOrder(orderId, contract, order);
            return taskSource.Task;
        }

        /// <summary>
        /// Get a list of all the positions in TWS.
        /// </summary>
        /// <returns>A list of position status events from TWS.</returns>
        public Task<List<PositionStatusEventArgs>> RequestPositions()
        {
            // Set the operation to cancel after 5 seconds
            CancellationTokenSource tokenSource = new CancellationTokenSource(5000);
            return this.RequestPositions(tokenSource.Token);
        }

        /// <summary>
        /// Get a list of all the positions in TWS.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token used to cancel the request</param>
        /// <returns>A list of position status events from TWS.</returns>
        public Task<List<PositionStatusEventArgs>> RequestPositions(CancellationToken cancellationToken)
        {
            List<PositionStatusEventArgs> positionStatusEvents = new List<PositionStatusEventArgs>();
            var taskSource = new TaskCompletionSource<List<PositionStatusEventArgs>>();
            EventHandler<PositionStatusEventArgs> positionStatusEventHandler = null;
            EventHandler<RequestPositionsEndEventArgs> requestPositionsEndEventHandler = null;

            positionStatusEventHandler = (sender, args) =>
            {
                positionStatusEvents.Add(args);
            };

            requestPositionsEndEventHandler = (sender, args) =>
            {
                // Cleanup the event handlers when the positions end event is called.
                this.twsCallbackHandler.PositionStatusEvent -= positionStatusEventHandler;
                this.twsCallbackHandler.RequestPositionsEndEvent -= requestPositionsEndEventHandler;

                taskSource.TrySetResult(positionStatusEvents);
            };

            this.twsCallbackHandler.PositionStatusEvent += positionStatusEventHandler;
            this.twsCallbackHandler.RequestPositionsEndEvent += requestPositionsEndEventHandler;

            cancellationToken.Register(() =>
            {
                taskSource.TrySetCanceled();
            });

            this.clientSocket.RequestPositions();

            return taskSource.Task;
        }

        /// <summary>
        /// Request security definition parameters.
        /// This is mainly used for finding strikes, multipliers, exchanges, and expirations for options contracts.
        /// </summary>
        /// <param name="underlyingSymbol">The underlying symbol</param>
        /// <param name="exchange">The exchange</param>
        /// <param name="underlyingSecType">The underlying security type</param>
        /// <param name="underlyingConId">The underlying contract ID, retrieved from the Contract Details call</param>
        /// <returns>The security definitions for options</returns>
        public Task<List<SecurityDefinitionOptionParameterEventArgs>> RequestSecurityDefinitionOptionParameters(
            string underlyingSymbol,
            string exchange,
            string underlyingSecType,
            int underlyingConId)
        {
            // Set the operation to cancel after 10 seconds
            CancellationTokenSource tokenSource = new CancellationTokenSource(10000);
            return this.RequestSecurityDefinitionOptionParameters(underlyingSymbol, exchange, underlyingSecType, underlyingConId, tokenSource.Token);
        }

        /// <summary>
        /// Request security definition parameters.
        /// This is mainly used for finding strikes, multipliers, exchanges, and expirations for options contracts.
        /// </summary>
        /// <param name="underlyingSymbol">The underlying symbol</param>
        /// <param name="exchange">The exchange</param>
        /// <param name="underlyingSecType">The underlying security type</param>
        /// <param name="underlyingConId">The underlying contract ID, retrieved from the Contract Details call</param>
        /// <param name="cancellationToken">The cancellation token used to cancel the request</param>
        /// <returns>The security definitions for options</returns>
        public Task<List<SecurityDefinitionOptionParameterEventArgs>> RequestSecurityDefinitionOptionParameters(
            string underlyingSymbol,
            string exchange,
            string underlyingSecType,
            int underlyingConId,
            CancellationToken cancellationToken)
        {
            var taskSource = new TaskCompletionSource<List<SecurityDefinitionOptionParameterEventArgs>>();

            List<SecurityDefinitionOptionParameterEventArgs> securityDefinitionEvents = new List<SecurityDefinitionOptionParameterEventArgs>();

            EventHandler<SecurityDefinitionOptionParameterEventArgs> securityDefinitionEventHandler = null;
            EventHandler securityDefintionEndEventHandler = null;

            securityDefinitionEventHandler = (sender, args) =>
            {
                securityDefinitionEvents.Add(args);
            };

            securityDefintionEndEventHandler = (sender, args) =>
            {
                this.twsCallbackHandler.SecurityDefinitionOptionParameterEvent -= securityDefinitionEventHandler;
                this.twsCallbackHandler.SecurityDefinitionOptionParameterEndEvent -= securityDefintionEndEventHandler;
                taskSource.TrySetResult(securityDefinitionEvents);
            };

            cancellationToken.Register(() =>
            {
                taskSource.TrySetCanceled();
            });

            this.twsCallbackHandler.SecurityDefinitionOptionParameterEvent += securityDefinitionEventHandler;
            this.twsCallbackHandler.SecurityDefinitionOptionParameterEndEvent += securityDefintionEndEventHandler;

            int requestId = this.twsRequestIdGenerator.GetNextRequestId();
            this.clientSocket.RequestSecurityDefinitionOptionParameters(requestId, underlyingSymbol, exchange, underlyingSecType, underlyingConId);
            return taskSource.Task;
        }

        /// <summary>
        /// This event is set during the initialization of the object.
        /// This event handler should be called only once during the startup of Tws.
        /// </summary>
        /// <param name="sender">The sender</param>
        /// <param name="eventArgs">The event arguments</param>
        private void OnNextValidId(object sender, NextValidIdEventArgs eventArgs)
        {
            long orderId = 0;
            Interlocked.Exchange(ref nextValidOrderId, eventArgs.OrderId);
            orderId = Interlocked.Read(ref nextValidOrderId);
            Interlocked.Increment(ref nextValidOrderId);

            // Unsubscribe the main event
            this.twsCallbackHandler.NextValidIdEvent -= this.OnNextValidId;
        }

        /// <summary>
        /// Connect to the TWS socket and launch a background thread to begin firing the events.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private Task ConnectAsync()
        {
            var taskSource = new TaskCompletionSource<bool>();

            void ConnectionAcknowledgementCallback(object sender, EventArgs eventArgs)
            {
                // When the connection is acknowledged, create a reader to consume messages from the TWS.
                // The EReader will consume the incoming messages and the callback handler will begin to fire events.
                this.twsCallbackHandler.ConnectionAcknowledgementEvent -= ConnectionAcknowledgementCallback;
                var reader = new EReader(this.clientSocket.EClientSocket, this.signal);
                reader.Start();

                this.readerThread = new Thread(
                 () =>
                 {
                     while (true)
                     {
                         this.signal.waitForSignal();
                         reader.processMsgs();
                     }
                 })
                { IsBackground = true };
                this.readerThread.Start();

                taskSource.TrySetResult(true);
            }

            // Set the operation to cancel after 5 seconds
            CancellationTokenSource tokenSource = new CancellationTokenSource(5000);
            tokenSource.Token.Register(() =>
            {
                taskSource.TrySetCanceled();
            });

            this.twsCallbackHandler.ConnectionAcknowledgementEvent += ConnectionAcknowledgementCallback;
            this.clientSocket.Connect(this.host, this.port, this.clientId);
            return taskSource.Task;
        }

        /// <summary>
        /// Run when an account value is updated
        /// </summary>
        /// <param name="sender">The sender</param>
        /// <param name="eventArgs">The event arguments</param>
        private void OnUpdateAccountValueEvent(object sender, UpdateAccountValueEventArgs eventArgs)
        {
            this.accountUpdates.AddOrUpdate(eventArgs.Key, eventArgs.Value, (key, value) =>
            {
                return eventArgs.Value; // Always take the most recent result
            });
        }
    }
}
