using System;
using System.Threading.Tasks;
using System.Timers;
using Bit.Core.Abstractions;
using Bit.Core.Enums;
using Bit.Core.Models.Response;
using Bit.Core.Utilities;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace Bit.Core.Services
{
    // https://github.com/bitwarden/jslib/blob/master/src/services/notifications.service.ts
    public class NotificationService : INotificationService
    {
        private const string HubMethodReceiveMessage = "ReceiveMessage";
        private const string HubMethodHeartbeat = "Heartbeat";

        private readonly IApiService _apiService;
        private readonly IAppIdService _appIdService;
        private readonly Func<bool, Task> _logoutCallbackAsync;
        private readonly ILogService _logService;
        private readonly ISyncService _syncService;
        private readonly IUserService _userService;
        private readonly IVaultTimeoutService _vaultTimeoutService;

        private bool _connected = false;
        private IEnvironmentService _environmentService;
        private bool _inited = false;
        private bool _inactive = false;
        private Timer _reconnectTimer;
        private HubConnection _signalrConnection;
        private string _url;

        public NotificationService(IUserService userService,
            ISyncService syncService, IAppIdService appIdService, IApiService apiService,
            IVaultTimeoutService vaultTimeoutService, Func<bool, Task> logoutCallbackAsync, 
            ILogService logService)
        {
            _userService = userService;
            _syncService = syncService;
            _appIdService = appIdService;
            _apiService = apiService;
            _vaultTimeoutService = vaultTimeoutService;
            _logoutCallbackAsync = logoutCallbackAsync;
            _logService = logService;
        }

        public async Task DisconnectFromInactivityAsync()
        {
            _inactive = true;
            if (_inited && _connected)
                await _signalrConnection.StopAsync();
        }

        public async Task InitAsync()
        {
            _inited = false;
            _url = "https://notifications.bitwarden.com";

            if (_environmentService == null)
                _environmentService = ServiceContainer.Resolve<IEnvironmentService>("");
            
            if (_environmentService.NotificationsUrl != null)
                _url = _environmentService.NotificationsUrl;
            else if (_environmentService.BaseUrl != null)
                _url = _environmentService.BaseUrl + "/notifications";

            // Set notifications server URL to `https://-` to effectively disable communication
            // with the notifications server from the client app
            if (_url == "https://-")
                return;

            if (_signalrConnection != null)
            {
                _signalrConnection.Closed -= OnClosedAsync;
                _signalrConnection.Remove(HubMethodReceiveMessage);
                _signalrConnection.Remove(HubMethodHeartbeat);
                await _signalrConnection.StopAsync();
                await _signalrConnection.DisposeAsync();
                _connected = false;
                _signalrConnection = null;
            }

            _signalrConnection = new HubConnectionBuilder()
                .WithUrl(_url + "/hub", options => new HttpConnectionOptions
                {
                    AccessTokenProvider = () => _apiService.GetActiveBearerTokenAsync(),
                    SkipNegotiation = true,
                    Transports = HttpTransportType.WebSockets
                })
                .AddMessagePackProtocol()
                //.ConfigureLogging(LogLevel.Trace)
                .Build();

            _signalrConnection.On<NotificationResponse>(HubMethodReceiveMessage, ProcessNotificationAsync);
            _signalrConnection.On(HubMethodHeartbeat, () => Console.WriteLine("Heartbeat!"));
            _signalrConnection.Closed += OnClosedAsync;
            _inited = true;
            if (await IsAuthedAndUnlockedAsync())
                await ReconnectAsync(false);
        }

        public async Task ReconnectFromActivityAsync()
        {
            _inactive = false;
            if (_inited && !_connected)
                await ReconnectAsync(true);
        }

        public async Task UpdateConnectionAsync(bool sync = false)
        {
            if (!_inited)
                return;

            try
            {
                if (await IsAuthedAndUnlockedAsync())
                    await ReconnectAsync(sync);
                else
                    await _signalrConnection.StopAsync();
            }
            catch (Exception e)
            {
                _logService.Error(e.Message);
            }
        }

        private async Task OnClosedAsync(Exception ex)
        {
            _connected = false;
            await ReconnectAsync(true);
        }

        private async Task ProcessNotificationAsync(NotificationResponse notification)
        {
            var appId = await _appIdService.GetAppIdAsync();
            if (notification == null || notification.ContextId == appId)
                return;

            var isAuthenticated = await _userService.IsAuthenticatedAsync();
            var myUserId = await _userService.GetUserIdAsync();
            if (!isAuthenticated || string.IsNullOrWhiteSpace(myUserId))
                return;

            switch (notification.Type)
            {
                case NotificationType.SyncCipherUpdate:
                case NotificationType.SyncCipherCreate:
                    var cipherCreateUpdateMessage = JsonConvert.DeserializeObject<SyncCipherNotification>(
                        notification.Payload);
                    if (isAuthenticated && cipherCreateUpdateMessage.UserId == myUserId)
                        await _syncService.SyncUpsertCipherAsync(cipherCreateUpdateMessage,
                            notification.Type == NotificationType.SyncCipherUpdate);
                    break;
                case NotificationType.SyncFolderUpdate:
                case NotificationType.SyncFolderCreate:
                    var folderCreateUpdateMessage = JsonConvert.DeserializeObject<SyncFolderNotification>(
                        notification.Payload);
                    if (isAuthenticated && folderCreateUpdateMessage.UserId == myUserId)
                        await _syncService.SyncUpsertFolderAsync(folderCreateUpdateMessage,
                            notification.Type == NotificationType.SyncFolderUpdate);
                    break;
                case NotificationType.SyncLoginDelete:
                case NotificationType.SyncCipherDelete:
                    var loginDeleteMessage = JsonConvert.DeserializeObject<SyncCipherNotification>(
                        notification.Payload);
                    if (isAuthenticated && loginDeleteMessage.UserId == myUserId)
                        await _syncService.SyncDeleteCipherAsync(loginDeleteMessage);
                    break;
                case NotificationType.SyncFolderDelete:
                    var folderDeleteMessage = JsonConvert.DeserializeObject<SyncFolderNotification>(
                        notification.Payload);
                    if (isAuthenticated && folderDeleteMessage.UserId == myUserId)
                        await _syncService.SyncDeleteFolderAsync(folderDeleteMessage);
                    break;
                case NotificationType.SyncCiphers:
                case NotificationType.SyncVault:
                case NotificationType.SyncSettings:
                    if (isAuthenticated)
                        await _syncService.FullSyncAsync(false);
                    break;
                case NotificationType.SyncOrgKeys:
                    if (isAuthenticated)
                    {
                        await _apiService.RefreshIdentityTokenAsync();
                        await _syncService.FullSyncAsync(true);
                    }
                    break;
                case NotificationType.LogOut:
                    if (isAuthenticated)
                        await _logoutCallbackAsync.Invoke(false);
                    break;
                default:
                    break;
            }
        }

        private async Task<bool> IsAuthedAndUnlockedAsync()
        {
            if (await _userService.IsAuthenticatedAsync())
            {
                var locked = await _vaultTimeoutService.IsLockedAsync();
                return !locked;
            }
            return false;
        }

        private async Task ReconnectAsync(bool sync = false)
        {
            if (_reconnectTimer != null)
            {
                _reconnectTimer.Elapsed -= OnReconnectTimerElapsedAsync;
                _reconnectTimer.Stop();
                _reconnectTimer = null;
            }

            if (_connected || !_inited || _inactive)
                return;

            var authedAndUnlocked = await IsAuthedAndUnlockedAsync();
            if (!authedAndUnlocked)
                return;

            try
            {
                await _signalrConnection.StartAsync();
                _connected = true;
                if (sync)
                    await _syncService.FullSyncAsync(false);
            }
            catch (Exception e)
            {
                _logService.Error(e.Message);
            }

            if (_connected)
                return;

            _reconnectTimer = new Timer(300000)
            {
                AutoReset = false,
                Enabled = true
            };
            _reconnectTimer.Elapsed += OnReconnectTimerElapsedAsync;
            _reconnectTimer.Start();
        }

        private async void OnReconnectTimerElapsedAsync(object sender, ElapsedEventArgs e)
            => await ReconnectAsync(true);
    }
}
