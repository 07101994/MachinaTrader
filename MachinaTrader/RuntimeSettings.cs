using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LazyCache;
using MachinaTrader.Globals;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mynt.Core.Enums;
using Mynt.Core.Exchanges;
using Mynt.Core.Interfaces;
using Mynt.Core.Models;
using Mynt.Core.Notifications;
using Mynt.Data.LiteDB;
using Mynt.Data.MongoDB;
using MachinaTrader.Helpers;
using MachinaTrader.Hubs;
using MachinaTrader.Models;
using Newtonsoft.Json.Linq;
using Quartz;
using Quartz.Impl;
using MachinaTrader.Globals.Helpers;

namespace MachinaTrader
{
    public static class Runtime
    {
        public static IConfiguration GlobalConfiguration { get; set; }
        public static IDataStore GlobalDataStore { get; set; }
        public static IDataStoreBacktest GlobalDataStoreBacktest { get; set; }
        public static IExchangeApi GlobalExchangeApi { get; set; }
        public static IAppCache AppCache { get; set; }
        public static CancellationToken GlobalTimerCancellationToken = new CancellationToken();
        public static IHubContext<HubTraders> GlobalHubMyntTraders;
        public static IHubContext<HubStatistics> GlobalHubMyntStatistics;
        public static IHubContext<HubLogs> GlobalHubMyntLogs;
        public static IHubContext<HubBacktest> GlobalHubMyntBacktest;
        public static RuntimeConfig RuntimeSettings = new RuntimeConfig();
        public static IScheduler QuartzTimer = new StdSchedulerFactory().GetScheduler().Result;
        public static TelegramNotificationOptions GlobalTelegramNotificationOptions { get; set; }
        public static List<INotificationManager> NotificationManagers;
        public static OrderBehavior GlobalOrderBehavior;
        public static ConcurrentDictionary<string, Ticker> WebSocketTickers = new ConcurrentDictionary<string, Ticker>();
        public static MainConfig Configuration { get; set; }

        public static List<string> GlobalCurrencys = new List<string>();
        public static List<string> ExchangeCurrencys = new List<string>();
    }

    /// <summary>
    /// Global Settings
    /// </summary>
    public class RuntimeSettings
    {
        public async static void Init()
        {
            Runtime.GlobalOrderBehavior = OrderBehavior.AlwaysFill;

            Runtime.NotificationManagers = new List<INotificationManager>()
            {
                new SignalrNotificationManager(),
                new TelegramNotificationManager(Runtime.GlobalTelegramNotificationOptions)
            };

            if (Runtime.Configuration.SystemOptions.Database == "MongoDB")
            {
                Global.Logger.Information("Database set to MongoDB");
                MongoDbOptions databaseOptions = new MongoDbOptions();
                Runtime.GlobalDataStore = new MongoDbDataStore(databaseOptions);
                MongoDbOptions backtestDatabaseOptions = new MongoDbOptions();
                Runtime.GlobalDataStoreBacktest = new MongoDbDataStoreBacktest(backtestDatabaseOptions);
            }
            else
            {
                Global.Logger.Information("Database set to LiteDB");
                LiteDbOptions databaseOptions = new LiteDbOptions { LiteDbName = Global.DataPath + "/MachinaTrader.db" };
                Runtime.GlobalDataStore = new LiteDbDataStore(databaseOptions);
                LiteDbOptions backtestDatabaseOptions = new LiteDbOptions { LiteDbName = Global.DataPath + "/MachinaTrader.db" };
                Runtime.GlobalDataStoreBacktest = new LiteDbDataStoreBacktest(backtestDatabaseOptions);
            }

            // Global Hubs
            Runtime.GlobalHubMyntTraders = Global.ServiceScope.ServiceProvider.GetService<IHubContext<HubTraders>>();
            Runtime.GlobalHubMyntStatistics = Global.ServiceScope.ServiceProvider.GetService<IHubContext<HubStatistics>>();
            Runtime.GlobalHubMyntLogs = Global.ServiceScope.ServiceProvider.GetService<IHubContext<HubLogs>>();
            Runtime.GlobalHubMyntBacktest = Global.ServiceScope.ServiceProvider.GetService<IHubContext<HubBacktest>>();

            //Run Cron
            IScheduler scheduler = Runtime.QuartzTimer;

            IJobDetail buyTimerJob = JobBuilder.Create<Timers.BuyTimer>()
                .WithIdentity("buyTimerJobTrigger", "buyTimerJob")
                .Build();

            ITrigger buyTimerJobTrigger = TriggerBuilder.Create()
                .WithIdentity("buyTimerJobTrigger", "buyTimerJob")
                .WithCronSchedule(Runtime.Configuration.TradeOptions.BuyTimer)
                .UsingJobData("force", false)
                .Build();

            await scheduler.ScheduleJob(buyTimerJob, buyTimerJobTrigger);

            IJobDetail sellTimerJob = JobBuilder.Create<Timers.SellTimer>()
                .WithIdentity("sellTimerJobTrigger", "sellTimerJob")
                .Build();

            ITrigger sellTimerJobTrigger = TriggerBuilder.Create()
                .WithIdentity("sellTimerJobTrigger", "sellTimerJob")
                .WithCronSchedule(Runtime.Configuration.TradeOptions.SellTimer)
                .UsingJobData("force", false)
                .Build();

            await scheduler.ScheduleJob(sellTimerJob, sellTimerJobTrigger);

            await scheduler.Start();
            Global.Logger.Information($"Buy Cron will run at: {buyTimerJobTrigger.GetNextFireTimeUtc() ?? DateTime.MinValue:r}");
            Global.Logger.Information($"Sell Cron will run at: {sellTimerJobTrigger.GetNextFireTimeUtc() ?? DateTime.MinValue:r}");
        }

        public static void LoadSettings()
        {
            // Check if Overrides exists
            var settingsStr = "appsettings.json";
            if (File.Exists("appsettings.overrides.json"))
                settingsStr = "appsettings.overrides.json";

            var builder = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile(Globals.Global.AppPath + "/" + settingsStr, optional: true);
            Runtime.GlobalConfiguration = builder.Build();

            if (!File.Exists(Global.DataPath + "/MainConfig.json"))
            {
                //Init Global Config with default currency array
                Runtime.Configuration = MergeObjects.MergeCsDictionaryAndSave(new MainConfig(), Global.DataPath + "/MainConfig.json").ToObject<MainConfig>();
                Runtime.Configuration.TradeOptions.MarketBlackList = new List<string> { };
                Runtime.Configuration.TradeOptions.OnlyTradeList = new List<string> { "ETHBTC", "LTCBTC" };
                Runtime.Configuration.TradeOptions.AlwaysTradeList = new List<string> { "ETHBTC", "LTCBTC" };
                var defaultExchangeOptions = new ExchangeOptions
                {
                    Exchange = Exchange.Binance,
                    ApiKey = "",
                    ApiSecret = ""
                };
                Runtime.Configuration.ExchangeOptions.Add(defaultExchangeOptions);
                Runtime.Configuration = MergeObjects.MergeCsDictionaryAndSave(Runtime.Configuration, Global.DataPath + "/MainConfig.json", JObject.FromObject(Runtime.Configuration)).ToObject<MainConfig>();
            }
            else

            {

                Runtime.Configuration = MergeObjects.MergeCsDictionaryAndSave(new MainConfig(), Global.DataPath + "/MainConfig.json").ToObject<MainConfig>();
            }

            var exchangeOption = Runtime.Configuration.ExchangeOptions.FirstOrDefault();
            switch (exchangeOption.Exchange)
            {
                case Exchange.GdaxSimulation:
                    Runtime.GlobalExchangeApi = new BaseExchange(exchangeOption, new SimulationExchanges.ExchangeGdaxSimulationApi());
                    exchangeOption.IsSimulation = true;
                    break;
                case Exchange.BinanceSimulation:
                    //Runtime.GlobalExchangeApi = new BaseExchange(exchangeOption, new SimulationExchanges.ExchangeBinanceSimulationApi());
                    exchangeOption.IsSimulation = true;
                    break;
                default:
                    Runtime.GlobalExchangeApi = new BaseExchange(exchangeOption);
                    exchangeOption.IsSimulation = false;
                    break;
            }

            //Websocket Test
            var fullApi = Runtime.GlobalExchangeApi.GetFullApi().Result;

            //Create Exchange Currencies as List
            foreach (var currency in Runtime.Configuration.TradeOptions.AlwaysTradeList)
            {
                Runtime.GlobalCurrencys.Add(Runtime.Configuration.TradeOptions.QuoteCurrency + "-" + currency);
            }

            foreach (var currency in Runtime.GlobalCurrencys)
            {
                Runtime.ExchangeCurrencys.Add(fullApi.GlobalSymbolToExchangeSymbol(currency));
            }

            if (!exchangeOption.IsSimulation)
                fullApi.GetTickersWebSocket(OnWebsocketTickersUpdated);

            // Telegram Notifications
            Runtime.GlobalTelegramNotificationOptions = Runtime.Configuration.TelegramOptions;
        }



        public static void OnWebsocketTickersUpdated(IReadOnlyCollection<KeyValuePair<string, ExchangeSharp.ExchangeTicker>> updatedTickers)
        {
            foreach (var update in updatedTickers)
            {
                if (Runtime.ExchangeCurrencys.Contains(update.Key))
                {
                    if (Runtime.WebSocketTickers.TryGetValue(update.Key, out Ticker ticker))
                    {
                        ticker.Ask = update.Value.Ask;
                        ticker.Bid = update.Value.Bid;
                        ticker.Last = update.Value.Last;
                    }
                    else
                    {
                        Runtime.WebSocketTickers.TryAdd(update.Key, new Ticker
                        {
                            Ask = update.Value.Ask,
                            Bid = update.Value.Bid,
                            Last = update.Value.Last
                        });
                    }
                }
            }
        }
    }
}
