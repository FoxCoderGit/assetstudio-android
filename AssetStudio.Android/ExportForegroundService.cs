using Android.App;
using Android.Content;
using Android.OS;
using AssetStudio;
using AssetStudio.AndroidApp.Processing;
using Uri = Android.Net.Uri;

namespace AssetStudio.AndroidApp;

[Service(Exported = false, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class ExportForegroundService : Service
{
    public const string ActionStart = "com.codebyfox.assetstudio.dotnet.action.START_EXPORT";
    public const string ActionStop = "com.codebyfox.assetstudio.dotnet.action.STOP_EXPORT";
    public const string BroadcastAction = "com.codebyfox.assetstudio.dotnet.action.EXPORT_UPDATE";

    public const string ExtraTreeUri = "extra_tree_uri";
    public const string ExtraOnlyType = "extra_only_type";
    public const string ExtraImageFormat = "extra_image_format";
    public const string ExtraConvertAudioToWav = "extra_convert_audio_to_wav";

    public const string ExtraEventType = "extra_event_type";
    public const string ExtraMessage = "extra_message";
    public const string ExtraIsRunning = "extra_is_running";
    public const string ExtraOutputDirectory = "extra_output_directory";
    public const string ExtraLogFilePath = "extra_log_file_path";

    public const string EventStarted = "started";
    public const string EventProgress = "progress";
    public const string EventCompleted = "completed";
    public const string EventFailed = "failed";
    public const string EventCanceled = "canceled";

    private const string NotificationChannelId = "assetstudio_export_channel";
    private const int NotificationId = 4747;

    private static readonly object s_sync = new();
    public static bool IsRunning { get; private set; }

    private CancellationTokenSource? _cts;
    private DateTime _lastNotificationUpdateUtc = DateTime.MinValue;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action ?? ActionStart;
        if (action == ActionStop)
        {
            _cts?.Cancel();
            return StartCommandResult.NotSticky;
        }

        lock (s_sync)
        {
            if (IsRunning)
            {
                SendUpdate(EventProgress, "Export is already running.", true);
                return StartCommandResult.Sticky;
            }
            IsRunning = true;
        }

        try
        {
            var treeUriText = intent?.GetStringExtra(ExtraTreeUri);
            if (string.IsNullOrWhiteSpace(treeUriText))
            {
                SendUpdate(EventFailed, "Cannot start export: folder URI is missing.", false);
                lock (s_sync)
                {
                    IsRunning = false;
                }
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            var treeUri = Uri.Parse(treeUriText);
            if (treeUri == null)
            {
                SendUpdate(EventFailed, "Cannot start export: invalid folder URI.", false);
                lock (s_sync)
                {
                    IsRunning = false;
                }
                StopSelf();
                return StartCommandResult.NotSticky;
            }
            var options = ReadOptions(intent);

            EnsureNotificationChannel();
            var preparingNotification = BuildNotification(
                GetString(Resource.String.notification_title),
                GetString(Resource.String.notification_preparing),
                true);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                StartForeground(NotificationId, preparingNotification, Android.Content.PM.ForegroundService.TypeDataSync);
            }
            else
            {
                StartForeground(NotificationId, preparingNotification);
            }

            _cts = new CancellationTokenSource();
            _ = RunExportAsync(treeUri, options, _cts.Token);
            return StartCommandResult.Sticky;
        }
        catch (Exception ex)
        {
            SendUpdate(EventFailed, "Failed to start export: " + ex.Message, false);
            lock (s_sync)
            {
                IsRunning = false;
            }
            StopSelf();
            return StartCommandResult.NotSticky;
        }
    }

    public override void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnDestroy();
    }

    private async Task RunExportAsync(Uri treeUri, ExportOptions options, CancellationToken cancellationToken)
    {
        ExportSummary? summary = null;

        try
        {
            SendUpdate(EventStarted, "Export started in background.", true);
            UpdateForegroundNotification(GetString(Resource.String.notification_title), GetString(Resource.String.notification_running));

            var processor = new AssetStudioProcessor(this);
            summary = await processor.ProcessTreeAsync(
                treeUri,
                options,
                message => ReportLog(message),
                cancellationToken);

            var completedMessage = "Export completed. Exported=" + summary.ExportedObjects +
                                   ", failed=" + summary.FailedObjects +
                                   ", skipped=" + summary.SkippedObjects + ".";

            SendUpdate(EventCompleted, completedMessage, false, summary);
            ShowTerminalNotification(
                GetString(Resource.String.notification_done_title),
                GetString(Resource.String.notification_done_text));
        }
        catch (System.OperationCanceledException)
        {
            SendUpdate(EventCanceled, "Export canceled.", false, summary);
            ShowTerminalNotification(
                GetString(Resource.String.notification_canceled_title),
                GetString(Resource.String.notification_canceled_text));
        }
        catch (Exception ex)
        {
            SendUpdate(EventFailed, "Export failed: " + ex.Message, false, summary);
            ShowTerminalNotification(
                GetString(Resource.String.notification_failed_title),
                ex.Message);
        }
        finally
        {
            lock (s_sync)
            {
                IsRunning = false;
            }

            if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
            {
                StopForeground(StopForegroundFlags.Detach);
            }
            else
            {
                StopForeground(false);
            }

            StopSelf();
        }
    }

    private void ReportLog(string message)
    {
        SendUpdate(EventProgress, message, true);
        MaybeUpdateForegroundNotification(message);
    }

    private void MaybeUpdateForegroundNotification(string message)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastNotificationUpdateUtc).TotalMilliseconds < 1000)
        {
            return;
        }

        _lastNotificationUpdateUtc = now;
        UpdateForegroundNotification(
            GetString(Resource.String.notification_title),
            TruncateSingleLine(message, 100));
    }

    private void UpdateForegroundNotification(string title, string text)
    {
        var notification = BuildNotification(title, text, true);
        var manager = GetSystemService(NotificationService) as NotificationManager;
        manager?.Notify(NotificationId, notification);
    }

    private void ShowTerminalNotification(string title, string text)
    {
        var notification = BuildNotification(title, TruncateSingleLine(text, 120), false);
        var manager = GetSystemService(NotificationService) as NotificationManager;
        manager?.Notify(NotificationId, notification);
    }

    private Notification BuildNotification(string title, string text, bool ongoing)
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);

        var pendingFlags = PendingIntentFlags.UpdateCurrent;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            pendingFlags |= PendingIntentFlags.Immutable;
        }

        var pendingIntent = PendingIntent.GetActivity(this, 0, intent, pendingFlags);

        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, NotificationChannelId)
            : new Notification.Builder(this);

        builder
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetStyle(new Notification.BigTextStyle().BigText(text))
            .SetContentIntent(pendingIntent)
            .SetOnlyAlertOnce(true)
            .SetOngoing(ongoing);

        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            builder.SetPriority((int)NotificationPriority.Low);
        }

        return builder.Build();
    }

    private void EnsureNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            return;
        }

        var manager = GetSystemService(NotificationService) as NotificationManager;
        if (manager == null)
        {
            return;
        }

        var channel = manager.GetNotificationChannel(NotificationChannelId);
        if (channel != null)
        {
            return;
        }

        channel = new NotificationChannel(
            NotificationChannelId,
            GetString(Resource.String.notification_channel_name),
            NotificationImportance.Low)
        {
            Description = GetString(Resource.String.notification_channel_description)
        };
        manager.CreateNotificationChannel(channel);
    }

    private static ExportOptions ReadOptions(Intent? intent)
    {
        var onlyTypeRaw = intent?.GetIntExtra(ExtraOnlyType, -1) ?? -1;
        var imageFormatRaw = intent?.GetIntExtra(ExtraImageFormat, (int)ImageFormat.Png) ?? (int)ImageFormat.Png;
        var audioToWav = intent?.GetBooleanExtra(ExtraConvertAudioToWav, true) ?? true;

        ClassIDType? onlyType = null;
        if (onlyTypeRaw >= 0 && Enum.IsDefined(typeof(ClassIDType), onlyTypeRaw))
        {
            onlyType = (ClassIDType)onlyTypeRaw;
        }

        var imageFormat = ImageFormat.Png;
        if (Enum.IsDefined(typeof(ImageFormat), imageFormatRaw))
        {
            imageFormat = (ImageFormat)imageFormatRaw;
        }

        return new ExportOptions
        {
            OnlyType = onlyType,
            ImageFormat = imageFormat,
            ConvertAudioToWav = audioToWav
        };
    }

    private void SendUpdate(string eventType, string? message, bool isRunning, ExportSummary? summary = null)
    {
        var intent = new Intent(BroadcastAction);
        intent.SetPackage(PackageName);
        intent.PutExtra(ExtraEventType, eventType);
        intent.PutExtra(ExtraIsRunning, isRunning);

        if (!string.IsNullOrWhiteSpace(message))
        {
            intent.PutExtra(ExtraMessage, message);
        }

        if (summary != null)
        {
            intent.PutExtra(ExtraOutputDirectory, summary.OutputDirectory ?? string.Empty);
            intent.PutExtra(ExtraLogFilePath, summary.LogFilePath ?? string.Empty);
        }

        SendBroadcast(intent);
    }

    private static string TruncateSingleLine(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var singleLine = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        return singleLine[..Math.Max(0, maxLength - 3)] + "...";
    }
}
