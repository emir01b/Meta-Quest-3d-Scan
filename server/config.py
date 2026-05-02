"""
MetaScan — Server Configuration
All server settings, paths, and pipeline parameters.
"""
import os
from dataclasses import dataclass, field
from pathlib import Path

# =============================================================================
# Server Settings
# =============================================================================
SERVER_HOST = "0.0.0.0"
SERVER_PORT = 8765

# =============================================================================
# Directory Structure
# =============================================================================
BASE_DIR = Path(__file__).parent
DATA_DIR = BASE_DIR / "data"
SESSIONS_DIR = DATA_DIR / "sessions"
EXPORTS_DIR = DATA_DIR / "exports"

# Create directories
for d in [DATA_DIR, SESSIONS_DIR, EXPORTS_DIR]:
    d.mkdir(parents=True, exist_ok=True)

# =============================================================================
# Camera Settings (Quest 3S Passthrough)
# =============================================================================
CAMERA_WIDTH = 1280
CAMERA_HEIGHT = 960
CAMERA_FPS = 30

# =============================================================================
# Frame Quality Thresholds
# =============================================================================
BLUR_THRESHOLD = 100.0        # Laplacian variance — below = blurry
EXPOSURE_MIN = 30             # Mean pixel value min
EXPOSURE_MAX = 230            # Mean pixel value max
MIN_OVERLAP_RATIO = 0.3       # Minimum overlap between consecutive frames

# =============================================================================
# Reconstruction Settings
# =============================================================================
MIN_FRAMES_FOR_RECONSTRUCTION = 20
MAX_FRAMES_FOR_RECONSTRUCTION = 500

# COLMAP parameters
COLMAP_BINARY = os.environ.get("COLMAP_PATH", "colmap")
COLMAP_CAMERA_MODEL = "PINHOLE"
COLMAP_MATCHER = "sequential"
COLMAP_MAX_IMAGE_SIZE = 1280

# Open3D TSDF parameters
TSDF_VOXEL_LENGTH = 0.005     # 5mm voxel size
TSDF_SDF_TRUNC = 0.04         # 40mm truncation distance
TSDF_DEPTH_SCALE = 1000.0     # mm to m conversion
TSDF_DEPTH_MAX = 3.0          # Maximum depth in meters

# Mesh simplification
TARGET_FACE_COUNT = 100000     # Target face count for simplification

# =============================================================================
# Binary Frame Protocol
# =============================================================================
# Header format (all little-endian):
# [4 bytes] Magic: "MSF\x01"
# [4 bytes] Frame index (uint32)
# [8 bytes] Timestamp (float64, seconds)
# [4 bytes] Image data length (uint32)
# [4 bytes] Depth data length (uint32)
# [4 bytes] Pose data length (uint32)
# [4 bytes] Intrinsics data length (uint32)
# --- Total header: 32 bytes ---
# [N bytes] Image data (JPEG)
# [M bytes] Depth data (raw uint16, mm)
# [P bytes] Pose data (7 floats: px,py,pz,qx,qy,qz,qw = 28 bytes)
# [Q bytes] Intrinsics data (4 floats: fx,fy,cx,cy = 16 bytes)

FRAME_MAGIC = b"MSF\x01"
FRAME_HEADER_SIZE = 32


# =============================================================================
# Session Info
# =============================================================================
@dataclass
class SessionInfo:
    session_id: str
    frame_count: int = 0
    status: str = "ready"
    created_at: float = 0.0
    frames: list = field(default_factory=list)
