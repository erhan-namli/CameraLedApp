using Avalonia.Media.Imaging;
using System;
using System.Device.Gpio;
using System.IO;
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
        void StartCameraService();
        void StopCameraService();
    }

    /// <summary>
    /// Real hardware service for Raspberry Pi
    /// </summary>
    public class RaspberryPiHardwareService : IHardwareService
    {
        private int _ledPin = 17;
        private GpioController? _gpioController;
        private bool _isLedOn = false;
        private readonly SemaphoreSlim _frameLock = new SemaphoreSlim(1, 1);
        private bool _isServiceRunning = false;
        private CancellationTokenSource? _serviceCts;
        private Task? _serviceTask;
        private bool _hasCameraTools = false;

        // Double-buffering for image files
        private readonly string _imagePathA;
        private readonly string _imagePathB;
        private string _currentReadPath;
        private string _currentWritePath;
        private bool _usingPathA = true;

        // Current frame as bitmap
        private Bitmap? _currentFrame;
        private DateTime _lastFrameTime = DateTime.MinValue;

        public RaspberryPiHardwareService()
        {
            // Create temp paths for double-buffering
            string tempDir = Path.Combine(Path.GetTempPath(), "camera_led_app");
            Directory.CreateDirectory(tempDir);

            _imagePathA = Path.Combine(tempDir, "camera_image_a.jpg");
            _imagePathB = Path.Combine(tempDir, "camera_image_b.jpg");
            _currentReadPath = _imagePathA;
            _currentWritePath = _imagePathB;

            // Clean up any existing temp files
            CleanupTempFiles();
        }

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

            // Check for camera tools during initialization
            CheckCameraTools();
        }

        private void CheckCameraTools()
        {
            _hasCameraTools = CheckIfCommandExists("libcamera-still") ||
                              CheckIfCommandExists("raspistill");

            if (!_hasCameraTools)
            {
                Console.WriteLine("Warning: No camera tools (libcamera-still or raspistill) found.");
            }
        }

        public void SetGpioPin(int pinNumber)
        {
            try
            {
                bool wasLedOn = _isLedOn;

                if (_gpioController != null && _gpioController.IsPinOpen(_ledPin))
                {
                    _gpioController.Write(_ledPin, PinValue.Low);
                    _gpioController.ClosePin(_ledPin);
                }

                _ledPin = pinNumber;
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

        public void StartCameraService()
        {
            if (_isServiceRunning)
                return;

            // Kill any existing camera processes
            KillExistingCameraProcesses();

            // Clean up temp files
            CleanupTempFiles();

            // Start the camera service
            _serviceCts = new CancellationTokenSource();
            _serviceTask = Task.Run(() => CameraServiceLoopAsync(_serviceCts.Token));
            _isServiceRunning = true;

            Console.WriteLine("Camera service started");
        }

        public void StopCameraService()
        {
            if (!_isServiceRunning)
                return;

            // Cancel the service task
            if (_serviceCts != null)
            {
                _serviceCts.Cancel();
                try
                {
                    _serviceTask?.Wait(1000);
                }
                catch { }
                _serviceCts.Dispose();
                _serviceCts = null;
            }

            // Kill any camera processes
            KillExistingCameraProcesses();

            // Clean up the current frame
            _frameLock.Wait();
            try
            {
                _currentFrame?.Dispose();
                _currentFrame = null;
            }
            finally
            {
                _frameLock.Release();
            }

            _isServiceRunning = false;
            Console.WriteLine("Camera service stopped");
        }

        private async Task CameraServiceLoopAsync(CancellationToken token)
        {
            try
            {
                int errorCount = 0;

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // Capture an image to the current write buffer
                        bool success = await CaptureImageToFileAsync(_currentWritePath, token);

                        if (success)
                        {
                            // Wait to ensure the file is fully written
                            await Task.Delay(50, token);

                            // Swap buffers
                            await _frameLock.WaitAsync(token);
                            try
                            {
                                // Swap read/write paths
                                SwapBuffers();

                                // Load the new frame
                                try
                                {
                                    // Dispose old frame if it exists
                                    _currentFrame?.Dispose();

                                    // Load new frame
                                    using FileStream fs = new FileStream(_currentReadPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                                    _currentFrame = new Bitmap(fs);
                                    _lastFrameTime = DateTime.Now;

                                    // Reset error count on success
                                    errorCount = 0;
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error loading frame: {ex.Message}");
                                    errorCount++;
                                }
                            }
                            finally
                            {
                                _frameLock.Release();
                            }
                        }
                        else
                        {
                            errorCount++;
                            Console.WriteLine($"Failed to capture image (error count: {errorCount})");
                        }

                        // If too many errors, take a longer break
                        if (errorCount > 5)
                        {
                            await Task.Delay(2000, token);
                            errorCount = 0;
                        }
                        else
                        {
                            // Normal delay between captures
                            await Task.Delay(200, token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // Rethrow to exit the loop
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error in camera service: {ex.Message}");
                        errorCount++;
                        await Task.Delay(500, token); // Delay on error
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when service is canceled
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error in camera service loop: {ex.Message}");
            }
        }

        private void SwapBuffers()
        {
            // Swap the read and write buffers
            _usingPathA = !_usingPathA;

            if (_usingPathA)
            {
                _currentReadPath = _imagePathA;
                _currentWritePath = _imagePathB;
            }
            else
            {
                _currentReadPath = _imagePathB;
                _currentWritePath = _imagePathA;
            }
        }

        public async Task<Bitmap?> CaptureImageAsync(CancellationToken token)
        {
            // Return null if camera service is not running
            if (!_isServiceRunning)
                return null;

            // Use a lock to prevent conflicts with buffer swapping
            if (!await _frameLock.WaitAsync(100, token))
                return null;

            try
            {
                // If we have a current frame that's recent enough, return a copy
                if (_currentFrame != null && (DateTime.Now - _lastFrameTime).TotalSeconds < 2)
                {
                    try
                    {
                        // Create a copy of the bitmap to avoid disposal issues
                        using MemoryStream ms = new MemoryStream();
                        _currentFrame.Save(ms);
                        ms.Position = 0;
                        return new Bitmap(ms);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error copying frame: {ex.Message}");
                    }
                }

                return null;
            }
            finally
            {
                _frameLock.Release();
            }
        }

        private async Task<bool> CaptureImageToFileAsync(string outputPath, CancellationToken token)
        {
            Process? process = null;

            try
            {
                // Try libcamera-still first
                if (CheckIfCommandExists("libcamera-still"))
                {
                    process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "libcamera-still",
                            Arguments = $"-o {outputPath} --nopreview --immediate -t 1 --width 800 --height 600 --quality 85",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };
                }
                // Fallback to raspistill
                else if (CheckIfCommandExists("raspistill"))
                {
                    process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "raspistill",
                            Arguments = $"-o {outputPath} -t 1 -w 800 -h 600 -q 85 -n",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };
                }
                else
                {
                    // No camera tools available
                    return false;
                }

                // Start the process
                process.Start();

                // Wait with timeout to prevent hanging
                var waitTask = process.WaitForExitAsync(token);
                var timeoutTask = Task.Delay(3000, token);

                var completedTask = await Task.WhenAny(waitTask, timeoutTask);

                if (completedTask == timeoutTask && !process.HasExited)
                {
                    process.Kill();
                    Console.WriteLine("Camera process killed due to timeout");
                    return false;
                }

                // Check if process completed successfully
                return process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error capturing to file: {ex.Message}");
                return false;
            }
            finally
            {
                process?.Dispose();
            }
        }

        public async Task<bool> IsCameraAvailableAsync()
        {
            try
            {
                // First check if camera tools are installed
                if (!_hasCameraTools)
                    return false;

                // Check if camera hardware is available using v4l2-ctl if available
                if (CheckIfCommandExists("v4l2-ctl"))
                {
                    if (!await CheckForCamerasWithV4l2Async())
                    {
                        Console.WriteLine("No cameras detected with v4l2-ctl");
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking camera: {ex.Message}");
                return false;
            }
        }

        private bool CheckIfCommandExists(string command)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = command,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> CheckForCamerasWithV4l2Async()
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "v4l2-ctl",
                        Arguments = "--list-devices",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                // Check if any camera devices were listed
                return !string.IsNullOrWhiteSpace(output) &&
                       (output.Contains("/dev/video") || output.Contains("camera"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking for cameras: {ex.Message}");
                return false;
            }
        }

        private void KillExistingCameraProcesses()
        {
            try
            {
                // Kill existing camera processes
                Process.Start("pkill", "-f 'libcamera|raspistill'")?.WaitForExit(1000);
                Thread.Sleep(500);
            }
            catch { }
        }

        private void CleanupTempFiles()
        {
            try
            {
                if (File.Exists(_imagePathA))
                    File.Delete(_imagePathA);

                if (File.Exists(_imagePathB))
                    File.Delete(_imagePathB);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error cleaning up temp files: {ex.Message}");
            }
        }

        public void CleanupGpio()
        {
            // Stop camera service
            StopCameraService();

            try
            {
                if (_gpioController != null)
                {
                    _gpioController.Write(_ledPin, PinValue.Low);
                    _gpioController.ClosePin(_ledPin);
                    _gpioController.Dispose();
                }

                // Clean up temp files
                CleanupTempFiles();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during cleanup: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Mock hardware service for development on Windows
    /// </summary>
    public class MockHardwareService : IHardwareService
    {
        private int _ledPin = 17;
        private bool _ledState = false;
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

        public void StartCameraService()
        {
            Console.WriteLine("[MOCK] Camera service started");
        }

        public void StopCameraService()
        {
            Console.WriteLine("[MOCK] Camera service stopped");
        }

        public Task<Bitmap?> CaptureImageAsync(CancellationToken token)
        {
            return Task.FromResult<Bitmap?>(null);
        }

        public Task<bool> IsCameraAvailableAsync()
        {
            return Task.FromResult(_mockCameraAvailable);
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