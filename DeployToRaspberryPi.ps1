# DeployToRaspberryPi.ps1
param(
    [string]$ProjectPath = (Get-Location),
    [string]$Hostname = "raspberrypi.local",
    [string]$Username = "erhan",
    [string]$Password = "1234",
    [string]$RemoteDirectory = "/home/erhan/CameraLedApp"
)

Write-Host "Raspberry Pi Deployment Tool" -ForegroundColor Cyan
Write-Host "=========================" -ForegroundColor Cyan
Write-Host "Project path: $ProjectPath"
Write-Host "Target: $Username@$Hostname`:$RemoteDirectory"
Write-Host ""

# Ensure we're in the project directory
Set-Location $ProjectPath

# Build the application for Raspberry Pi
Write-Host "Building application for Raspberry Pi..." -ForegroundColor Yellow
$buildOutput = dotnet publish -c RaspberryPi -r linux-arm64 --self-contained true
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed! See errors above." -ForegroundColor Red
    exit 1
}

Write-Host "Build successful!" -ForegroundColor Green

# Path to published files (from RaspberryPi directory)
$publishPath = Join-Path $ProjectPath "bin\RaspberryPi\net8.0\linux-arm64\publish"
if (-not (Test-Path $publishPath)) {
    Write-Host "Published files not found at $publishPath" -ForegroundColor Red
    exit 1
}

# Deploy using SCP
Write-Host "Deploying to Raspberry Pi..." -ForegroundColor Yellow

# First, try connecting to verify SSH access
Write-Host "Testing SSH connection..."
$testConnection = ssh -o "BatchMode=yes" -o "ConnectTimeout=5" "$Username@$Hostname" "echo Connection successful"
if ($LASTEXITCODE -ne 0) {
    Write-Host "SSH connection failed. Make sure SSH is enabled on your Pi and credentials are correct." -ForegroundColor Red
    Write-Host "You might need to run 'ssh-keygen -R $Hostname' if you've reinstalled your Raspberry Pi." -ForegroundColor Yellow
    
    $proceed = Read-Host "Would you like to try deployment anyway? (y/n)"
    if ($proceed -ne "y") {
        exit 1
    }
}

# Create remote directory
ssh "$Username@$Hostname" "mkdir -p $RemoteDirectory"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to create remote directory." -ForegroundColor Red
    exit 1
}

# Copy files using SCP
Write-Host "Copying files... (this may take a while)"
scp -r "$publishPath\*" "$Username@$Hostname`:$RemoteDirectory"
if ($LASTEXITCODE -ne 0) {
    Write-Host "File transfer failed." -ForegroundColor Red
    exit 1
}

# Set execute permissions and create desktop shortcut
Write-Host "Setting up application on Raspberry Pi..." -ForegroundColor Yellow
$setupCommands = @"
chmod +x $RemoteDirectory/CameraLedApp
echo '[Desktop Entry]
Name=Camera LED Demo
Comment=Raspberry Pi Camera and LED Control Application
Exec=dotnet $RemoteDirectory/CameraLedApp.dll
Icon=camera
Terminal=false
Type=Application
Categories=Utility;' > ~/Desktop/camera-led-demo.desktop
chmod +x ~/Desktop/camera-led-demo.desktop
"@

ssh "$Username@$Hostname" "$setupCommands"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to set up application on Raspberry Pi." -ForegroundColor Red
    exit 1
}

Write-Host "Deployment completed successfully!" -ForegroundColor Green
Write-Host "You can now run the application on your Raspberry Pi by double-clicking the desktop shortcut" -ForegroundColor Green
Write-Host "or by running: dotnet $RemoteDirectory/CameraLedApp.dll" -ForegroundColor Green

# Offer to run the application
$runApp = Read-Host "Would you like to run the application now? (y/n)"
if ($runApp -eq "y") {
    Write-Host "Starting the application..." -ForegroundColor Yellow
    ssh -t "$Username@$Hostname" "cd $RemoteDirectory && dotnet $RemoteDirectory/CameraLedApp.dll"
}