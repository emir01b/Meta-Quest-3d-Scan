# Meta3D Scanner - Setup Script
# Installs Python dependencies and verifies the environment

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Meta3D Scanner - Kurulum" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $projectRoot) { $projectRoot = (Get-Location).Path }
$serverDir = Join-Path $projectRoot "server"

# Check Python
Write-Host "[1/5] Python kontrolu..." -ForegroundColor Yellow
$python = Get-Command python -ErrorAction SilentlyContinue
if ($python) {
    $pyVersion = & python --version 2>&1
    Write-Host "  OK: $pyVersion" -ForegroundColor Green
} else {
    Write-Host "  HATA: Python bulunamadi!" -ForegroundColor Red
    exit 1
}

# Check CUDA
Write-Host "[2/5] NVIDIA GPU kontrolu..." -ForegroundColor Yellow
$nvidiaSmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($nvidiaSmi) {
    $gpuInfo = & nvidia-smi --query-gpu=name,memory.total --format=csv,noheader 2>&1
    Write-Host "  OK: $gpuInfo" -ForegroundColor Green
} else {
    Write-Host "  UYARI: nvidia-smi bulunamadi. CUDA gerekli!" -ForegroundColor Yellow
}

# Install Python packages
Write-Host "[3/5] Python paketleri yukleniyor..." -ForegroundColor Yellow
$reqFile = Join-Path $serverDir "requirements.txt"
if (Test-Path $reqFile) {
    & python -m pip install -r $reqFile --quiet
    Write-Host "  OK: Temel paketler yuklendi" -ForegroundColor Green
} else {
    Write-Host "  HATA: requirements.txt bulunamadi!" -ForegroundColor Red
}

# Check COLMAP
Write-Host "[4/5] COLMAP kontrolu..." -ForegroundColor Yellow
$colmap = Get-Command colmap -ErrorAction SilentlyContinue
if ($colmap) {
    Write-Host "  OK: COLMAP bulundu" -ForegroundColor Green
} else {
    Write-Host "  UYARI: COLMAP bulunamadi!" -ForegroundColor Yellow
    Write-Host "  Indirin: https://colmap.github.io/install.html" -ForegroundColor Yellow
    Write-Host "  Windows icin pre-built binary: https://github.com/colmap/colmap/releases" -ForegroundColor Yellow
    Write-Host "  Kurduktan sonra COLMAP_PATH environment variable'ini ayarlayin" -ForegroundColor Yellow
}

# Create data directories
Write-Host "[5/5] Dizinler olusturuluyor..." -ForegroundColor Yellow
$dataDirs = @(
    (Join-Path $projectRoot "data"),
    (Join-Path $projectRoot "data\sessions"),
    (Join-Path $projectRoot "data\exports"),
    (Join-Path $projectRoot "data\colmap_workspace")
)
foreach ($dir in $dataDirs) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}
Write-Host "  OK: Dizinler olusturuldu" -ForegroundColor Green

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Kurulum Tamamlandi!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Sonraki adimlar:" -ForegroundColor White
Write-Host "  1. COLMAP'i yukleyin (henuz yuklemediyseniz)" -ForegroundColor White
Write-Host "  2. Unity Hub'dan Unity 6 (veya 2022.3.58f1+) yukleyin" -ForegroundColor White
Write-Host "  3. Unity'de Meta XR SDK'yi yukleyin" -ForegroundColor White
Write-Host "  4. Sunucuyu baslatin: cd server && python main.py" -ForegroundColor White
Write-Host ""
