using Avalonia.Media.Imaging;
using System;
using System.Device.Gpio;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace CameraLedApp
{
    /// <summary>
    /// Interface for hardware services to allow mocking during development
    /// </summary>
    public interface IHardwareService
    {
        Task<Bitmap?> CaptureImageAsync(CancellationToken token);
        void InitializeGpio();
        void SetLedState(bool isOn);
        void CleanupGpio();

        void SetGpioPin(int pinNumber);
        Task<bool> IsCameraAvailableAsync();
    }

    /// <summary>
    /// Real hardware service for Raspberry Pi
    /// </summary>
    public class RaspberryPiHardwareService : IHardwareService
    {
        private int _ledPin = 17; // Define private field for LED pin
        private GpioController? _gpioController;
        private readonly string _tempImagePath = Path.Combine(Path.GetTempPath(), "camera_image.jpg");
        private bool _isLedOn = false;

        public void InitializeGpio()
        {
            try
            {
                _gpioController = new GpioController();
                _gpioController.OpenPin(_ledPin, PinMode.Output);
                _gpioController.Write(_ledPin, PinValue.Low);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing GPIO: {ex.Message}");
            }
        }

        public void SetGpioPin(int pinNumber)
        {
            try
            {
                // Save current LED state
                bool wasLedOn = _isLedOn;

                // Turn off LED on old pin
                if (_gpioController != null && _gpioController.IsPinOpen(_ledPin))
                {
                    _gpioController.Write(_ledPin, PinValue.Low);
                    _gpioController.ClosePin(_ledPin);
                }

                // Change pin
                _ledPin = pinNumber;

                // Initialize and restore LED state on new pin
                _gpioController?.OpenPin(_ledPin, PinMode.Output);
                SetLedState(wasLedOn);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error changing GPIO pin: {ex.Message}");
            }
        }

        public void SetLedState(bool isOn)
        {
            try
            {
                _isLedOn = isOn;
                _gpioController?.Write(_ledPin, isOn ? PinValue.High : PinValue.Low);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting LED state: {ex.Message}");
            }
        }


        public async Task<Bitmap?> CaptureImageAsync(CancellationToken token)
        {
            try
            {
                // Use libcamera-jpeg to capture an image
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "libcamera-jpeg",
                        Arguments = $"-o {_tempImagePath} -n -t 100 --width 800 --height 600 --quality 85",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                await process.WaitForExitAsync(token);

                if (token.IsCancellationRequested)
                    return null;

                if (File.Exists(_tempImagePath))
                {
                    using var fileStream = File.OpenRead(_tempImagePath);
                    return new Bitmap(fileStream);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error capturing image: {ex.Message}");
            }
            return null;
        }

        public async Task<bool> IsCameraAvailableAsync()
        {
            try
            {
                // A simple check: Try to run libcamera-hello with minimal args just to see if it works
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "libcamera-hello",
                        Arguments = "-t 1",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                await process.WaitForExitAsync();

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
        public void CleanupGpio()
        {
            try
            {
                if (_gpioController != null)
                {
                    _gpioController.Write(_ledPin, PinValue.Low); // Turn off LED
                    _gpioController.ClosePin(_ledPin);
                    _gpioController.Dispose();
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Mock hardware service for development on Windows
    /// </summary>
    public class MockHardwareService : IHardwareService
    {
        private int _ledPin = 17; // Define private field for LED pin
        private bool _ledState = false;
        private readonly string[] _mockCameraImages = {
        "mock_camera_1.jpg",
        "mock_camera_2.jpg",
        "mock_camera_3.jpg"
    };
        private int _currentImageIndex = 0;
        private bool _mockCameraAvailable = true;

        public void InitializeGpio()
        {
            Console.WriteLine("[MOCK] GPIO initialized");
        }

        public void SetGpioPin(int pinNumber)
        {
            _ledPin = pinNumber;
            Console.WriteLine($"[MOCK] GPIO pin changed to {_ledPin}");
        }

        public void SetLedState(bool isOn)
        {
            _ledState = isOn;
            Console.WriteLine($"[MOCK] LED on pin {_ledPin} turned {(_ledState ? "ON" : "OFF")}");
        }

        public async Task<Bitmap?> CaptureImageAsync(CancellationToken token)
        {
            try
            {
                // Simulate camera delay
                await Task.Delay(100, token);

                if (token.IsCancellationRequested || !_mockCameraAvailable)
                    return null;

                // Load a mock image
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = $"CameraLedApp.Assets.{_mockCameraImages[_currentImageIndex]}";

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    var bitmap = new Bitmap(stream);

                    // Cycle through mock images
                    _currentImageIndex = (_currentImageIndex + 1) % _mockCameraImages.Length;

                    return bitmap;
                }

                // If mock images aren't found, create a simple colored bitmap
                var width = 800;
                var height = 600;

                using var memoryStream = new MemoryStream();
                // Code to create a simple colored bitmap would go here
                // For now just return null

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MOCK] Error simulating camera: {ex.Message}");
                return null;
            }
        }

        public Task<bool> IsCameraAvailableAsync()
        {
            // For testing, you can toggle this value
            return Task.FromResult(_mockCameraAvailable);

            // Alternatively, to simulate random camera disconnections (for testing):
            // Random random = new Random();
            // _mockCameraAvailable = random.Next(10) > 1; // 10% chance of "disconnection"
            // return Task.FromResult(_mockCameraAvailable);
        }

        public void CleanupGpio()
        {
            Console.WriteLine("[MOCK] GPIO cleaned up");
        }
    }

    public static class HardwareServiceFactory
    {
        public static IHardwareService Create()
        {
            // Use environment detection to decide which service to create
            bool isRaspberryPi = Environment.OSVersion.Platform == PlatformID.Unix;

            return isRaspberryPi
                ? new RaspberryPiHardwareService()
                : new MockHardwareService();
        }
    }
}