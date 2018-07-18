using ExchangeSharp;
using Mynt.Core.Enums;
using Mynt.Core.Interfaces;
using Mynt.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Mynt.Core.Exchanges
{
    public class ExchangeBacktestGdaxAPI : ExchangeAPI
    {
        public override string BaseUrl { get => "local"; set => throw new NotImplementedException(); }

        public override string Name => "ExchangeBacktestGdaxAPI";

        public async Task<IEnumerable<KeyValuePair<string, ExchangeTicker>>> GetTickersAsync()
        {
            var tickers = new List<KeyValuePair<string, ExchangeTicker>>();
            var listOfMakert = new List<string>();
            foreach (var child in obj)
            {
                string symbol = "".ToStringInvariant();
                var ticker = new ExchangeTicker()
                {

                };

                tickers.Add(new KeyValuePair<string, ExchangeTicker>(symbol, ticker));
            }
            return tickers;
        }

        public Task<IEnumerable<ExchangeOrderResult>> GetOpenOrderDetailsAsync(string symbol = null)
        {
        }

        public Task<ExchangeOrderResult> GetOrderDetailsAsync(string orderId, string symbol = null)
        {
        }

        public Task<ExchangeOrderBook> GetOrderBookAsync(string symbol, int maxCount = 100)
        {
        }

        public Task<ExchangeTicker> GetTickerAsync(string symbol)
        {
        }

        public Task<IEnumerable<MarketCandle>> GetCandlesAsync(string symbol, int periodSeconds, DateTime? startDate = null, DateTime? endDate = null, int? limit = null)
        {
        }

        public Task<ExchangeOrderResult> PlaceOrderAsync(ExchangeOrderRequest order)
        {
        }

        public Task<IEnumerable<ExchangeMarket>> GetSymbolsMetadataAsync()
        {
        }

        public virtual string GlobalSymbolToExchangeSymbol(string symbol)
        {
        }

        public virtual string ExchangeSymbolToGlobalSymbol(string symbol)
        {
        }
    }
}
