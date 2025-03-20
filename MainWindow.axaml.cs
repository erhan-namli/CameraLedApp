using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;
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

        private async void StartCamera_Click(object sender, RoutedEventArgs e)
        {
            if (_isCameraRunning)
                return;

            _isCameraRunning = true;
            StartCameraButton.IsEnabled = false;
            StopCameraButton.IsEnabled = true;

            // Hide the placeholder text when starting camera
            _noImageText.IsVisible = false;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            await Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && _isCameraRunning)
                {
                    try
                    {
                        // Capture image using the hardware service
                        var bitmap = await _hardwareService.CaptureImageAsync(token);

                        if (token.IsCancellationRequested)
                            break;

                        // Load and display the image
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

                        await Task.Delay(100, token); // Short delay between captures
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Camera error: {ex.Message}");
                        await Task.Delay(1000, token); // Longer delay if error occurs
                    }
                }
            }, token);
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
            _cancellationTokenSource?.Cancel();

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