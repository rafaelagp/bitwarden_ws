using System.Threading.Tasks;

namespace Bit.Core.Abstractions
{
    public interface INotificationService
    {
        Task InitAsync();
        Task UpdateConnectionAsync(bool sync = false);
        Task ReconnectFromActivityAsync();
        Task DisconnectFromInactivityAsync();
    }
}
