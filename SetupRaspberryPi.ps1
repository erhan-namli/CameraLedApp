# SetupRaspberryPi.ps1
param(
    [string]$Hostname = "raspberrypi.local",
    [string]$Username = "erhan",
    [string]$Password = "1234"
)

Write-Host "Raspberry Pi Setup Script" -ForegroundColor Cyan
Write-Host "=========================" -ForegroundColor Cyan
Write-Host "This script will install all required packages on your Raspberry Pi."
Write-Host "Target: $Username@$Hostname"
Write-Host ""

# Check SSH connection
Write-Host "Testing SSH connection..." -ForegroundColor Yellow
$testConnection = ssh -o "BatchMode=yes" -o "ConnectTimeout=5" "$Username@$Hostname" "echo Connection successful"
if ($LASTEXITCODE -ne 0) {
    Write-Host "SSH connection failed. Make sure SSH is enabled on your Pi and credentials are correct." -ForegroundColor Red
    Write-Host "You might need to run 'ssh-keygen -R $Hostname' if you've reinstalled your Raspberry Pi." -ForegroundColor Yellow
    exit 1
}

Write-Host "Connection successful!" -ForegroundColor Green
Write-Host ""

# Install required packages
Write-Host "Installing required packages..." -ForegroundColor Yellow

$setupCommands = @"
echo "Updating package lists..."
sudo apt update

echo "Installing dependencies for .NET..."
sudo apt install -y curl libunwind8 gettext apt-transport-https

echo "Installing libraries for camera access..."
sudo apt install -y libgdiplus libgtk-3-dev libx11-dev

echo "Enabling camera interface..."
sudo raspi-config nonint do_camera 0

echo "Installing GPIO libraries..."
sudo apt install -y python3-rpi.gpio

echo "Download and installing .NET 8..."
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 8.0
rm dotnet-install.sh

# Add .NET to PATH if not already added
if ! grep -q 'export DOTNET_ROOT=\$HOME/.dotnet' ~/.bashrc; then
    echo 'export DOTNET_ROOT=\$HOME/.dotnet' >> ~/.bashrc
    echo 'export PATH=\$PATH:\$HOME/.dotnet:\$HOME/.dotnet/tools' >> ~/.bashrc
    echo ".NET path added to .bashrc"
fi

echo "Installation complete!"
"@

Write-Host "Executing commands on Raspberry Pi..."
$result = ssh "$Username@$Hostname" "$setupCommands"
Write-Host $result

Write-Host ""
Write-Host "Setup completed successfully!" -ForegroundColor Green
Write-Host "You can now deploy and run your application on the Raspberry Pi." -ForegroundColor Green