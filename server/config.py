"""Runtime configuration. Frame geometry comes from validated capture metadata."""

from pathlib import Path

SERVER_HOST = "0.0.0.0"
SERVER_PORT = 8765

BASE_DIR = Path(__file__).resolve().parent
DATA_DIR = BASE_DIR / "data"
SESSIONS_DIR = DATA_DIR / "sessions"
EXPORTS_DIR = DATA_DIR / "exports"
for path in (SESSIONS_DIR, EXPORTS_DIR):
    path.mkdir(parents=True, exist_ok=True)

# Diagnostic thresholds; real Quest captures are needed to tune them.
BLUR_THRESHOLD = 15.0
EXPOSURE_MIN = 30
EXPOSURE_MAX = 230

MIN_FRAMES_FOR_RECONSTRUCTION = 20
MAX_FRAMES_FOR_RECONSTRUCTION = 500

COLMAP_BINARY = str(BASE_DIR / "colmap_bin" / "COLMAP.bat")
