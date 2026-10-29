using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AssetStudio;
using AssetStudio.AndroidApp.Processing;
using System.Linq;
using Uri = Android.Net.Uri;

namespace AssetStudio.AndroidApp;

[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true)]
public class MainActivity : Activity
{
    private const int OpenTreeRequestCode = 1042;
    private const int NotificationPermissionRequestCode = 1043;
    private const string PrefsName = "assetstudio_dotnet_prefs";
    private const string TreeUriKey = "tree_uri";

    private Button? _selectButton;
    private Button? _startButton;
    private TextView? _selectedFolderText;
    private TextView? _statusValueText;
    private TextView? _logText;
    private ProgressBar? _progress;
    private Spinner? _exportTypeSpinner;
    private Spinner? _imageFormatSpinner;
    private CheckBox? _audioToWavCheck;
    private ExportUpdateReceiver? _exportUpdateReceiver;

    private Uri? _selectedTreeUri;
    private bool _isBusy;
    private bool _receiverRegistered;

    private static readonly (string Label, ClassIDType? Type)[] s_exportTypeOptions =
    {
        ("All Supported Types", null),
        ("Texture2D", ClassIDType.Texture2D),
        ("Sprite", ClassIDType.Sprite),
        ("AudioClip", ClassIDType.AudioClip),
        ("Font", ClassIDType.Font),
        ("Mesh", ClassIDType.Mesh),
        ("TextAsset", ClassIDType.TextAsset),
        ("Shader", ClassIDType.Shader),
        ("MovieTexture", ClassIDType.MovieTexture),
        ("VideoClip", ClassIDType.VideoClip),
        ("MonoBehaviour", ClassIDType.MonoBehaviour),
        ("Animator (FBX)", ClassIDType.Animator)
    };

    private static readonly ImageFormat[] s_imageFormatOptions =
    {
        ImageFormat.Png,
        ImageFormat.Tga,
        ImageFormat.Jpeg,
        ImageFormat.Bmp
    };

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _selectButton = FindViewById<Button>(Resource.Id.buttonSelectFolder);
        _startButton = FindViewById<Button>(Resource.Id.buttonStartExport);
        _selectedFolderText = FindViewById<TextView>(Resource.Id.textSelectedValue);
        _statusValueText = FindViewById<TextView>(Resource.Id.textStatusValue);
        _logText = FindViewById<TextView>(Resource.Id.textLog);
        _progress = FindViewById<ProgressBar>(Resource.Id.progressBar);
        _exportTypeSpinner = FindViewById<Spinner>(Resource.Id.spinnerExportType);
        _imageFormatSpinner = FindViewById<Spinner>(Resource.Id.spinnerImageFormat);
        _audioToWavCheck = FindViewById<CheckBox>(Resource.Id.checkAudioToWav);
        _exportUpdateReceiver = new ExportUpdateReceiver(this);

        _selectButton!.Click += (_, _) => OpenFolderPicker();
        _startButton!.Click += (_, _) => StartExport();

        ConfigureOptionControls();
        EnsureNotificationPermission();
        RestoreSelectedUri();

        _isBusy = ExportForegroundService.IsRunning;
        UpdateStatus(_isBusy ? GetString(Resource.String.status_running) : GetString(Resource.String.status_ready));
        UpdateUiState(_isBusy);
    }

    protected override void OnResume()
    {
        base.OnResume();
        RegisterExportReceiver();
    }

    protected override void OnPause()
    {
        UnregisterExportReceiver();
        base.OnPause();
    }

    private void RegisterExportReceiver()
    {
        if (_receiverRegistered || _exportUpdateReceiver == null)
        {
            return;
        }

        var filter = new IntentFilter(ExportForegroundService.BroadcastAction);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            RegisterReceiver(_exportUpdateReceiver, filter, ReceiverFlags.NotExported);
        }
        else
        {
            RegisterReceiver(_exportUpdateReceiver, filter);
        }

        _receiverRegistered = true;
    }

    private void UnregisterExportReceiver()
    {
        if (!_receiverRegistered || _exportUpdateReceiver == null)
        {
            return;
        }

        try
        {
            UnregisterReceiver(_exportUpdateReceiver);
        }
        catch
        {
            // Ignore repeated unregister calls.
        }

        _receiverRegistered = false;
    }

    private void ConfigureOptionControls()
    {
        if (_exportTypeSpinner != null)
        {
            var typeLabels = s_exportTypeOptions.Select(x => x.Label).ToArray();
            var adapter = new ArrayAdapter<string>(this, Resource.Layout.spinner_item, typeLabels);
            adapter.SetDropDownViewResource(Resource.Layout.spinner_dropdown_item);
            _exportTypeSpinner.Adapter = adapter;
            _exportTypeSpinner.SetSelection(0);
        }

        if (_imageFormatSpinner != null)
        {
            var imageLabels = s_imageFormatOptions.Select(x => x.ToString().ToUpperInvariant()).ToArray();
            var adapter = new ArrayAdapter<string>(this, Resource.Layout.spinner_item, imageLabels);
            adapter.SetDropDownViewResource(Resource.Layout.spinner_dropdown_item);
            _imageFormatSpinner.Adapter = adapter;
            _imageFormatSpinner.SetSelection(0);
        }

        if (_audioToWavCheck != null)
        {
            _audioToWavCheck.Checked = true;
        }
    }

    private ExportOptions BuildExportOptions()
    {
        var typeIndex = _exportTypeSpinner?.SelectedItemPosition ?? 0;
        if (typeIndex < 0 || typeIndex >= s_exportTypeOptions.Length)
        {
            typeIndex = 0;
        }

        var imageIndex = _imageFormatSpinner?.SelectedItemPosition ?? 0;
        if (imageIndex < 0 || imageIndex >= s_imageFormatOptions.Length)
        {
            imageIndex = 0;
        }

        return new ExportOptions
        {
            OnlyType = s_exportTypeOptions[typeIndex].Type,
            ImageFormat = s_imageFormatOptions[imageIndex],
            ConvertAudioToWav = _audioToWavCheck?.Checked ?? true
        };
    }

    private void OpenFolderPicker()
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        intent.AddFlags(ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);

        if (_selectedTreeUri != null && Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            intent.PutExtra(DocumentsContract.ExtraInitialUri, _selectedTreeUri);
        }

        StartActivityForResult(intent, OpenTreeRequestCode);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode != OpenTreeRequestCode)
        {
            return;
        }

        if (resultCode != Result.Ok || data?.Data == null)
        {
            AppendLog("Folder selection canceled.");
            return;
        }

        var treeUri = data.Data;
        var takeFlags = data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);

        try
        {
            ContentResolver?.TakePersistableUriPermission(treeUri, takeFlags);
        }
        catch (Exception ex)
        {
            AppendLog("Permission warning: " + ex.Message);
        }

        _selectedTreeUri = treeUri;
        SaveSelectedUri(treeUri);
        _selectedFolderText!.Text = treeUri.ToString();
        AppendLog("Folder selected: " + treeUri);
        UpdateUiState(_isBusy);
    }

    private void StartExport()
    {
        if (_selectedTreeUri == null)
        {
            AppendLog("Select a folder first.");
            return;
        }

        if (_isBusy)
        {
            AppendLog("Export is already running in background.");
            return;
        }

        try
        {
            EnsureNotificationPermission();

            var options = BuildExportOptions();
            AppendLog(
                "Options: type=" + (options.OnlyType?.ToString() ?? "All") +
                ", image=" + options.ImageFormat +
                ", audioToWav=" + options.ConvertAudioToWav);

            _isBusy = true;
            UpdateStatus(GetString(Resource.String.status_starting));
            UpdateUiState(true);

            StartExportService(_selectedTreeUri, options);
            AppendLog("Background export started. You can minimize the app.");
        }
        catch (Exception ex)
        {
            _isBusy = false;
            UpdateStatus(GetString(Resource.String.status_error));
            UpdateUiState(false);
            AppendLog("Error: " + ex.Message);
        }
    }

    private void EnsureNotificationPermission()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
        {
            return;
        }

        if (CheckSelfPermission(Manifest.Permission.PostNotifications) == Permission.Granted)
        {
            return;
        }

        RequestPermissions(new[] { Manifest.Permission.PostNotifications }, NotificationPermissionRequestCode);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[]? permissions, Permission[]? grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode != NotificationPermissionRequestCode)
        {
            return;
        }

        var granted = grantResults != null && grantResults.Length > 0 && grantResults[0] == Permission.Granted;
        AppendLog(granted ? "Notification permission granted." : "Notification permission denied.");
    }

    private void StartExportService(Uri treeUri, ExportOptions options)
    {
        var serviceIntent = new Intent(this, typeof(ExportForegroundService));
        serviceIntent.SetAction(ExportForegroundService.ActionStart);
        serviceIntent.PutExtra(ExportForegroundService.ExtraTreeUri, treeUri.ToString());
        serviceIntent.PutExtra(
            ExportForegroundService.ExtraOnlyType,
            options.OnlyType.HasValue ? (int)options.OnlyType.Value : -1);
        serviceIntent.PutExtra(ExportForegroundService.ExtraImageFormat, (int)options.ImageFormat);
        serviceIntent.PutExtra(ExportForegroundService.ExtraConvertAudioToWav, options.ConvertAudioToWav);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            StartForegroundService(serviceIntent);
        }
        else
        {
            StartService(serviceIntent);
        }
    }

    private void SaveSelectedUri(Uri uri)
    {
        var prefs = GetSharedPreferences(PrefsName, FileCreationMode.Private);
        prefs?.Edit()?.PutString(TreeUriKey, uri.ToString())?.Apply();
    }

    private void RestoreSelectedUri()
    {
        var prefs = GetSharedPreferences(PrefsName, FileCreationMode.Private);
        var uriText = prefs?.GetString(TreeUriKey, null);
        if (string.IsNullOrWhiteSpace(uriText))
        {
            _selectedFolderText!.Text = GetString(Resource.String.no_folder);
            return;
        }

        var parsedUri = Uri.Parse(uriText);
        if (parsedUri == null)
        {
            _selectedFolderText!.Text = GetString(Resource.String.no_folder);
            return;
        }

        _selectedTreeUri = parsedUri;
        _selectedFolderText!.Text = parsedUri.ToString();
        AppendLog("Restored previously selected folder.");
    }

    private void HandleExportUpdate(Intent intent)
    {
        var message = intent.GetStringExtra(ExportForegroundService.ExtraMessage);
        if (!string.IsNullOrWhiteSpace(message))
        {
            AppendLog(message);
        }

        var eventType = intent.GetStringExtra(ExportForegroundService.ExtraEventType) ?? string.Empty;
        _isBusy = intent.GetBooleanExtra(ExportForegroundService.ExtraIsRunning, _isBusy);

        switch (eventType)
        {
            case ExportForegroundService.EventStarted:
                UpdateStatus(GetString(Resource.String.status_running));
                break;
            case ExportForegroundService.EventProgress:
                UpdateStatus(GetString(Resource.String.status_running));
                break;
            case ExportForegroundService.EventCompleted:
                UpdateStatus(GetString(Resource.String.status_completed));
                break;
            case ExportForegroundService.EventCanceled:
                UpdateStatus(GetString(Resource.String.status_canceled));
                break;
            case ExportForegroundService.EventFailed:
                UpdateStatus(GetString(Resource.String.status_error));
                break;
        }

        var outputDir = intent.GetStringExtra(ExportForegroundService.ExtraOutputDirectory);
        if (!string.IsNullOrWhiteSpace(outputDir) && eventType == ExportForegroundService.EventCompleted)
        {
            AppendLog("Results: " + outputDir);
        }

        var logFile = intent.GetStringExtra(ExportForegroundService.ExtraLogFilePath);
        if (!string.IsNullOrWhiteSpace(logFile) &&
            (eventType == ExportForegroundService.EventCompleted || eventType == ExportForegroundService.EventFailed))
        {
            AppendLog("Log file: " + logFile);
        }

        UpdateUiState(_isBusy);
    }

    private void UpdateUiState(bool isBusy)
    {
        _selectButton!.Enabled = !isBusy;
        _startButton!.Enabled = !isBusy && _selectedTreeUri != null;
        if (_exportTypeSpinner != null) _exportTypeSpinner.Enabled = !isBusy;
        if (_imageFormatSpinner != null) _imageFormatSpinner.Enabled = !isBusy;
        if (_audioToWavCheck != null) _audioToWavCheck.Enabled = !isBusy;
        _progress!.Visibility = isBusy ? ViewStates.Visible : ViewStates.Gone;
    }

    private void UpdateStatus(string status)
    {
        if (_statusValueText == null)
        {
            return;
        }

        RunOnUiThread(() => _statusValueText.Text = status);
    }

    private void AppendLog(string message)
    {
        RunOnUiThread(() =>
        {
            if (_logText == null)
            {
                return;
            }

            var time = DateTime.Now.ToString("HH:mm:ss");
            _logText.Append($"[{time}] {message}\n");
        });
    }

    private sealed class ExportUpdateReceiver : BroadcastReceiver
    {
        private readonly WeakReference<MainActivity> _activityRef;

        public ExportUpdateReceiver(MainActivity activity)
        {
            _activityRef = new WeakReference<MainActivity>(activity);
        }

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent == null)
            {
                return;
            }

            if (_activityRef.TryGetTarget(out var activity))
            {
                activity.HandleExportUpdate(intent);
            }
        }
    }
}
