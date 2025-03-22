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
    /// Real hardware service for Raspberry Pi using direct MJPEG streaming
    /// </summary>
    public class RaspberryPiHardwareService : IHardwareService
    {
        private int _ledPin = 17;
        private GpioController? _gpioController;
        private bool _isLedOn = false;
        private readonly SemaphoreSlim _frameLock = new SemaphoreSlim(1, 1);
        private bool _isServiceRunning = false;
        private CancellationTokenSource? _serviceCts;
        private Task? _cameraTask;
        private Process? _cameraProcess;
        private Bitmap? _currentFrame;
        private readonly string _tempDir;
        private readonly string _mjpegPipePath;
        private readonly string _scriptPath;
        private int _frameCount = 0;
        private DateTime _lastFrameTime = DateTime.Now;
        private double _actualFps = 0;

        public RaspberryPiHardwareService()
        {
            // Setup temp directory
            _tempDir = Path.Combine(Path.GetTempPath(), "camera_led_app");
            Directory.CreateDirectory(_tempDir);

            // Setup file paths
            _mjpegPipePath = Path.Combine(_tempDir, "camera_stream.mjpeg");
            _scriptPath = Path.Combine(_tempDir, "camera_script.sh");

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

            // Create FIFO pipe
            CreateMjpegPipe();

            // Create streaming script
            CreateCameraScript();

            // Start the camera service
            _serviceCts = new CancellationTokenSource();

            // Start camera process first
            _cameraProcess = StartCameraProcess();

            // Wait a moment for camera to start
            Thread.Sleep(1000);

            // Start the frame reader task
            _cameraTask = Task.Run(() => ProcessMjpegStreamAsync(_serviceCts.Token));

            _isServiceRunning = true;
            Console.WriteLine("Camera service started");
        }

        public void StopCameraService()
        {
            if (!_isServiceRunning)
                return;

            Console.WriteLine("Stopping camera service...");

            // Cancel tasks
            if (_serviceCts != null)
            {
                _serviceCts.Cancel();
                try
                {
                    if (_cameraTask != null)
                        _cameraTask.Wait(1000);
                }
                catch { }
                _serviceCts.Dispose();
                _serviceCts = null;
            }

            // Kill process
            try
            {
                if (_cameraProcess != null && !_cameraProcess.HasExited)
                {
                    _cameraProcess.Kill();
                    _cameraProcess.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error killing processes: {ex.Message}");
            }

            _cameraProcess = null;

            // Kill any other camera processes
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

        private void CreateMjpegPipe()
        {
            try
            {
                // Remove existing pipe if it exists
                if (File.Exists(_mjpegPipePath))
                {
                    File.Delete(_mjpegPipePath);
                }

                // Create a FIFO pipe
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "mkfifo",
                    Arguments = _mjpegPipePath,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                process.Start();
                process.WaitForExit();

                Console.WriteLine($"Created MJPEG pipe at {_mjpegPipePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating MJPEG pipe: {ex.Message}");
            }
        }

        private void CreateCameraScript()
        {
            try
            {
                string scriptContent;

                // Check which camera tool to use
                if (CheckIfCommandExists("libcamera-vid"))
                {
                    // Script for libcamera-vid - direct MJPEG output
                    scriptContent = $@"#!/bin/bash
# Turn off display
export DISPLAY=
export WAYLAND_DISPLAY=
export QT_QPA_PLATFORM=offscreen

# Kill any existing camera processes
pkill -f 'libcamera|raspivid|ffmpeg|gst-launch' 2>/dev/null || true

# Start streaming - direct MJPEG output
libcamera-vid --nopreview -t 0 --width 800 --height 600 --framerate 30 --codec mjpeg -o {_mjpegPipePath}
";
                }
                else if (CheckIfCommandExists("raspivid") && CheckIfCommandExists("gst-launch-1.0"))
                {
                    // Script for raspivid + gstreamer
                    scriptContent = $@"#!/bin/bash
# Turn off display
export DISPLAY=
export WAYLAND_DISPLAY=

# Kill any existing camera processes
pkill -f 'libcamera|raspivid|ffmpeg|gst-launch' 2>/dev/null || true

# Start streaming - h264 to mjpeg via gstreamer
raspivid -n -t 0 -w 800 -h 600 -fps 30 -o - | \
  gst-launch-1.0 fdsrc ! h264parse ! avdec_h264 ! jpegenc ! multifilesink location={_mjpegPipePath}
";
                }
                else if (CheckIfCommandExists("raspistill"))
                {
                    // Fallback for raspistill - lower framerate but still works
                    scriptContent = $@"#!/bin/bash
# Turn off display
export DISPLAY=
export WAYLAND_DISPLAY=

# Kill any existing camera processes
pkill -f 'libcamera|raspivid|raspistill' 2>/dev/null || true

# Start streaming by continuously capturing images
while true; do
  raspistill -n -t 1 -w 800 -h 600 -q 85 -o {_mjpegPipePath}
  sleep 0.05
done
";
                }
                else
                {
                    // No camera tool available
                    scriptContent = @"#!/bin/bash
echo ""Error: No camera tools found"" >&2
exit 1
";
                }

                // Write the script with Unix line endings
                File.WriteAllText(_scriptPath, scriptContent.Replace("\r\n", "\n"));

                // Make it executable
                Process.Start("chmod", $"+x {_scriptPath}")?.WaitForExit();

                Console.WriteLine($"Created camera script at {_scriptPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating camera script: {ex.Message}");
            }
        }

        private Process? StartCameraProcess()
        {
            try
            {
                // Start the camera streaming process
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bash",
                        Arguments = _scriptPath,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine($"Camera process: {e.Data}");
                };

                process.Start();
                process.BeginErrorReadLine();

                Console.WriteLine("Started camera process");
                return process;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting camera process: {ex.Message}");
                return null;
            }
        }

        private async Task ProcessMjpegStreamAsync(CancellationToken token)
        {
            try
            {
                Console.WriteLine($"Opening MJPEG pipe for reading: {_mjpegPipePath}");

                // Open the MJPEG pipe
                using FileStream fileStream = new FileStream(_mjpegPipePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                Console.WriteLine("MJPEG pipe opened successfully");

                // Buffer for reading JPEG data
                using MemoryStream frameBuffer = new MemoryStream(1024 * 1024); // 1MB initial capacity
                byte[] buffer = new byte[32 * 1024]; // 32KB read buffer

                // JPEG markers
                byte[] jpegStartMarker = new byte[] { 0xFF, 0xD8 };
                byte[] jpegEndMarker = new byte[] { 0xFF, 0xD9 };

                bool inJpegFrame = false;
                int frameSize = 0;
                _frameCount = 0;

                DateTime fpsTimer = DateTime.Now;
                int fpsFrameCount = 0;

                int bytesRead;
                while (!token.IsCancellationRequested &&
                      (bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    int offset = 0;

                    while (offset < bytesRead)
                    {
                        if (!inJpegFrame)
                        {
                            // Look for JPEG start marker
                            if (offset <= bytesRead - 2 &&
                                buffer[offset] == jpegStartMarker[0] &&
                                buffer[offset + 1] == jpegStartMarker[1])
                            {
                                // Found start of JPEG
                                inJpegFrame = true;
                                frameBuffer.SetLength(0);
                                frameBuffer.Write(buffer, offset, 2);
                                offset += 2;
                                frameSize = 2;
                            }
                            else
                            {
                                offset++;
                            }
                        }
                        else
                        {
                            // We're in a JPEG frame
                            // Find how many bytes we can copy
                            int bytesToEnd = bytesRead - offset;

                            // Look for end marker within this buffer
                            int endMarkerPos = -1;
                            for (int i = offset; i <= bytesRead - 2; i++)
                            {
                                if (buffer[i] == jpegEndMarker[0] && buffer[i + 1] == jpegEndMarker[1])
                                {
                                    endMarkerPos = i;
                                    break;
                                }
                            }

                            if (endMarkerPos >= 0)
                            {
                                // Found end marker - copy up to and including it
                                int bytesToCopy = endMarkerPos - offset + 2;
                                frameBuffer.Write(buffer, offset, bytesToCopy);
                                frameSize += bytesToCopy;
                                offset += bytesToCopy;

                                // Process the complete frame
                                try
                                {
                                    ProcessJpegFrame(frameBuffer.ToArray(), frameSize);

                                    // Update frame count for FPS calculation
                                    _frameCount++;
                                    fpsFrameCount++;

                                    // Calculate FPS every second
                                    TimeSpan elapsed = DateTime.Now - fpsTimer;
                                    if (elapsed.TotalSeconds >= 1)
                                    {
                                        _actualFps = fpsFrameCount / elapsed.TotalSeconds;
                                        Console.WriteLine($"Camera: {_actualFps:F1} FPS ({fpsFrameCount} frames in {elapsed.TotalSeconds:F1}s)");
                                        fpsTimer = DateTime.Now;
                                        fpsFrameCount = 0;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error processing frame: {ex.Message}");
                                }

                                inJpegFrame = false;
                            }
                            else
                            {
                                // End marker not found - copy all bytes and continue
                                frameBuffer.Write(buffer, offset, bytesToEnd);
                                frameSize += bytesToEnd;
                                offset += bytesToEnd;

                                // Safety check to avoid memory issues with corrupted streams
                                if (frameSize > 5 * 1024 * 1024) // 5MB max frame size
                                {
                                    Console.WriteLine("Abnormally large frame detected, resetting");
                                    inJpegFrame = false;
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when canceled
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing MJPEG stream: {ex.Message}");
            }
        }

        private void ProcessJpegFrame(byte[] frameData, int frameSize)
        {
            try
            {
                // Create bitmap from JPEG data
                using var ms = new MemoryStream(frameData, 0, frameSize);
                var bitmap = new Bitmap(ms);

                // Update the current frame
                _frameLock.Wait();
                try
                {
                    _currentFrame?.Dispose();
                    _currentFrame = bitmap;
                    _lastFrameTime = DateTime.Now;
                }
                finally
                {
                    _frameLock.Release();
                }
            }
            catch (Exception ex)
            {
                // Uncomment for detailed error logging
                // Console.WriteLine($"Error creating bitmap: {ex.Message}");
            }
        }

        public async Task<Bitmap?> CaptureImageAsync(CancellationToken token)
        {
            // Return null if camera service is not running
            if (!_isServiceRunning)
                return null;

            // Use a lock to prevent conflicts with frame processing
            if (!await _frameLock.WaitAsync(100, token))
                return null;

            try
            {
                // If we have a current frame, return a copy
                if (_currentFrame != null)
                {
                    try
                    {
                        // Create a copy of the bitmap
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

        public async Task<bool> IsCameraAvailableAsync()
        {
            try
            {
                // Check for camera tools
                bool hasLibcameraVid = CheckIfCommandExists("libcamera-vid");
                bool hasRaspivid = CheckIfCommandExists("raspivid");
                bool hasRaspistill = CheckIfCommandExists("raspistill");

                if (!hasLibcameraVid && !hasRaspivid && !hasRaspistill)
                {
                    Console.WriteLine("No camera tools found");
                    return false;
                }

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
                Process.Start("pkill", "-f 'libcamera|raspivid|raspistill|ffmpeg|gst-launch'")?.WaitForExit(1000);
                Thread.Sleep(500);
            }
            catch { }
        }

        private void CleanupTempFiles()
        {
            try
            {
                if (File.Exists(_mjpegPipePath))
                    File.Delete(_mjpegPipePath);

                if (File.Exists(_scriptPath))
                    File.Delete(_scriptPath);
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