using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mynt.Core.Interfaces;
using Mynt.Core.Notifications;
using Mynt.Core.Strategies;
using MyntUI.Helpers;
using MyntUI.TradeManagers;
using Quartz;

namespace MyntUI.Timers
{
    [DisallowConcurrentExecution]
    public class BuyTimer : IJob
    {
        private static readonly ILogger Log = Globals.GlobalLoggerFactory.CreateLogger<BuyTimer>();

        /// <summary>
        /// Called by the <see cref="IScheduler" /> when a
        /// <see cref="ITrigger" /> fires that is associated with
        /// the <see cref="IJob" />.
        /// </summary>
        public virtual Task Execute(IJobExecutionContext context)
        {
            var type = Type.GetType($"Mynt.Core.Strategies.{Globals.GlobalTradeOptions.DefaultStrategy}, Mynt.Core", true, true);
            var strategy = Activator.CreateInstance(type) as ITradingStrategy ?? new TheScalper();

            var notificationManagers = new List<INotificationManager>()
            {
                new SignalrNotificationManager(),
                new TelegramNotificationManager(Globals.GlobalTelegramNotificationOptions)
            };

            ILogger tradeLogger = Globals.GlobalLoggerFactory.CreateLogger<TradeManager>();
            var tradeManager = new TradeManager();

            Log.LogInformation("Mynt service is looking for new trades.");
            tradeManager.LookForNewTrades();

            return Task.FromResult(true);
        }
    }
}
