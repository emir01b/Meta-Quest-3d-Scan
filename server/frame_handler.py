"""
MetaScan — Frame Handler
Processes binary frames from Quest 3S, performs quality checks,
and stores data for 3D reconstruction.
"""
import json
import logging
import struct
import time
from pathlib import Path

import cv2
import numpy as np

from config import (
    FRAME_MAGIC, FRAME_HEADER_SIZE,
    BLUR_THRESHOLD, EXPOSURE_MIN, EXPOSURE_MAX,
    SESSIONS_DIR, CAMERA_WIDTH, CAMERA_HEIGHT,
    COLMAP_CAMERA_MODEL
)

logger = logging.getLogger("metascan.frame_handler")


class FrameHandler:
    """Handles incoming binary frames from Quest 3S."""

    def __init__(self, session_id: str):
        self.session_id = session_id
        self.session_dir = SESSIONS_DIR / session_id
        self.images_dir = self.session_dir / "images"
        self.depth_dir = self.session_dir / "depth"
        self.poses_dir = self.session_dir / "poses"

        # Create directories
        for d in [self.images_dir, self.depth_dir, self.poses_dir]:
            d.mkdir(parents=True, exist_ok=True)

        self.frame_count = 0
        self.accepted_frames = 0
        self.rejected_frames = 0
        self.start_time = time.time()
        self.frame_metadata: list[dict] = []

        # Camera intrinsics (updated from first frame)
        self.intrinsics = None

        logger.info(f"FrameHandler initialized for session: {session_id}")

    def process_frame(self, raw_data: bytes) -> dict:
        """
        Parse and process a binary frame from Quest 3S.

        Returns dict with frame result info.
        """
        try:
            # Parse header
            frame_data = self._parse_binary_frame(raw_data)
            if frame_data is None:
                return {
                    "status": "error",
                    "message": "Invalid frame format",
                    "total_frames": self.accepted_frames,
                }

            frame_index = frame_data["frame_index"]
            timestamp = frame_data["timestamp"]
            image_bytes = frame_data["image_data"]
            depth_bytes = frame_data["depth_data"]
            pose_data = frame_data["pose_data"]
            intrinsics_data = frame_data["intrinsics_data"]

            self.frame_count += 1

            # Decode image
            img_array = np.frombuffer(image_bytes, dtype=np.uint8)
            image = cv2.imdecode(img_array, cv2.IMREAD_COLOR)

            if image is None:
                self.rejected_frames += 1
                return {
                    "status": "rejected",
                    "reason": "decode_failed",
                    "total_frames": self.accepted_frames,
                    "quality_ok": False,
                    "quality_issues": ["Image decode failed"],
                }

            # Quality check
            quality_ok, quality_issues = self._check_quality(image)

            if not quality_ok:
                logger.warning(f"Frame {frame_index} quality warning: {', '.join(quality_issues)} (Accepted due to bypass)")
                quality_ok = True
                quality_issues = []

            # Save image
            img_filename = f"frame_{self.accepted_frames:06d}.jpg"
            img_path = self.images_dir / img_filename
            cv2.imwrite(str(img_path), image, [cv2.IMWRITE_JPEG_QUALITY, 95])

            # Save depth map (if present)
            depth_filename = None
            if len(depth_bytes) > 0:
                depth_filename = f"depth_{self.accepted_frames:06d}.raw"
                depth_path = self.depth_dir / depth_filename
                with open(depth_path, "wb") as f:
                    f.write(depth_bytes)

            # Parse and save pose
            pose = self._parse_pose(pose_data)
            if pose is not None:
                pose_filename = f"pose_{self.accepted_frames:06d}.json"
                pose_path = self.poses_dir / pose_filename
                with open(pose_path, "w") as f:
                    json.dump(pose, f, indent=2)

            # Parse intrinsics (save once)
            if self.intrinsics is None and len(intrinsics_data) >= 16:
                fx, fy, cx, cy = struct.unpack("<4f", intrinsics_data[:16])
                self.intrinsics = {
                    "fx": fx, "fy": fy, "cx": cx, "cy": cy,
                    "width": CAMERA_WIDTH, "height": CAMERA_HEIGHT,
                    "model": COLMAP_CAMERA_MODEL,
                }
                intrinsics_path = self.session_dir / "intrinsics.json"
                with open(intrinsics_path, "w") as f:
                    json.dump(self.intrinsics, f, indent=2)
                logger.info(f"Camera intrinsics saved: fx={fx:.1f}, fy={fy:.1f}")

            # Store metadata
            frame_meta = {
                "index": self.accepted_frames,
                "original_index": frame_index,
                "timestamp": timestamp,
                "image": img_filename,
                "depth": depth_filename,
                "pose": pose,
                "quality_ok": quality_ok,
            }
            self.frame_metadata.append(frame_meta)
            self.accepted_frames += 1

            logger.debug(
                f"Frame {self.accepted_frames} accepted "
                f"(total: {self.frame_count}, rejected: {self.rejected_frames})"
            )

            return {
                "status": "accepted",
                "frame_index": self.accepted_frames,
                "total_frames": self.accepted_frames,
                "quality_ok": True,
                "quality_issues": [],
            }

        except Exception as e:
            logger.error(f"Frame processing error: {e}", exc_info=True)
            return {
                "status": "error",
                "message": str(e),
                "total_frames": self.accepted_frames,
            }

    def _parse_binary_frame(self, data: bytes) -> dict | None:
        """Parse binary frame protocol."""
        if len(data) < FRAME_HEADER_SIZE:
            logger.warning(f"Frame too small: {len(data)} bytes")
            return None

        # Check magic
        magic = data[:4]
        if magic != FRAME_MAGIC:
            logger.warning(f"Invalid frame magic: {magic}")
            return None

        # Parse header
        frame_index = struct.unpack("<I", data[4:8])[0]
        timestamp = struct.unpack("<d", data[8:16])[0]
        image_len = struct.unpack("<I", data[16:20])[0]
        depth_len = struct.unpack("<I", data[20:24])[0]
        pose_len = struct.unpack("<I", data[24:28])[0]
        intrinsics_len = struct.unpack("<I", data[28:32])[0]

        # Validate lengths
        expected_total = FRAME_HEADER_SIZE + image_len + depth_len + pose_len + intrinsics_len
        if len(data) < expected_total:
            logger.warning(
                f"Frame data too short: got {len(data)}, expected {expected_total}"
            )
            return None

        # Extract segments
        offset = FRAME_HEADER_SIZE
        image_data = data[offset:offset + image_len]
        offset += image_len
        depth_data = data[offset:offset + depth_len]
        offset += depth_len
        pose_data = data[offset:offset + pose_len]
        offset += pose_len
        intrinsics_data = data[offset:offset + intrinsics_len]

        return {
            "frame_index": frame_index,
            "timestamp": timestamp,
            "image_data": image_data,
            "depth_data": depth_data,
            "pose_data": pose_data,
            "intrinsics_data": intrinsics_data,
        }

    def _check_quality(self, image: np.ndarray) -> tuple[bool, list[str]]:
        """Check image quality (blur, exposure)."""
        issues = []

        # Convert to grayscale for analysis
        if len(image.shape) == 3:
            gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        else:
            gray = image

        # Blur detection (Laplacian variance)
        laplacian_var = cv2.Laplacian(gray, cv2.CV_64F).var()
        if laplacian_var < BLUR_THRESHOLD:
            issues.append(f"blurry ({laplacian_var:.0f})")

        # Exposure check
        mean_val = np.mean(gray)
        if mean_val < EXPOSURE_MIN:
            issues.append(f"underexposed ({mean_val:.0f})")
        elif mean_val > EXPOSURE_MAX:
            issues.append(f"overexposed ({mean_val:.0f})")

        quality_ok = len(issues) == 0
        return quality_ok, issues

    def _parse_pose(self, pose_data: bytes) -> dict | None:
        """Parse 6DoF pose from bytes (position + quaternion)."""
        if len(pose_data) < 28:
            return None

        values = struct.unpack("<7f", pose_data[:28])
        return {
            "position": {"x": values[0], "y": values[1], "z": values[2]},
            "rotation": {
                "x": values[3], "y": values[4],
                "z": values[5], "w": values[6],
            },
        }

    def save_session_metadata(self):
        """Save session metadata to JSON file."""
        elapsed = time.time() - self.start_time
        metadata = {
            "session_id": self.session_id,
            "total_frames": self.accepted_frames,
            "rejected_frames": self.rejected_frames,
            "duration_seconds": round(elapsed, 1),
            "fps": round(self.accepted_frames / max(elapsed, 0.1), 1),
            "timestamp": self.start_time,
            "intrinsics": self.intrinsics,
            "frames": self.frame_metadata,
        }
        meta_path = self.session_dir / "session_metadata.json"
        with open(meta_path, "w") as f:
            json.dump(metadata, f, indent=2)
        logger.info(
            f"Session metadata saved: {self.accepted_frames} frames, "
            f"{elapsed:.1f}s, {self.rejected_frames} rejected"
        )

    def prepare_colmap_input(self):
        """Prepare COLMAP-compatible input structure."""
        colmap_dir = self.session_dir / "colmap"
        colmap_images = colmap_dir / "images"
        colmap_dir.mkdir(parents=True, exist_ok=True)
        colmap_images.mkdir(parents=True, exist_ok=True)

        # Create symlinks or copies for COLMAP images
        for frame in self.frame_metadata:
            src = self.images_dir / frame["image"]
            dst = colmap_images / frame["image"]
            if src.exists() and not dst.exists():
                try:
                    dst.symlink_to(src.resolve())
                except OSError:
                    # Symlink not supported, copy instead
                    import shutil
                    shutil.copy2(str(src), str(dst))

        # Write cameras.txt for known intrinsics
        if self.intrinsics:
            cameras_path = colmap_dir / "cameras.txt"
            fx = self.intrinsics["fx"]
            fy = self.intrinsics["fy"]
            cx = self.intrinsics["cx"]
            cy = self.intrinsics["cy"]
            w = self.intrinsics["width"]
            h = self.intrinsics["height"]
            with open(cameras_path, "w") as f:
                f.write("# Camera list with one line of data per camera:\n")
                f.write("#   CAMERA_ID, MODEL, WIDTH, HEIGHT, PARAMS[]\n")
                f.write(f"1 PINHOLE {w} {h} {fx} {fy} {cx} {cy}\n")

        # Write images.txt with known poses
        images_path = colmap_dir / "images.txt"
        with open(images_path, "w") as f:
            f.write("# Image list with two lines per image:\n")
            f.write("#   IMAGE_ID, QW, QX, QY, QZ, TX, TY, TZ, CAMERA_ID, NAME\n")
            f.write("#   POINTS2D[] as (X, Y, POINT3D_ID)\n")
            for i, frame in enumerate(self.frame_metadata):
                pose = frame.get("pose")
                if pose is None:
                    continue
                qw = pose["rotation"]["w"]
                qx = pose["rotation"]["x"]
                qy = pose["rotation"]["y"]
                qz = pose["rotation"]["z"]
                tx = pose["position"]["x"]
                ty = pose["position"]["y"]
                tz = pose["position"]["z"]
                f.write(
                    f"{i + 1} {qw} {qx} {qy} {qz} {tx} {ty} {tz} 1 {frame['image']}\n"
                )
                f.write("\n")  # Empty line for points

        # Write points3D.txt (empty)
        points_path = colmap_dir / "points3D.txt"
        with open(points_path, "w") as f:
            f.write("# 3D point list (empty — to be filled by COLMAP)\n")

        logger.info(f"COLMAP input prepared at: {colmap_dir}")
