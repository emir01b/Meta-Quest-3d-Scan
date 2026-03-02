"""
Meta3D Scanner - Configuration
"""
import os
from pathlib import Path
from pydantic import BaseModel

# Base directories
PROJECT_ROOT = Path(__file__).parent.parent
SERVER_DIR = Path(__file__).parent
DATA_DIR = PROJECT_ROOT / "data"
SESSIONS_DIR = DATA_DIR / "sessions"
EXPORTS_DIR = DATA_DIR / "exports"
COLMAP_DIR = DATA_DIR / "colmap_workspace"

# Ensure directories exist
for d in [DATA_DIR, SESSIONS_DIR, EXPORTS_DIR, COLMAP_DIR]:
    d.mkdir(parents=True, exist_ok=True)

# Server settings
SERVER_HOST = "0.0.0.0"
SERVER_PORT = 8765
WS_MAX_SIZE = 10 * 1024 * 1024  # 10MB max WebSocket message

# Camera settings (Meta Quest 3S Passthrough Camera)
CAMERA_WIDTH = 1280
CAMERA_HEIGHT = 960
CAMERA_FPS = 30

# Frame capture settings
FRAME_FORMAT = "png"  # png for lossless, jpg for smaller size
JPEG_QUALITY = 98  # When using JPEG (high quality for reconstruction)
MIN_FRAMES_FOR_RECONSTRUCTION = 30
MAX_FRAMES = 500

# Image quality thresholds
BLUR_THRESHOLD = 100.0  # Laplacian variance below this = blurry
MIN_BRIGHTNESS = 30  # Too dark
MAX_BRIGHTNESS = 225  # Too bright

# COLMAP settings
COLMAP_BINARY = os.environ.get("COLMAP_PATH", "colmap")
COLMAP_CAMERA_MODEL = "PINHOLE"  # Quest camera is well-calibrated
COLMAP_MAX_IMAGE_SIZE = 1280
SFM_MAX_NUM_FEATURES = 16384  # More features = better quality
MATCH_TYPE = "exhaustive"  # exhaustive for best quality, sequential for speed

# Reconstruction settings
POISSON_DEPTH = 11  # Higher = more detail (9-13 range)
POISSON_TRIM = 7.0  # Trim noisy areas
VOXEL_SIZE = 0.002  # 2mm voxel size for TSDF
MESH_SIMPLIFY_TARGET = 500000  # Target face count for simplified mesh

# Nerfstudio settings
NERFSTUDIO_METHOD = "splatfacto"  # Gaussian Splatting via Nerfstudio
NERFSTUDIO_MAX_STEPS = 30000  # Training iterations
NERFSTUDIO_EXPORT_FORMAT = "mesh"  # mesh, point_cloud, or gaussian_splat


class SessionInfo(BaseModel):
    """Information about a scanning session."""
    session_id: str
    name: str
    created_at: str
    frame_count: int = 0
    status: str = "capturing"  # capturing, processing, completed, failed
    reconstruction_method: str = "colmap_mvs"
    export_path: str | None = None


class FrameData(BaseModel):
    """Data received for each captured frame."""
    timestamp: float
    frame_index: int
    # Camera pose (4x4 matrix flattened)
    pose_matrix: list[float]
    # Camera intrinsics
    focal_length_x: float
    focal_length_y: float
    principal_point_x: float
    principal_point_y: float
    # Image dimensions
    width: int = CAMERA_WIDTH
    height: int = CAMERA_HEIGHT
    # Depth map included?
    has_depth: bool = False
    # Quality metrics
    blur_score: float = 0.0
    brightness: float = 0.0
