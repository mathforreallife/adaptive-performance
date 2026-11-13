using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Gapo.App;

public sealed class Notifier : IDisposable
{
    private readonly DispatcherQueue dispatcher;
    private readonly bool registered;

    public Notifier(DispatcherQueue dispatcher)
    {
        this.dispatcher = dispatcher;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register();
            registered = true;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            registered = false;
        }
    }

    public event Action? Invoked;

    public bool IsAvailable => registered;

    public bool Show(string title, string body)
    {
        if (!registered)
        {
            return false;
        }

        try
        {
            var notification = new AppNotificationBuilder().AddText(title).AddText(body).BuildNotification();
            AppNotificationManager.Default.Show(notification);
            return notification.Id != 0;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<int> CountShownAsync() =>
        registered ? (await AppNotificationManager.Default.GetAllAsync()).Count : 0;

    public async Task RemoveAllAsync()
    {
        if (registered)
        {
            await AppNotificationManager.Default.RemoveAllAsync();
        }
    }

    public void Dispose()
    {
        if (registered)
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            AppNotificationManager.Default.Unregister();
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        dispatcher.TryEnqueue(() => Invoked?.Invoke());
}
