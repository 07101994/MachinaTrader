using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Mynt.Core.Interfaces;

namespace MachinaTrader.Helpers
{
    public class SignalrNotificationManager : INotificationManager
    {
        public async Task<bool> SendNotification(string message)
        {
            await Runtime.GlobalHubTraders.Clients.All.SendAsync("Send", message);
            return true;
        }

        public async Task<bool> SendTemplatedNotification(string template, params object[] parameters)
        {
            var finalMessage = string.Format(template, parameters);
            return await SendNotification(finalMessage);
        }
    }
}
