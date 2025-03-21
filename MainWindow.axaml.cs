using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CameraLedApp
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isCameraRunning = false;
        private IHardwareService _hardwareService;
        private TextBlock _noImageText;
        private Ellipse _cameraStatusIndicator;
        private TextBlock _cameraStatusText;
        private Timer? _cameraCheckTimer;
        private Timer? _frameUpdateTimer;
        private const int FRAME_UPDATE_INTERVAL = 200; // Update frames every 200ms

        public MainWindow()
        {
            InitializeComponent();
            _hardwareService = HardwareServiceFactory.Create();
            _hardwareService.InitializeGpio();

            // Find UI elements
            _noImageText = this.FindControl<TextBlock>("NoImageText");
            _cameraStatusIndicator = this.FindControl<Ellipse>("CameraStatusIndicator");
            _cameraStatusText = this.FindControl<TextBlock>("CameraStatusText");

            // Connect event handlers
            StartCameraButton.Click += StartCamera_Click;
            StopCameraButton.Click += StopCamera_Click;
            TurnOnLedButton.Click += TurnOnLed_Click;
            TurnOffLedButton.Click += TurnOffLed_Click;

            // Start camera status check timer
            _cameraCheckTimer = new Timer(CheckCameraStatus, null, 0, 2000); // Check every 2 seconds
        }

        private async void CheckCameraStatus(object? state)
        {
            bool isCameraAvailable = await _hardwareService.IsCameraAvailableAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (isCameraAvailable)
                {
                    _cameraStatusIndicator.Fill = new SolidColorBrush(Colors.Green);
                    _cameraStatusText.Text = "Camera: Connected";
                    StartCameraButton.IsEnabled = !_isCameraRunning;
                }
                else
                {
                    _cameraStatusIndicator.Fill = new SolidColorBrush(Colors.Red);
                    _cameraStatusText.Text = "Camera: Not Connected";

                    // If camera disconnects while running, stop camera
                    if (_isCameraRunning)
                    {
                        StopCamera();
                    }

                    StartCameraButton.IsEnabled = false;
                    StopCameraButton.IsEnabled = false;
                }
            });
        }

        private void StartCamera_Click(object sender, RoutedEventArgs e)
        {
            if (_isCameraRunning)
                return;

            _isCameraRunning = true;
            StartCameraButton.IsEnabled = false;
            StopCameraButton.IsEnabled = true;

            // Hide the placeholder text when starting camera
            _noImageText.IsVisible = false;

            // Start the camera service
            _hardwareService.StartCameraService();

            _cancellationTokenSource = new CancellationTokenSource();

            // Create a timer to update frames at regular intervals
            _frameUpdateTimer = new Timer(UpdateFrameCallback, null, 0, FRAME_UPDATE_INTERVAL);
        }

        private async void UpdateFrameCallback(object? state)
        {
            if (!_isCameraRunning || _cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested)
                return;

            try
            {
                // Get the latest image from the camera service
                var bitmap = await _hardwareService.CaptureImageAsync(_cancellationTokenSource.Token);

                if (_cancellationTokenSource.IsCancellationRequested)
                    return;

                // Update the UI on the UI thread
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        if (bitmap != null)
                        {
                            CameraImage.Source = bitmap;
                            _noImageText.IsVisible = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error displaying image: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating frame: {ex.Message}");
            }
        }

        private void StopCamera_Click(object sender, RoutedEventArgs e)
        {
            StopCamera();
            StartCameraButton.IsEnabled = true;
            StopCameraButton.IsEnabled = false;
        }

        private void StopCamera()
        {
            _isCameraRunning = false;

            // Stop the frame update timer
            _frameUpdateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _frameUpdateTimer?.Dispose();
            _frameUpdateTimer = null;

            // Cancel any ongoing operations
            _cancellationTokenSource?.Cancel();

            // Stop the camera service
            _hardwareService.StopCameraService();

            // Show the placeholder text when stopping the camera
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                CameraImage.Source = null;
                _noImageText.IsVisible = true;
            });
        }

        private void TurnOnLed_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _hardwareService.SetLedState(true);
                TurnOnLedButton.IsEnabled = false;
                TurnOffLedButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error turning on LED: {ex.Message}");
            }
        }

        private void TurnOffLed_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _hardwareService.SetLedState(false);
                TurnOnLedButton.IsEnabled = true;
                TurnOffLedButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error turning off LED: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            StopCamera();
            _hardwareService.CleanupGpio();
            _cameraCheckTimer?.Dispose();
            base.OnClosed(e);
        }
    }
}