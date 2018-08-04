using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MachinaTrader.Globals;
using Mynt.Core.Enums;
using Mynt.Core.Extensions;
using Mynt.Core.Interfaces;
using Mynt.Core.Models;
using Mynt.Core.Strategies;

namespace MachinaTrader.TradeManagers
{
    public class TradeManager : ITradeManager
    {
        #region BUY SIDE

        /// <summary>
        /// Checks if new trades can be started.
        /// </summary>
        /// <returns></returns>
        public async Task LookForNewTrades(string strategyString = null)
        {
            // Initialize the things we'll be using throughout the process.

            ITradingStrategy strategy;

            if (strategyString != null)
            {
                var type = Type.GetType($"Mynt.Core.Strategies.{strategyString}, Mynt.Core", true, true);
                strategy = Activator.CreateInstance(type) as ITradingStrategy ?? new TheScalper();
            }
            else
            {
                var type = Type.GetType($"Mynt.Core.Strategies.{Runtime.Configuration.TradeOptions.DefaultStrategy}, Mynt.Core", true, true);
                strategy = Activator.CreateInstance(type) as ITradingStrategy ?? new TheScalper();
            }

            Global.Logger.Information($"Looking for trades using {strategy.Name}");

            // This means an order to buy has been open for an entire buy cycle.
            if (Runtime.Configuration.TradeOptions.CancelUnboughtOrdersEachCycle && Runtime.GlobalOrderBehavior == OrderBehavior.CheckMarket)
                await CancelUnboughtOrders();

            // Check active trades against our strategy.
            // If the strategy tells you to sell, we create a sell.
            await CheckActiveTradesAgainstStrategy(strategy);

            //var activeTrades = await Runtime.GlobalDataStore.GetActiveTradesAsync();
            //int currentActiveTrades = activeTrades.Count(x => x.IsOpen);

            await FindBuyOpportunities(strategy);
        }

        /// <summary>
        /// Cancels any orders that have been buying for an entire cycle.
        /// </summary>
        /// <returns></returns>
        private async Task CancelUnboughtOrders()
        {
            // Only trigger if there are orders still buying.
            var activeTrades = await Runtime.GlobalDataStore.GetActiveTradesAsync();
            if (activeTrades.Any(x => x.IsBuying))
            {
                // Loop our current trades that are still looking to buy if there are any.
                foreach (var trade in activeTrades.Where(x => x.IsBuying))
                {
                    // Only in livetrading
                    if (!Runtime.Configuration.TradeOptions.PaperTrade)
                    {
                        // Cancel our open buy order on the exchange.
                        var exchangeOrder = await Runtime.GlobalExchangeApi.GetOrder(trade.BuyOrderId, trade.Market);

                        // If this order is PartiallyFilled, don't cancel
                        if (exchangeOrder?.Status == OrderStatus.PartiallyFilled)
                            continue;  // not yet completed so wait

                        await Runtime.GlobalExchangeApi.CancelOrder(trade.BuyOrderId, trade.Market);
                    }

                    // Update the buy order in our data storage.
                    trade.IsBuying = false;
                    trade.OpenOrderId = null;
                    trade.IsOpen = false;
                    trade.SellType = SellType.Cancelled;
                    trade.CloseDate = DateTime.UtcNow;

                    // Update the order
                    await Runtime.GlobalDataStore.SaveTradeAsync(trade);

                    await SendNotification($"Cancelled {trade.Market} buy order because it wasn't filled in time.");
                }
            }
        }

        /// <summary>
        /// Checks our current running trades against the strategy.
        /// If the strategy tells us to sell we need to do so.
        /// </summary>
        /// <returns></returns>
        private async Task CheckActiveTradesAgainstStrategy(ITradingStrategy strategy)
        {
            // Check our active trades for a sell signal from the strategy
            var activeTrades = await Runtime.GlobalDataStore.GetActiveTradesAsync();

            foreach (var trade in activeTrades.Where(x => !x.IsSelling && x.IsOpen))
            {
                //Check against main conditions -> No Signal mode
                // Sell if we setup instant sell
                if (trade.SellNow)
                {
                    await SendNotification($"Sell now is set: Selling with {(trade.SellOnPercentage / 100)} for {trade.TradeId}");

                    var orderId = Runtime.Configuration.TradeOptions.PaperTrade ? Guid.NewGuid().ToString().Replace("-", "") : await Runtime.GlobalExchangeApi.Sell(trade.Market, trade.Quantity, trade.TickerLast.Bid);
                    trade.CloseRate = trade.TickerLast.Bid;
                    trade.OpenOrderId = orderId;
                    trade.SellOrderId = orderId;
                    trade.SellType = SellType.Immediate;
                    trade.IsSelling = true;

                    await Runtime.GlobalDataStore.SaveTradeAsync(trade);
                    continue;
                }
                // Hold
                if (trade.HoldPosition)
                {
                    await SendNotification($"Hold is set: Ignore {trade.TradeId}");
                    continue;
                }

                // Sell if defined percentage is reached
                //await SendNotification($"Try to sell:  {trade.TradeId} - {currentProfit * 100} {trade.SellOnPercentage}");
                var currentProfit = (trade.TickerLast.Bid - trade.OpenRate) / trade.OpenRate;
                if ((currentProfit) >= (trade.SellOnPercentage / 100))
                {
                    await SendNotification($"We've reached defined percentage ({(trade.SellOnPercentage)})for {trade.TradeId} - Selling now");
                    var orderId = Runtime.Configuration.TradeOptions.PaperTrade ? Guid.NewGuid().ToString().Replace("-", "") : await Runtime.GlobalExchangeApi.Sell(trade.Market, trade.Quantity, trade.TickerLast.Bid);

                    trade.CloseRate = trade.TickerLast.Bid;
                    trade.OpenOrderId = orderId;
                    trade.SellOrderId = orderId;
                    trade.SellType = SellType.Immediate;
                    trade.IsSelling = true;

                    await Runtime.GlobalDataStore.SaveTradeAsync(trade);
                    continue;
                }

                if (trade.SellOnPercentage != 0)
                {
                    continue;
                }

                var signal = await GetStrategySignal(trade.Market, strategy);

                // If the strategy is telling us to sell we need to do so.
                if (signal != null && signal.TradeAdvice == TradeAdvice.Sell)
                {
                    // Create a sell order for our strategy.
                    var ticker = await Runtime.GlobalExchangeApi.GetTicker(trade.Market);

                    // Check Trading Mode
                    var orderId = Runtime.Configuration.TradeOptions.PaperTrade ? Guid.NewGuid().ToString().Replace("-", "") : await Runtime.GlobalExchangeApi.Sell(trade.Market, trade.Quantity, ticker.Bid);

                    trade.CloseRate = ticker.Bid;
                    trade.OpenOrderId = orderId;
                    trade.SellOrderId = orderId;
                    trade.SellType = SellType.Strategy;
                    trade.IsSelling = true;

                    await Runtime.GlobalDataStore.SaveTradeAsync(trade);
                }
            }
        }

        /// <summary>
        /// Checks the implemented trading indicator(s),
        /// if one pair triggers the buy signal a new trade record gets created.
        /// </summary>
        /// <returns></returns>
        private async Task<List<TradeSignal>> FindBuyOpportunities(ITradingStrategy strategy)
        {
            Global.Logger.Warning("FindBuyOpportunities START: " + DateTime.Now + " " + strategy.Name);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // Retrieve our current markets
            var markets = await Runtime.GlobalExchangeApi.GetMarketSummaries(Runtime.Configuration.TradeOptions.QuoteCurrency);
            var pairs = new List<TradeSignal>();

            Global.Logger.Information($"Markets found: {markets.Count}");

            // Check if there are markets matching our volume.
            markets = markets.Where(x =>
                (x.Volume > Runtime.Configuration.TradeOptions.MinimumAmountOfVolume ||
                 Runtime.Configuration.TradeOptions.AlwaysTradeList.Contains(x.CurrencyPair.BaseCurrency)) &&
                 Runtime.Configuration.TradeOptions.QuoteCurrency.ToUpper() == x.CurrencyPair.QuoteCurrency.ToUpper()).ToList();

            // If there are items on the only trade list remove the rest
            if (Runtime.Configuration.TradeOptions.OnlyTradeList.Count > 0)
                markets = markets.Where(m => Runtime.Configuration.TradeOptions.OnlyTradeList.Any(c => c.Contains(m.CurrencyPair.BaseCurrency))).ToList();
            
            // Remove existing trades from the list to check.
            var activeTrades = await Runtime.GlobalDataStore.GetActiveTradesAsync();
            foreach (var trade in activeTrades)
                markets.RemoveAll(x => x.MarketName == trade.Market);

            // Remove items that are on our blacklist.
            foreach (var market in Runtime.Configuration.TradeOptions.MarketBlackList)
                markets.RemoveAll(x => x.MarketName == market);


            // Buy from external - Currently for Debug -> This will buy on each tick !
            /******************************/
            //var externalTicker = await Runtime.GlobalExchangeApi.GetTicker("LINKBTC");
            //Candle externalCandle = new Candle();
            //externalCandle.Timestamp = DateTime.UtcNow;
            //externalCandle.Open = externalTicker.Last;
            //externalCandle.High = externalTicker.Last;
            //externalCandle.Volume = externalTicker.Volume;
            //externalCandle.Close = externalTicker.Last;
            //pairs.Add(new TradeSignal
            //{
            //    MarketName = "LINKBTC",
            //    QuoteCurrency = "LINK",
            //    BaseCurrency = "BTC",
            //    TradeAdvice = TradeAdvice.StrongBuy,
            //    SignalCandle = externalCandle
            //});

            //_activeTrades = await Runtime.GlobalDataStore.GetActiveTradesAsync();
            //if (_activeTrades.Where(x => x.IsOpen).Count() < Runtime.Configuration.TradeOptions.MaxNumberOfConcurrentTrades)
            //{
            //    await CreateNewTrade(new TradeSignal
            //    {
            //        MarketName = "LINKBTC",
            //        QuoteCurrency = "LINK",
            //        BaseCurrency = "BTC",
            //        TradeAdvice = TradeAdvice.StrongBuy,
            //        SignalCandle = externalCandle
            //    }, strategy);
            //    Global.Logger.Information("Match signal -> Buying " + "LINKBTC");
            //}
            //else
            //{
            //    Global.Logger.Information("Too Many Trades: Ignore Match signal " + "LINKBTC");
            //}
            /******************************/

            int pairsCount = 0;

            var cts = new CancellationTokenSource();
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cts.Token,
                MaxDegreeOfParallelism = 1 //Environment.ProcessorCount
            };

            await Task.Run(() => Parallel.ForEach(markets.Distinct().OrderByDescending(x => x.Volume).ToList(), parallelOptions, async market =>
            {
                var watch1 = System.Diagnostics.Stopwatch.StartNew();
                Global.Logger.Information("Parallel start " + market.MarketName);

                var signal = await GetStrategySignal(market.MarketName, strategy);

                // A match was made, buy that please!
                if (signal != null && signal.TradeAdvice == TradeAdvice.Buy)
                {
                    activeTrades = Runtime.GlobalDataStore.GetActiveTradesAsync().Result;
                    int currentActiveTrades = activeTrades.Count(x => x.IsOpen);

                    if (currentActiveTrades < Runtime.Configuration.TradeOptions.MaxNumberOfConcurrentTrades)
                    {
                        await CreateNewTrade(new TradeSignal
                        {
                            MarketName = market.MarketName,
                            QuoteCurrency = market.CurrencyPair.QuoteCurrency,
                            BaseCurrency = market.CurrencyPair.BaseCurrency,
                            TradeAdvice = signal.TradeAdvice,
                            SignalCandle = signal.SignalCandle
                        }, strategy);

                        pairsCount = pairsCount + 1;
                        Global.Logger.Information("Match signal -> Buying " + market.MarketName);
                    }
                    else
                    {
                        Global.Logger.Information("Too Many Trades: Ignore Match signal " + market.MarketName);
                    }
                }
                watch1.Stop();
                Global.Logger.Warning("Parallel END: " + DateTime.Now + " / " + watch1.Elapsed.TotalSeconds);
            }), cts.Token);

            if (pairs.Count == 0)
            {
                Global.Logger.Information("No trade opportunities found...");
            }

            watch.Stop();
            Global.Logger.Warning("FindBuyOpportunities END: " + DateTime.Now + " / " + watch.Elapsed.TotalSeconds);

            return pairs;
        }

        /// <summary>
        /// Calculates a buy signal based on several technical analysis indicators.
        /// </summary>
        /// <param name="market">The market we're going to check against.</param>
        /// <param name="strategy"></param>
        /// <returns></returns>
        private async Task<TradeSignal> GetStrategySignal(string market, ITradingStrategy strategy)
        {
            try
            {
                Global.Logger.Information("Checking market {Market}...", market);

                var minimumDate = strategy.GetMinimumDateTime();
                var candleDate = strategy.GetCurrentCandleDateTime();
                DateTime? endDate = null;

                if (Runtime.Configuration.ExchangeOptions.FirstOrDefault().IsSimulation)
                {
                    //in simulation the date comes from external
                    candleDate = Runtime.Configuration.ExchangeOptions.FirstOrDefault().SimulationCurrentDate;

                    //TODO: improve to other timeframe
                    minimumDate = candleDate.AddMinutes(-(30 * strategy.MinimumAmountOfCandles));

                    endDate = candleDate;
                }

                var candles = await Runtime.GlobalExchangeApi.GetTickerHistory(market, strategy.IdealPeriod, minimumDate, endDate);

                var desiredLastCandleTime = candleDate.AddMinutes(-(strategy.IdealPeriod.ToMinutesEquivalent()));

                Global.Logger.Information("Checking market {Market} lastCandleTime {a} - desiredLastCandleTime {b}", market, candles.Last().Timestamp, desiredLastCandleTime);

                int k = 1;

                while (candles.Last().Timestamp < desiredLastCandleTime && k < 20)
                {
                    k++;
                    Thread.Sleep(1000 * k);

                    candles = await Runtime.GlobalExchangeApi.GetTickerHistory(market, strategy.IdealPeriod, minimumDate, endDate);
                    Global.Logger.Information("R Checking market {Market} lastCandleTime {a} - desiredLastCandleTime {b}", market, candles.Last().Timestamp, desiredLastCandleTime);
                }

                Global.Logger.Information("Checking market {Market}... lastCandleTime: {last} , close: {close}", market, candles.Last().Timestamp, candles.Last().Close);

                // We eliminate all candles that aren't needed for the dataset incl. the last one (if it's the current running candle).
                candles = candles.Where(x => x.Timestamp >= minimumDate && x.Timestamp < candleDate).ToList();

                // Not enough candles to perform what we need to do.
                if (candles.Count < strategy.MinimumAmountOfCandles)
                {
                    Global.Logger.Warning("Not enough candle data for {Market}...", market);
                    return new TradeSignal
                    {
                        TradeAdvice = TradeAdvice.Hold,
                        MarketName = market
                    };
                }

                // Get the date for the last candle.
                var signalDate = candles[candles.Count - 1].Timestamp;
                var strategySignalDate = strategy.GetSignalDate();

                if (Runtime.Configuration.ExchangeOptions.FirstOrDefault().IsSimulation)
                {
                    //TODO: improve to other timeframe
                    strategySignalDate = candleDate.AddMinutes(-30);
                }

                // This is an outdated candle...
                if (signalDate < strategySignalDate)
                {
                    Global.Logger.Information("Outdated candle for {Market}...", market);
                    return null;
                }

                // This calculates an advice for the next timestamp.
                var advice = strategy.Forecast(candles, Global.Logger);

                return new TradeSignal
                {
                    TradeAdvice = advice,
                    MarketName = market,
                    SignalCandle = strategy.GetSignalCandle(candles)
                };
            }
            catch (Exception ex)
            {
                // Couldn't get a buy signal for this market, no problem. Let's skip it.
                Global.Logger.Error(ex, "Couldn't get buy signal for {Market}...", market);
                return null;
            }
        }

        /// <summary>
        /// Calculates bid target between current ask price and last price.
        /// </summary>
        /// <param name="tick"></param>
        /// <param name="signalCandle"></param>
        /// <returns></returns>
        private decimal GetTargetBid(Ticker tick, Candle signalCandle)
        {
            if (Runtime.Configuration.TradeOptions.BuyInPriceStrategy == BuyInPriceStrategy.AskLastBalance)
            {
                // If the ask is below the last, we can get it on the cheap.
                if (tick.Ask < tick.Last) return tick.Ask;

                return tick.Ask + Runtime.Configuration.TradeOptions.AskLastBalance * (tick.Last - tick.Ask);
            }

            if (Runtime.Configuration.TradeOptions.BuyInPriceStrategy == BuyInPriceStrategy.SignalCandleClose)
            {
                return signalCandle.Close;
            }

            if (Runtime.Configuration.TradeOptions.BuyInPriceStrategy == BuyInPriceStrategy.MatchCurrentBid)
            {
                return tick.Bid;
            }

            return Math.Round(tick.Bid * (1 - Runtime.Configuration.TradeOptions.BuyInPricePercentage), 8);
        }

        /// <summary>
        /// Creates a new trade in our system and opens a buy order.
        /// </summary>
        /// <returns></returns>
        private async Task CreateNewTrade(TradeSignal signal, ITradingStrategy strategy)
        {
            decimal currentQuoteBalance = 9999;

            if (!Runtime.Configuration.TradeOptions.PaperTrade)
            {
                // Get our Bitcoin balance from the exchange
                var exchangeQuoteBalance = await Runtime.GlobalExchangeApi.GetBalance(signal.QuoteCurrency);

                // Check trading mode
                currentQuoteBalance = Runtime.Configuration.TradeOptions.PaperTrade ? 9999 : exchangeQuoteBalance.Available;
            }

            // Do we even have enough funds to invest?
            if (currentQuoteBalance < Runtime.Configuration.TradeOptions.AmountToInvestPerTrader)
            {
                Global.Logger.Warning("Insufficient funds ({Available}) to perform a {MarketName} trade. Skipping this trade.", currentQuoteBalance, signal.MarketName);
                return;
            }

            var order = await CreateBuyOrder(signal.MarketName, signal.SignalCandle, strategy);

            // We found a trade and have set it all up!
            if (order != null)
            {
                // Save the order.
                await Runtime.GlobalDataStore.SaveTradeAsync(order);

                // Send a notification that we found something suitable
                Global.Logger.Information("New trade signal {Market}...", order.Market);
            }
        }

        /// <summary>
        /// Creates a buy order on the exchange.
        /// </summary>
        /// <param name="pair">The pair we're buying</param>
        /// <param name="signalCandle"></param>
        /// <param name="strategy"></param>
        /// <returns></returns>
        private async Task<Trade> CreateBuyOrder(string pair, Candle signalCandle, ITradingStrategy strategy)
        {
            // Take the amount to invest per trader OR the current balance for this trader.

            //if (freeTrader.CurrentBalance < Runtime.Configuration.TradeOptions.AmountToInvestPerTrader || Runtime.Configuration.TradeOptions.ProfitStrategy == ProfitType.Reinvest)
            //    btcToSpend = freeTrader.CurrentBalance;
            //else
            var btcToSpend = Runtime.Configuration.TradeOptions.AmountToInvestPerTrader;

            // The amount here is an indication and will probably not be precisely what you get.
            var ticker = await Runtime.GlobalExchangeApi.GetTicker(pair);
            var openRate = GetTargetBid(ticker, signalCandle);
            var amount = btcToSpend / openRate;

            // Get the order ID, this is the most important because we need this to check
            // up on our trade. We update the data below later when the final data is present.
            var orderId = Runtime.Configuration.TradeOptions.PaperTrade ? GetOrderId() : await Runtime.GlobalExchangeApi.Buy(pair, amount, openRate);

            await SendNotification($"Buying #{pair} with limit {openRate:0.00000000} BTC ({amount:0.0000} units).");

            var fullApi = await Runtime.GlobalExchangeApi.GetFullApi();
            var trade = new Trade()
            {
                Market = pair,
                StakeAmount = btcToSpend,
                OpenRate = openRate,
                OpenDate = DateTime.UtcNow,
                Quantity = amount,
                OpenOrderId = orderId,
                BuyOrderId = orderId,
                IsOpen = true,
                IsBuying = true,
                StrategyUsed = strategy.Name,
                SellType = SellType.None,
                TickerLast = await Runtime.GlobalExchangeApi.GetTicker(pair),
                GlobalSymbol = await Runtime.GlobalExchangeApi.ExchangeCurrencyToGlobalCurrency(pair),
                Exchange = fullApi.Name,
                PaperTrade = Runtime.Configuration.TradeOptions.PaperTrade
            };

            if (Runtime.Configuration.TradeOptions.PlaceFirstStopAtSignalCandleLow)
            {
                trade.StopLossRate = signalCandle.Low;
                Global.Logger.Information("Automatic stop set at signal candle low {Low}", signalCandle.Low.ToString("0.00000000"));
            }

            return trade;
        }

        #endregion

        #region SELL SIDE

        public async Task UpdateExistingTrades()
        {
            // First we update our open buy orders by checking if they're filled.
            await UpdateOpenBuyOrders();

            // Secondly we check if currently selling trades can be marked as sold if they're filled.
            await UpdateOpenSellOrders();

            // Third, our current trades need to be checked if one of these has hit its sell targets...
            if (!Runtime.Configuration.TradeOptions.OnlySellOnStrategySignals)
            {
                await CheckForSellConditions();
            }
        }

        /// <summary>
        /// Updates the buy orders by checking with the exchange what status they are currently.
        /// </summary>
        /// <returns></returns>
        private async Task UpdateOpenBuyOrders()
        {
            // This means its a buy trade that is waiting to get bought. See if we can update that first.
            var activeTrades = await Runtime.GlobalDataStore.GetActiveTradesAsync();
            foreach (var trade in activeTrades.Where(x => x.IsBuying))
            {
                // Check trading mode
                Global.Logger.Information("Checking {Market} BUY order @ {OpenRate}...", trade.Market, trade.OpenRate.ToString("0.00000000"));

                if (Runtime.Configuration.TradeOptions.PaperTrade)
                {
                    //in simulation mode we always fill..
                    if (Runtime.Configuration.ExchangeOptions.Last().IsSimulation)
                    {
                        trade.OpenOrderId = null;
                        trade.IsBuying = false;
                    }
                    else
                    {
                        // Papertrading
                        var candles = await Runtime.GlobalExchangeApi.GetTickerHistory(trade.Market, Period.Minute, 1);
                        var candle = candles.FirstOrDefault();

                        if (candle != null && (trade.OpenRate >= candle.High ||
                                               (trade.OpenRate >= candle.Low && trade.OpenRate <= candle.High) ||
                                               Runtime.GlobalOrderBehavior == OrderBehavior.AlwaysFill
                            ))
                        {
                            trade.OpenOrderId = null;
                            trade.IsBuying = false;
                        }
                    }
                }
                else
                {
                    // Livetrading
                    var exchangeOrder = await Runtime.GlobalExchangeApi.GetOrder(trade.BuyOrderId, trade.Market);

                    // if this order is filled, we can update our database.
                    if (exchangeOrder?.Status == OrderStatus.Filled)
                    {
                        trade.OpenOrderId = null;
                        trade.StakeAmount = exchangeOrder.OriginalQuantity * exchangeOrder.Price;
                        trade.Quantity = exchangeOrder.OriginalQuantity;
                        trade.OpenRate = exchangeOrder.Price;
                        trade.OpenDate = exchangeOrder.OrderDate;
                        trade.IsBuying = false;
                    }
                }

                Global.Logger.Information("{Market} BUY order filled @ {OpenRate}...", trade.Market, trade.OpenRate.ToString("0.00000000"));

                // If this is enabled we place a sell order as soon as our buy order got filled.
                if (Runtime.Configuration.TradeOptions.ImmediatelyPlaceSellOrder)
                {
                    var sellPrice = Math.Round(trade.OpenRate * (1 + Runtime.Configuration.TradeOptions.ImmediatelyPlaceSellOrderAtProfit), 8);
                    var orderId = Runtime.Configuration.TradeOptions.PaperTrade ? GetOrderId() : await Runtime.GlobalExchangeApi.Sell(trade.Market, trade.Quantity, sellPrice);

                    trade.CloseRate = sellPrice;
                    trade.OpenOrderId = orderId;
                    trade.SellOrderId = orderId;
                    trade.IsSelling = true;
                    trade.SellType = SellType.Immediate;

                    Global.Logger.Information("{Market} order placed @ {CloseRate}...", trade.Market, trade.CloseRate?.ToString("0.00000000"));
                }

                await Runtime.GlobalDataStore.SaveTradeAsync(trade);
            }
        }

        /// <summary>
        /// Updates the sell orders by checking with the exchange what status they are currently.
        /// </summary>
        /// <returns></returns>
        private async Task UpdateOpenSellOrders()
        {
            // There are trades that have an open order ID set & sell order id set
            // that means its a sell trade that is waiting to get sold. See if we can update that first.

            var activeTrades = await Runtime.GlobalDataStore.GetActiveTradesAsync();
            foreach (var order in activeTrades.Where(x => x.IsSelling))
            {
                // Check trading mode
                Global.Logger.Information("Checking {Market} SELL order @ {CloseRate}...", order.Market, order.CloseRate?.ToString("0.00000000"));

                if (Runtime.Configuration.TradeOptions.PaperTrade)
                {
                    //in simulation mode we always fill..
                    if (Runtime.Configuration.ExchangeOptions.Last().IsSimulation)
                    {
                        order.OpenOrderId = null;
                        order.IsOpen = false;
                        order.IsSelling = false;
                        order.CloseDate = DateTime.UtcNow;
                        order.CloseProfit = (order.CloseRate * order.Quantity) - order.StakeAmount;
                        order.CloseProfitPercentage = ((order.CloseRate * order.Quantity) - order.StakeAmount) / order.StakeAmount * 100;
                    }
                    else
                    {
                        var candles = await Runtime.GlobalExchangeApi.GetTickerHistory(order.Market, Period.Minute, 1);
                        var candle = candles.FirstOrDefault();

                        if (candle != null && (order.CloseRate <= candle.Low || (order.CloseRate >= candle.Low && order.CloseRate <= candle.High) || Runtime.GlobalOrderBehavior == OrderBehavior.AlwaysFill))
                        {
                            order.OpenOrderId = null;
                            order.IsOpen = false;
                            order.IsSelling = false;
                            order.CloseDate = DateTime.UtcNow;
                            order.CloseProfit = (order.CloseRate * order.Quantity) - order.StakeAmount;
                            order.CloseProfitPercentage = ((order.CloseRate * order.Quantity) - order.StakeAmount) / order.StakeAmount * 100;
                        }
                    }
                }
                else
                {
                    // Livetrading
                    var exchangeOrder = await Runtime.GlobalExchangeApi.GetOrder(order.SellOrderId, order.Market);

                    // if this order is filled, we can update our database.
                    if (exchangeOrder?.Status == OrderStatus.Filled)
                    {
                        order.OpenOrderId = null;
                        order.IsOpen = false;
                        order.IsSelling = false;
                        order.CloseDate = exchangeOrder.OrderDate;
                        order.CloseRate = exchangeOrder.Price;
                        order.CloseProfit = (exchangeOrder.Price * exchangeOrder.OriginalQuantity) - order.StakeAmount;
                        order.CloseProfitPercentage = ((exchangeOrder.Price * exchangeOrder.OriginalQuantity) - order.StakeAmount) / order.StakeAmount * 100;
                    }
                }

                await Runtime.GlobalDataStore.SaveTradeAsync(order);

                await SendNotification($"Selling #{order.Market} with limit {order.CloseRate:0.00000000} BTC (profit: ± {order.CloseProfitPercentage:0.00}%, {order.CloseProfit:0.00000000} BTC).");

            }
        }

        /// <summary>
        /// Checks the current active trades if they need to be sold.
        /// </summary>
        /// <returns></returns>
        private async Task CheckForSellConditions()
        {
            // There are trades that have no open order ID set & are still open.
            // that means its a trade that is waiting to get sold. See if we can update that first.

            // An open order currently not selling or being an immediate sell are checked for SL  etc.
            // Prioritize markets with high volume.
            var activeTrades = await Runtime.GlobalDataStore.GetActiveTradesAsync();
            foreach (var trade in activeTrades.Where(x => !x.IsSelling && !x.IsBuying && x.IsOpen))
            {
                // These are trades that are not being bought or sold at the moment so these need to be checked for sell conditions.
                var ticker = Runtime.GlobalExchangeApi.GetTicker(trade.Market).Result;
                var sellType = await ShouldSell(trade, ticker, DateTime.UtcNow);

                Global.Logger.Information("Checking {Market} sell conditions...", trade.Market);

                if (sellType == SellType.TrailingStopLossUpdated)
                {
                    // Update the stop loss for this trade, which was set in ShouldSell.
                    await Runtime.GlobalDataStore.SaveTradeAsync(trade);
                }
                else if (sellType != SellType.None)
                {
                    var orderId = Runtime.Configuration.TradeOptions.PaperTrade ? GetOrderId() : await Runtime.GlobalExchangeApi.Sell(trade.Market, trade.Quantity, ticker.Bid);

                    trade.CloseRate = trade.TickerLast.Bid;
                    trade.OpenOrderId = orderId;
                    trade.SellOrderId = orderId;
                    trade.SellType = sellType;
                    trade.IsSelling = true;

                    Global.Logger.Information("Selling {Market} ({SellType})...", trade.Market, sellType);

                    await Runtime.GlobalDataStore.SaveTradeAsync(trade);
                }
            }
        }

        /// <summary>
        /// Based on earlier trade and current price and configuration, decides whether bot should sell.
        /// </summary>
        /// <param name="trade"></param>
        /// <param name="ticker"></param>
        /// <param name="utcNow"></param>
        /// <returns>True if bot should sell at current rate.</returns>
        private async Task<SellType> ShouldSell(Trade trade, Ticker ticker, DateTime utcNow)
        {
            var currentProfit = (ticker.Bid - trade.OpenRate) / trade.OpenRate;

            Global.Logger.Information("Should sell {Market}? Profit: {Profit}%...", trade.Market, (currentProfit * 100).ToString("0.00"));

            var activeTrades = await Runtime.GlobalDataStore.GetActiveTradesAsync();
            var tradeToUpdate = activeTrades.FirstOrDefault(x => x.TradeId == trade.TradeId);
            if (tradeToUpdate != null)
            {
                tradeToUpdate.TickerLast = ticker;
                await Runtime.GlobalDataStore.SaveTradeAsync(tradeToUpdate);
            }

            // too much notification to slack/telegram! :-)
            //await SendNotification($"Update LastPrice for Trade {trade.TradeId}");

            // Let's not do a stoploss for now...
            if (currentProfit < Runtime.Configuration.TradeOptions.StopLossPercentage)
            {
                Global.Logger.Information("Stop loss hit: {StopLoss}%", Runtime.Configuration.TradeOptions.StopLossPercentage);
                return SellType.StopLoss;
            }

            // Only use ROI when no stoploss is set, because the stop loss
            // will be the anchor that sells when the trade falls below it.
            // This gives the trade room to rise further instead of selling directly.
            if (!trade.StopLossRate.HasValue)
            {
                // Check if time matches and current rate is above threshold
                foreach (var item in Runtime.Configuration.TradeOptions.ReturnOnInvestment)
                {
                    var timeDiff = (utcNow - trade.OpenDate).TotalSeconds / 60;

                    if (timeDiff > item.Duration && currentProfit > item.Profit)
                    {
                        Global.Logger.Information("Timer hit: {TimeDifference} mins, profit {Profit}%", timeDiff, item.Profit.ToString("0.00"));
                        return SellType.Timed;
                    }
                }
            }

            // Only run this when we're past our starting percentage for trailing stop.
            if (Runtime.Configuration.TradeOptions.EnableTrailingStop)
            {
                // If the current rate is below our current stoploss percentage, close the trade.
                if (trade.StopLossRate.HasValue && ticker.Bid < trade.StopLossRate.Value)
                    return SellType.TrailingStopLoss;

                // The new stop would be at a specific percentage above our starting point.
                var newStopRate = trade.OpenRate * (1 + (currentProfit - Runtime.Configuration.TradeOptions.TrailingStopPercentage));

                // Only update the trailing stop when its above our starting percentage and higher than the previous one.
                if (currentProfit > Runtime.Configuration.TradeOptions.TrailingStopStartingPercentage && (trade.StopLossRate < newStopRate || !trade.StopLossRate.HasValue))
                {
                    Global.Logger.Information("Trailing stop loss updated for {Market} from {StopLossRate} to {NewStopRate}", trade.Market, trade.StopLossRate?.ToString("0.00000000"), newStopRate.ToString("0.00000000"));

                    // The current profit percentage is high enough to create the trailing stop value.
                    // If we are getting our first stop loss raise, we set it to break even. From there the stop
                    // gets increased every given TrailingStopPercentage...
                    if (!trade.StopLossRate.HasValue)
                        trade.StopLossRate = trade.OpenRate;
                    else
                        trade.StopLossRate = Math.Round(newStopRate, 8);

                    return SellType.TrailingStopLossUpdated;
                }

                return SellType.None;
            }

            return SellType.None;
        }


        #endregion

        private static string GetOrderId()
        {
            return Guid.NewGuid().ToString().Replace("-", string.Empty);
        }

        private async Task SendNotification(string message)
        {
            if (Runtime.NotificationManagers != null)
                foreach (var notificationManager in Runtime.NotificationManagers)
                    await notificationManager.SendNotification(message);
        }
    }
}
