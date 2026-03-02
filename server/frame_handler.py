"""
Meta3D Scanner - Frame Handler
Processes and stores incoming frames from Quest 3S.
"""
import json
import time
import struct
import logging
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

from config import (
    FrameData, SESSIONS_DIR, FRAME_FORMAT, JPEG_QUALITY,
    BLUR_THRESHOLD, MIN_BRIGHTNESS, MAX_BRIGHTNESS,
    CAMERA_WIDTH, CAMERA_HEIGHT
)

logger = logging.getLogger("meta3d.frame_handler")


class FrameHandler:
    """Handles incoming frames from Quest 3S."""

    def __init__(self, session_id: str):
        self.session_id = session_id
        self.session_dir = SESSIONS_DIR / session_id
        self.images_dir = self.session_dir / "images"
        self.depth_dir = self.session_dir / "depth"
        self.metadata_dir = self.session_dir / "metadata"
        self.frame_count = 0
        self.frame_metadata_list: list[dict] = []

        # Create directories
        for d in [self.images_dir, self.depth_dir, self.metadata_dir]:
            d.mkdir(parents=True, exist_ok=True)

        logger.info(f"FrameHandler initialized for session: {session_id}")

    def process_frame(self, raw_data: bytes) -> dict:
        """
        Process a raw frame message from Quest.
        
        Binary protocol:
        [4 bytes: header_size (uint32)]
        [header_size bytes: JSON metadata]
        [remaining bytes: image data (JPEG/PNG)]
        [if has_depth: last width*height*2 bytes are depth map (uint16)]
        """
        try:
            # Parse header size
            header_size = struct.unpack("<I", raw_data[:4])[0]

            # Parse JSON metadata
            metadata_json = raw_data[4:4 + header_size].decode("utf-8")
            metadata = json.loads(metadata_json)
            frame_data = FrameData(**metadata)

            # Extract image data
            image_start = 4 + header_size
            if frame_data.has_depth:
                depth_size = frame_data.width * frame_data.height * 2
                image_bytes = raw_data[image_start:-depth_size]
                depth_bytes = raw_data[-depth_size:]
            else:
                image_bytes = raw_data[image_start:]
                depth_bytes = None

            # Decode image
            image_array = np.frombuffer(image_bytes, dtype=np.uint8)
            image = cv2.imdecode(image_array, cv2.IMREAD_COLOR)

            if image is None:
                return {"status": "error", "message": "Failed to decode image"}

            # Calculate quality metrics
            blur_score = self._calculate_blur(image)
            brightness = self._calculate_brightness(image)
            frame_data.blur_score = blur_score
            frame_data.brightness = brightness

            # Quality check
            quality_ok = True
            quality_issues = []
            if blur_score < BLUR_THRESHOLD:
                quality_issues.append(f"blurry ({blur_score:.1f})")
                quality_ok = False
            if brightness < MIN_BRIGHTNESS:
                quality_issues.append(f"too dark ({brightness:.1f})")
                quality_ok = False
            if brightness > MAX_BRIGHTNESS:
                quality_issues.append(f"too bright ({brightness:.1f})")
                quality_ok = False

            # Save frame (even if quality issues, let reconstruction decide)
            frame_name = f"frame_{self.frame_count:06d}"
            self._save_frame(frame_name, image, depth_bytes, frame_data)
            self.frame_count += 1

            result = {
                "status": "ok",
                "frame_index": self.frame_count - 1,
                "frame_name": frame_name,
                "quality_ok": quality_ok,
                "blur_score": blur_score,
                "brightness": brightness,
                "quality_issues": quality_issues,
                "total_frames": self.frame_count,
            }

            return result

        except Exception as e:
            logger.error(f"Error processing frame: {e}", exc_info=True)
            return {"status": "error", "message": str(e)}

    def process_frame_simple(self, image_bytes: bytes, metadata: dict) -> dict:
        """
        Simplified frame processing for testing or alternative protocols.
        Accepts raw image bytes and metadata dict separately.
        """
        try:
            frame_data = FrameData(**metadata)

            # Decode image
            image_array = np.frombuffer(image_bytes, dtype=np.uint8)
            image = cv2.imdecode(image_array, cv2.IMREAD_COLOR)

            if image is None:
                return {"status": "error", "message": "Failed to decode image"}

            # Quality metrics
            blur_score = self._calculate_blur(image)
            brightness = self._calculate_brightness(image)
            frame_data.blur_score = blur_score
            frame_data.brightness = brightness

            # Save
            frame_name = f"frame_{self.frame_count:06d}"
            self._save_frame(frame_name, image, None, frame_data)
            self.frame_count += 1

            return {
                "status": "ok",
                "frame_index": self.frame_count - 1,
                "frame_name": frame_name,
                "blur_score": blur_score,
                "brightness": brightness,
                "total_frames": self.frame_count,
            }

        except Exception as e:
            logger.error(f"Error processing frame: {e}", exc_info=True)
            return {"status": "error", "message": str(e)}

    def _save_frame(self, frame_name: str, image: np.ndarray,
                    depth_bytes: bytes | None, frame_data: FrameData):
        """Save frame image, depth map, and metadata."""
        # Save image
        if FRAME_FORMAT == "png":
            cv2.imwrite(str(self.images_dir / f"{frame_name}.png"), image)
        else:
            cv2.imwrite(
                str(self.images_dir / f"{frame_name}.jpg"), image,
                [cv2.IMWRITE_JPEG_QUALITY, JPEG_QUALITY]
            )

        # Save depth map if available
        if depth_bytes is not None:
            depth_map = np.frombuffer(depth_bytes, dtype=np.uint16).reshape(
                frame_data.height, frame_data.width
            )
            np.save(str(self.depth_dir / f"{frame_name}_depth.npy"), depth_map)

            # Also save as 16-bit PNG for visualization
            cv2.imwrite(
                str(self.depth_dir / f"{frame_name}_depth.png"), depth_map
            )

        # Save metadata
        metadata_dict = frame_data.model_dump()
        metadata_dict["frame_name"] = frame_name
        self.frame_metadata_list.append(metadata_dict)

        with open(self.metadata_dir / f"{frame_name}.json", "w") as f:
            json.dump(metadata_dict, f, indent=2)

    def save_session_metadata(self):
        """Save complete session metadata for reconstruction."""
        session_meta = {
            "session_id": self.session_id,
            "total_frames": self.frame_count,
            "timestamp": time.time(),
            "camera_width": CAMERA_WIDTH,
            "camera_height": CAMERA_HEIGHT,
            "frames": self.frame_metadata_list,
        }
        with open(self.session_dir / "session_metadata.json", "w") as f:
            json.dump(session_meta, f, indent=2)
        logger.info(f"Session metadata saved: {self.frame_count} frames")

    def prepare_colmap_input(self):
        """
        Prepare COLMAP-compatible input from captured data.
        Creates cameras.txt and images.txt for known camera poses.
        """
        colmap_dir = self.session_dir / "colmap"
        sparse_dir = colmap_dir / "sparse" / "0"
        sparse_dir.mkdir(parents=True, exist_ok=True)

        if not self.frame_metadata_list:
            # Load from saved files
            for meta_file in sorted(self.metadata_dir.glob("*.json")):
                with open(meta_file) as f:
                    self.frame_metadata_list.append(json.load(f))

        if not self.frame_metadata_list:
            logger.error("No frame metadata found")
            return

        # Write cameras.txt (single camera model for Quest)
        first_frame = self.frame_metadata_list[0]
        cameras_content = (
            "# Camera list with one line of data per camera:\n"
            "# CAMERA_ID, MODEL, WIDTH, HEIGHT, PARAMS[]\n"
            f"1 PINHOLE {first_frame['width']} {first_frame['height']} "
            f"{first_frame['focal_length_x']} {first_frame['focal_length_y']} "
            f"{first_frame['principal_point_x']} {first_frame['principal_point_y']}\n"
        )
        with open(sparse_dir / "cameras.txt", "w") as f:
            f.write(cameras_content)

        # Write images.txt (with known poses)
        images_lines = [
            "# Image list with two lines of data per image:\n",
            "# IMAGE_ID, QW, QX, QY, QZ, TX, TY, TZ, CAMERA_ID, NAME\n",
            "# POINTS2D[] as (X, Y, POINT3D_ID)\n",
        ]

        for i, frame_meta in enumerate(self.frame_metadata_list):
            pose = np.array(frame_meta["pose_matrix"]).reshape(4, 4)
            # Convert to COLMAP format (world-to-camera)
            R = pose[:3, :3].T
            t = -R @ pose[:3, 3]
            # Rotation matrix to quaternion
            qw, qx, qy, qz = self._rotation_matrix_to_quaternion(R)

            ext = FRAME_FORMAT
            frame_name = frame_meta["frame_name"]
            images_lines.append(
                f"{i + 1} {qw} {qx} {qy} {qz} {t[0]} {t[1]} {t[2]} "
                f"1 {frame_name}.{ext}\n"
            )
            images_lines.append("\n")  # Empty line for 2D points

        with open(sparse_dir / "images.txt", "w") as f:
            f.writelines(images_lines)

        # Write empty points3D.txt
        with open(sparse_dir / "points3D.txt", "w") as f:
            f.write("# 3D point list (empty - will be computed by COLMAP)\n")

        # Create symlink/copy images to expected location
        colmap_images = colmap_dir / "images"
        colmap_images.mkdir(exist_ok=True)

        import shutil
        for img_file in self.images_dir.glob(f"*.{FRAME_FORMAT}"):
            dest = colmap_images / img_file.name
            if not dest.exists():
                shutil.copy2(str(img_file), str(dest))

        logger.info(f"COLMAP input prepared at: {colmap_dir}")
        return colmap_dir

    @staticmethod
    def _calculate_blur(image: np.ndarray) -> float:
        """Calculate blur score using Laplacian variance. Higher = sharper."""
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        return cv2.Laplacian(gray, cv2.CV_64F).var()

    @staticmethod
    def _calculate_brightness(image: np.ndarray) -> float:
        """Calculate average brightness."""
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        return float(np.mean(gray))

    @staticmethod
    def _rotation_matrix_to_quaternion(R: np.ndarray) -> tuple:
        """Convert 3x3 rotation matrix to quaternion (w, x, y, z)."""
        trace = np.trace(R)
        if trace > 0:
            s = 0.5 / np.sqrt(trace + 1.0)
            w = 0.25 / s
            x = (R[2, 1] - R[1, 2]) * s
            y = (R[0, 2] - R[2, 0]) * s
            z = (R[1, 0] - R[0, 1]) * s
        elif R[0, 0] > R[1, 1] and R[0, 0] > R[2, 2]:
            s = 2.0 * np.sqrt(1.0 + R[0, 0] - R[1, 1] - R[2, 2])
            w = (R[2, 1] - R[1, 2]) / s
            x = 0.25 * s
            y = (R[0, 1] + R[1, 0]) / s
            z = (R[0, 2] + R[2, 0]) / s
        elif R[1, 1] > R[2, 2]:
            s = 2.0 * np.sqrt(1.0 + R[1, 1] - R[0, 0] - R[2, 2])
            w = (R[0, 2] - R[2, 0]) / s
            x = (R[0, 1] + R[1, 0]) / s
            y = 0.25 * s
            z = (R[1, 2] + R[2, 1]) / s
        else:
            s = 2.0 * np.sqrt(1.0 + R[2, 2] - R[0, 0] - R[1, 1])
            w = (R[1, 0] - R[0, 1]) / s
            x = (R[0, 2] + R[2, 0]) / s
            y = (R[1, 2] + R[2, 1]) / s
            z = 0.25 * s
        return (w, x, y, z)
