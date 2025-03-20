# Raspberry Pi Camera and LED Control Application

This repository contains a C# application for Raspberry Pi that demonstrates camera capture and GPIO control.

## Development Setup

### On Windows Development Machine

1. Install Visual Studio 2022 with the following workloads:

- .NET Desktop Development
- Universal Windows Platform development
- .NET Core cross-platform development

2. Install .NET 8 SDK from Microsoft's website

3. Install Avalonia UI extension:

- Open Visual Studio
- Go to Extensions → Manage Extensions
- Search for "Avalonia" and install "Avalonia for Visual Studio 2022"
- Restart Visual Studio

4. Clone this repository

```bash
git clone https://github.com/erhan-namli/CameraLedApp.git
cd CameraLedApp
```

5. Open the solution in Visual Studio 2022

- Open CameraLedApp.sln

### Required NuGet Packages

Make sure these NuGet packages are installed:

- Avalonia (core packages)
- Avalonia.Controls.ItemsRepeater
- Iot.Device.Bindings
- System.Device.Gpio
- SSH.NET (for deployment)

### Building the Application

#### Build from Visual Studio

1. Select the build configuration:

- Use "Release" for production build
- Use "Debug" for development and testing


2. Select "ARM64" as the target platform
3. Build the solution:

- Select Build → Build Solution (or press F6)


4. The built application will be in:

- bin/Release/net8.0/linux-arm64/publish/ (for Release build)
- bin/Debug/net8.0/linux-arm64/publish/ (for Debug build)

#### Build from Command Line

1. Open a Command Prompt or PowerShell window

2. Navigate to the project directory:

```bash
cd path\to\CameraLedApp
```

3. Run the publish command:

```bash
dotnet publish -c Release -r linux-arm64 --self-contained true
```

4. The output will be in:

```bash
bin\Release\net8.0\linux-arm64\publish\
```

### Deployment to Raspberry Pi

1. Create a file named Deploy.ps1 with this content:

```bash

param(
    [string]$Hostname = "raspberrypi.local",
    [string]$Username = "pi",
    [string]$Password = "raspberry"
)

$publishPath = Join-Path (Get-Location) "bin\Release\net8.0\linux-arm64\publish"
$remoteDir = "/home/$Username/CameraLedApp"

# Test SSH connection
Write-Host "Testing SSH connection..."
ssh -o "ConnectTimeout=5" "$Username@$Hostname" "echo Connected"

# Create remote directory
ssh "$Username@$Hostname" "mkdir -p $remoteDir"

# Copy files
Write-Host "Copying files..."
scp -r "$publishPath\*" "$Username@$Hostname`:$remoteDir"

# Setup desktop shortcut
ssh "$Username@$Hostname" @"
echo '[Desktop Entry]
Name=Camera LED Demo
Comment=Raspberry Pi Camera and LED Control
Exec=dotnet $remoteDir/CameraLedApp.dll
Icon=camera
Terminal=false
Type=Application
Categories=Utility;' > ~/Desktop/camera-led-demo.desktop
chmod +x ~/Desktop/camera-led-demo.desktop
"@

Write-Host "Deployment complete!"

```

2. Run the script:

```bash
.\Deploy.ps1 -Hostname "raspberrypi.local" -Username "your-username" -Password "your-password"
```

### Raspberry Pi Setup

#### Install Required Dependencies

1. Update package lists:

```bash
sudo apt update
```

2. Install .NET runtime and dependencies:

```bash
sudo apt install -y curl libunwind8 gettext apt-transport-https
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 8.0
echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc
echo 'export PATH=$PATH:$HOME/.dotnet:$HOME/.dotnet/tools' >> ~/.bashrc
source ~/.bashrc
```

3. Install camera and GPIO libraries:

```bash
sudo apt install -y libgdiplus libgtk-3-dev libx11-dev
sudo apt install -y python3-rpi.gpio
sudo apt install -y libcamera-apps  # For newer camera stack
```

4. Enable camera interface:

```bash
sudo raspi-config nonint do_camera 0
```

### Running the Application

On the Raspberry Pi:

```
cd ~/CameraLedApp
dotnet CameraLedApp.dll
```