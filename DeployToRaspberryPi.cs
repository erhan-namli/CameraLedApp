using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace CameraLedApp
{
    public class RaspberryPiDeployer
    {
        // Configuration
        public string Hostname { get; set; } = "raspberrypi.local"; // or IP address
        public string Username { get; set; } = "erhan"; // default Raspberry Pi username
        public string Password { get; set; } = "1234"; // default Raspberry Pi password
        private const string RemoteDirectory = "/home/pi/CameraLedApp";

        // Local build directory relative to project
        private readonly string _localBuildDir;

        public RaspberryPiDeployer(string projectDir)
        {
            _localBuildDir = Path.Combine(projectDir, "bin", "RaspberryPi", "net8.0", "linux-arm64", "publish");
        }

        public async Task<bool> DeployAsync()
        {
            try
            {
                // Step 1: Build the project for Raspberry Pi
                Console.WriteLine("Building project for Raspberry Pi...");
                if (!await BuildProjectAsync())
                {
                    Console.WriteLine("Build failed. Deployment aborted.");
                    return false;
                }

                // Step 2: Deploy via SSH/SFTP
                Console.WriteLine($"Connecting to Raspberry Pi at {Hostname}...");
                using var client = new SftpClient(Hostname, Username, Password);
                using var sshClient = new SshClient(Hostname, Username, Password);

                try
                {
                    client.Connect();
                    sshClient.Connect();

                    // Create remote directory if it doesn't exist
                    Console.WriteLine($"Creating remote directory {RemoteDirectory} if needed...");
                    sshClient.RunCommand($"mkdir -p {RemoteDirectory}");

                    // Upload files
                    Console.WriteLine("Uploading application files...");
                    UploadDirectory(client, _localBuildDir, RemoteDirectory);

                    // Set execute permissions
                    Console.WriteLine("Setting execute permissions...");
                    sshClient.RunCommand($"chmod +x {RemoteDirectory}/CameraLedApp");

                    // Create desktop shortcut
                    Console.WriteLine("Creating desktop shortcut...");
                    string shortcutCommand = $@"echo '[Desktop Entry]
Name=Camera LED Demo
Comment=Raspberry Pi Camera and LED Control Application
Exec=dotnet {RemoteDirectory}/CameraLedApp.dll
Icon=camera
Terminal=false
Type=Application
Categories=Utility;' > ~/Desktop/camera-led-demo.desktop";

                    sshClient.RunCommand(shortcutCommand);
                    sshClient.RunCommand("chmod +x ~/Desktop/camera-led-demo.desktop");

                    Console.WriteLine("Deployment completed successfully!");
                    return true;
                }
                finally
                {
                    if (client.IsConnected)
                        client.Disconnect();

                    if (sshClient.IsConnected)
                        sshClient.Disconnect();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Deployment failed: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> BuildProjectAsync()
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = "publish -c RaspberryPi -r linux-arm64 --self-contained true",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = false,
                        WorkingDirectory = Directory.GetCurrentDirectory()
                    }
                };

                process.Start();

                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"Build error: {error}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Build process error: {ex.Message}");
                return false;
            }
        }

        private void UploadDirectory(SftpClient client, string localPath, string remotePath)
        {
            var files = Directory.GetFiles(localPath);

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                var remoteFilePath = $"{remotePath}/{fileName}";

                using var fileStream = new FileStream(file, FileMode.Open);
                client.UploadFile(fileStream, remoteFilePath, true);
                Console.WriteLine($"Uploaded: {fileName}");
            }

            var directories = Directory.GetDirectories(localPath);
            foreach (var directory in directories)
            {
                var directoryName = Path.GetFileName(directory);
                var remoteDirectoryPath = $"{remotePath}/{directoryName}";

                try
                {
                    client.CreateDirectory(remoteDirectoryPath);
                }
                catch (SftpPathNotFoundException)
                {
                    // Directory already exists
                }

                UploadDirectory(client, directory, remoteDirectoryPath);
            }
        }
    }
}